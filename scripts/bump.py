"""Raises the version of one package and opens its changelog entry.

Each package has its own version, so this never touches any other package. Raising a field zeroes the fields
to its right, and the Compressor Exclusions constant moves along with its package.json.

Usage: python scripts/bump.py <package> <major|minor|patch>
"""
import argparse
import datetime
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES_DIR = ROOT / "Packages"
CODE_VERSION = re.compile(r'(const string \w*Version = ")\d+\.\d+\.\d+(")')


def next_version(version, field):
    major, minor, patch = [int(part) for part in version.split(".")]
    if field == "major":
        return f"{major + 1}.0.0"
    if field == "minor":
        return f"{major}.{minor + 1}.0"
    return f"{major}.{minor}.{patch + 1}"


def replace_text(path, change):
    raw = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in raw else "\n"
    path.write_bytes(change(raw.replace("\r\n", "\n")).replace("\n", newline).encode("utf-8"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package")
    parser.add_argument("field", choices=["major", "minor", "patch"])
    args = parser.parse_args()

    package_dir = PACKAGES_DIR / args.package
    if not (package_dir / "package.json").is_file():
        print(f"ERROR: Unknown package {args.package}", file=sys.stderr)
        sys.exit(1)

    version = json.loads((package_dir / "package.json").read_text(encoding="utf-8"))["version"]
    bumped = next_version(version, args.field)
    today = datetime.date.today().isoformat()

    replace_text(package_dir / "package.json", lambda text: text.replace(f'"version": "{version}"', f'"version": "{bumped}"', 1))
    replace_text(
        package_dir / "CHANGELOG.md",
        lambda text: re.sub(r"^## \[", f"## [{bumped}] - {today}\n\n### Alterado\n\n- \n\n## [", text, count=1, flags=re.MULTILINE),
    )
    for source in package_dir.rglob("*.cs"):
        if CODE_VERSION.search(source.read_text(encoding="utf-8")):
            replace_text(source, lambda text: CODE_VERSION.sub(rf"\g<1>{bumped}\g<2>", text))

    print(f"{args.package}: {version} -> {bumped}. Fill in the new CHANGELOG.md entry.")


if __name__ == "__main__":
    main()
