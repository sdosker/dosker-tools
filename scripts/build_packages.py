"""Validates every package in Packages/ and builds the zips that VCC installs.

Writes <name>-<version>.zip and <name>-<version>.json to the output folder. The .json is the package
manifest with the download url and checksum, which build_listing.py turns into the VPM listing.

Usage: python scripts/build_packages.py [--out Dist] [--repo sdosker/dosker-tools]
"""
import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES_DIR = ROOT / "Packages"
PACKAGE_PREFIX = "com.dosker."

# Only for maintainers. Unity ignores folders ending in ~, so they never have .meta files
EXCLUDED_NAMES = {"CLAUDE.md", "CLAUDE.md.meta"}
SOURCE_SUFFIXES = {".cs", ".uss", ".asmdef"}
OLD_NAMES = re.compile(r"ShaderViewEditorUi")
# Zip entries need a fixed date so that the same sources always give the same file
ZIP_DATE = (2020, 1, 1, 0, 0, 0)


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def collect_files(package_dir):
    files = []
    for path in sorted(package_dir.rglob("*")):
        relative = path.relative_to(package_dir)
        if path.name in EXCLUDED_NAMES or any(part.endswith("~") for part in relative.parts):
            continue
        files.append(path)
    return files


def read_guid(meta_path):
    match = re.search(r"^guid: ([0-9a-f]{32})\s*$", meta_path.read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        fail(f"No GUID found in {meta_path}")
    return match.group(1)


def check_metas(package_dir, files, seen_guids):
    for path in files:
        if path.suffix == ".meta":
            if not path.with_suffix("").exists():
                fail(f"Orphan .meta: {path}")
            continue
        meta_path = path.with_name(path.name + ".meta")
        if not meta_path.is_file():
            fail(f"Missing {meta_path.name} in {package_dir.name}. Import the file in a Unity project and copy the .meta back.")
        guid = read_guid(meta_path)
        if guid in seen_guids:
            fail(f"Duplicate GUID {guid}: {path} and {seen_guids[guid]}")
        seen_guids[guid] = path


def check_sources(files):
    for path in files:
        if path.suffix in SOURCE_SUFFIXES and OLD_NAMES.search(path.read_text(encoding="utf-8")):
            fail(f"Old name left in {path}")


def check_versions(package_dir, version):
    changelog = (package_dir / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(r"^## \[(\d+\.\d+\.\d+)\]", changelog, re.MULTILINE)
    if not match or match.group(1) != version:
        fail(f"{package_dir.name}: package.json is at {version}, CHANGELOG.md at {match and match.group(1)}")

    version_file = package_dir / "Editor" / "CompressorUi.cs"
    if version_file.is_file():
        code = re.search(r'Version = "([^"]+)"', version_file.read_text(encoding="utf-8"))
        if not code or code.group(1) != version:
            fail(f"{package_dir.name}: package.json is at {version}, CompressorUi.Version at {code and code.group(1)}")


def check_dependencies(manifests):
    for manifest in manifests.values():
        for dependency in manifest.get("vpmDependencies", {}):
            if dependency.startswith(PACKAGE_PREFIX) and dependency not in manifests:
                fail(f"{manifest['name']} depends on unknown package {dependency}")


def build_zip(package_dir, files, output):
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in files:
            if path.is_file():
                info = zipfile.ZipInfo(path.relative_to(package_dir).as_posix(), ZIP_DATE)
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, path.read_bytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "Dist")
    parser.add_argument("--repo", default="sdosker/dosker-tools")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    package_dirs = sorted(path for path in PACKAGES_DIR.iterdir() if path.name.startswith(PACKAGE_PREFIX))
    if not package_dirs:
        fail("No packages found")

    manifests = {}
    seen_guids = {}
    for package_dir in package_dirs:
        manifest = json.loads((package_dir / "package.json").read_text(encoding="utf-8"))
        if manifest["name"] != package_dir.name:
            fail(f"{package_dir.name}: package.json is named {manifest['name']}")
        files = collect_files(package_dir)
        check_metas(package_dir, files, seen_guids)
        check_sources(files)
        check_versions(package_dir, manifest["version"])
        manifests[manifest["name"]] = manifest
    check_dependencies(manifests)

    for package_dir in package_dirs:
        manifest = manifests[package_dir.name]
        name, version = manifest["name"], manifest["version"]
        output = args.out / f"{name}-{version}.zip"
        files = collect_files(package_dir)
        build_zip(package_dir, files, output)

        tag = f"{name}-v{version}"
        manifest["url"] = f"https://github.com/{args.repo}/releases/download/{tag}/{output.name}"
        manifest["zipSHA256"] = hashlib.sha256(output.read_bytes()).hexdigest()
        output.with_suffix(".json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{output.name}: {sum(path.is_file() for path in files)} files, {output.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
