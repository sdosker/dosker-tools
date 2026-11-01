"""Tells if a new VRCFury changed the files that Compressor Exclusions depends on.

Compares the newest stable version on the VRCFury listing with scripts/vrcfury_compat.json. Line endings are
ignored, so only a real change counts. Exits with 2 when a review is needed, which is what the scheduled
workflow uses to open an issue. After reviewing the checklist in the package's notes, record the new version
with --update.

Usage: python scripts/check_vrcfury.py [--update]
"""
import argparse
import hashlib
import io
import json
import sys
import urllib.request
import zipfile
from pathlib import Path

LISTING_URL = "https://vcc.vrcfury.com"
PACKAGE = "com.vrcfury.vrcfury"
WATCHED_FILES = ("ParameterCompressorSolverService.cs", "OptimizationDecision.cs")
COMPAT_FILE = Path(__file__).with_name("vrcfury_compat.json")


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "dosker-tools-compat-check"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


def version_key(version):
    return [int(part) for part in version.split(".")]


def newest_version(listing):
    versions = listing["packages"][PACKAGE]["versions"]
    stable = [version for version in versions if "-" not in version]
    newest = max(stable, key=version_key)
    return newest, versions[newest]["url"]


def hash_watched_files(archive_bytes):
    hashes = {}
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        for entry in archive.namelist():
            name = entry.rsplit("/", 1)[-1]
            if name in WATCHED_FILES:
                content = archive.read(entry).replace(b"\r\n", b"\n")
                hashes[name] = hashlib.sha256(content).hexdigest()
    missing = sorted(set(WATCHED_FILES) - set(hashes))
    if missing:
        hashes.update({name: "missing" for name in missing})
    return hashes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--update", action="store_true", help="record the newest version as reviewed")
    args = parser.parse_args()

    version, url = newest_version(json.loads(fetch(LISTING_URL)))
    hashes = hash_watched_files(fetch(url))

    if args.update:
        COMPAT_FILE.write_text(json.dumps({"version": version, "files": hashes}, indent=2) + "\n", encoding="utf-8")
        print(f"Recorded VRCFury {version}")
        return

    compat = json.loads(COMPAT_FILE.read_text(encoding="utf-8"))
    changed = sorted(name for name, value in hashes.items() if compat["files"].get(name) != value)
    if not changed:
        print(f"VRCFury {version}: the watched files match {compat['version']}")
        return

    print(f"VRCFury {version} changed since {compat['version']}: {', '.join(changed)}")
    sys.exit(2)


if __name__ == "__main__":
    main()
