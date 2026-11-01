"""Validates every package in Packages/ and builds the zips that VCC installs.

Writes <name>-<version>.zip, .json and .md to the output folder. The .json is the package manifest with
the download url and checksum, which build_listing.py turns into the VPM listing. The .md has the release
notes, taken from that version's section of the changelog.

Usage: python scripts/build_packages.py [--out Dist] [--repo sdosker/dosker-tools]
"""
import argparse
import hashlib
import json
import re
import subprocess
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
VERSION = re.compile(r"\d+\.\d+\.\d+")
DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
CODE_VERSION = re.compile(r'const string \w*Version = "(\d+\.\d+\.\d+)"')
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


def version_key(version):
    return [int(part) for part in version.split(".")]


def check_changelog(package_dir, version):
    changelog = (package_dir / "CHANGELOG.md").read_text(encoding="utf-8")
    entries = re.findall(r"^## \[([^\]]+)\](?: - (\S+))?\s*$", changelog, re.MULTILINE)
    if not entries or entries[0][0] != version:
        fail(f"{package_dir.name}: package.json is at {version}, CHANGELOG.md at {entries[0][0] if entries else None}")

    for entry, date in entries:
        if not VERSION.fullmatch(entry):
            fail(f"{package_dir.name}: CHANGELOG.md has an invalid version: {entry}")
        if not DATE.fullmatch(date):
            fail(f"{package_dir.name}: CHANGELOG.md has no valid date for {entry}")
    versions = [version_key(entry) for entry, _ in entries]
    dates = [date for _, date in entries]
    if versions != sorted(versions, reverse=True) or len(set(map(tuple, versions))) != len(versions):
        fail(f"{package_dir.name}: CHANGELOG.md versions must be unique, newest first")
    if dates != sorted(dates, reverse=True):
        fail(f"{package_dir.name}: CHANGELOG.md dates must be newest first")


def check_versions(package_dir, version):
    if not VERSION.fullmatch(version):
        fail(f"{package_dir.name}: invalid version {version}")
    check_changelog(package_dir, version)

    # Each package has its own version, so a constant in its code only has to match that package
    for source in package_dir.rglob("*.cs"):
        for code in CODE_VERSION.findall(source.read_text(encoding="utf-8")):
            if code != version:
                fail(f"{package_dir.name}: package.json is at {version}, {source.name} at {code}")


def check_not_tracked():
    tracked = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    leaked = [path for path in tracked.splitlines() if path.rsplit("/", 1)[-1] in EXCLUDED_NAMES]
    if leaked:
        fail(f"These files must never reach the repository: {', '.join(leaked)}")


def changelog_section(package_dir, version):
    changelog = (package_dir / "CHANGELOG.md").read_text(encoding="utf-8")
    start = re.search(rf"^## \[{re.escape(version)}\].*$", changelog, re.MULTILINE)
    following = re.search(r"^## \[", changelog[start.end():], re.MULTILINE)
    end = start.end() + following.start() if following else len(changelog)
    return changelog[start.end():end].strip() + "\n"


def check_dependencies(manifests):
    for manifest in manifests.values():
        for dependency in manifest.get("vpmDependencies", {}):
            if not dependency.startswith(PACKAGE_PREFIX):
                continue
            if dependency not in manifests:
                fail(f"{manifest['name']} depends on unknown package {dependency}")
            minimum = re.fullmatch(r">=\s*(\d+\.\d+\.\d+)", manifest["vpmDependencies"][dependency])
            if not minimum:
                fail(f"{manifest['name']}: the range of {dependency} must look like >=1.2.3")
            current = manifests[dependency]["version"]
            if version_key(minimum.group(1)) > version_key(current):
                fail(f"{manifest['name']} needs {dependency} {minimum.group(0)}, which is at {current}")


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
    check_not_tracked()

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
        output.with_suffix(".md").write_text(changelog_section(package_dir, version), encoding="utf-8")
        print(f"{output.name}: {sum(path.is_file() for path in files)} files, {output.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
