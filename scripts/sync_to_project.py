"""Copies the packages to an avatar project for testing, and brings back the .meta files Unity generated.

This repository is not a Unity project, so new files only get a .meta after being imported in one.

Usage:
  python scripts/sync_to_project.py push <project> [package ...]
  python scripts/sync_to_project.py pull <project> [package ...]
  python scripts/sync_to_project.py clean <project> [package ...]

push mirrors the packages to <project>/Packages, keeping the .meta files Unity generated for new files, because
a new one would get a different GUID. pull copies back only the .meta files that are missing here, and never
changes one that already exists. clean removes the package folders from the project.
"""
import argparse
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES_DIR = ROOT / "Packages"
PACKAGE_PREFIX = "com.dosker."
EXCLUDED_NAMES = {"CLAUDE.md", "CLAUDE.md.meta"}
RETRIES = 5


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def select_packages(names):
    available = sorted(path.name for path in PACKAGES_DIR.iterdir() if path.name.startswith(PACKAGE_PREFIX))
    unknown = sorted(set(names) - set(available))
    if unknown:
        fail(f"Unknown packages: {', '.join(unknown)}")
    return names or available


def with_retries(action, *args):
    # Sync clients like OneDrive hold files for a moment after a change
    for attempt in range(RETRIES):
        try:
            return action(*args)
        except PermissionError:
            if attempt == RETRIES - 1:
                raise
            time.sleep(1 + attempt)


def push(project_packages, package):
    source = PACKAGES_DIR / package
    target = project_packages / package
    wanted = {path.relative_to(source) for path in source.rglob("*") if path.name not in EXCLUDED_NAMES}

    for relative in sorted(wanted):
        destination = target / relative
        if (source / relative).is_dir():
            destination.mkdir(parents=True, exist_ok=True)
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            with_retries(shutil.copyfile, source / relative, destination)

    if target.exists():
        for path in sorted(target.rglob("*"), reverse=True):
            relative = path.relative_to(target)
            keeps_asset = path.suffix == ".meta" and relative.with_suffix("") in wanted
            if relative in wanted or keeps_asset:
                continue
            with_retries(path.rmdir if path.is_dir() else path.unlink)
    print(f"Copied {package}")


def pull(project_packages, package):
    source = project_packages / package
    if not source.is_dir():
        fail(f"{package} is not in {project_packages}")

    copied = 0
    for meta in source.rglob("*.meta"):
        relative = meta.relative_to(source)
        target = PACKAGES_DIR / package / relative
        if meta.name in EXCLUDED_NAMES or target.exists():
            continue
        # A .meta only belongs here if its asset does too
        if not target.with_suffix("").exists():
            print(f"Skipped {relative}: the asset isn't in this repository")
            continue
        shutil.copyfile(meta, target)
        print(f"Brought back {package}/{relative}")
        copied += 1
    print(f"{package}: {copied} new .meta files")


def clean(project_packages, package):
    target = project_packages / package
    if target.exists():
        with_retries(shutil.rmtree, target)
        print(f"Removed {package}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("action", choices=["push", "pull", "clean"])
    parser.add_argument("project", type=Path)
    parser.add_argument("packages", nargs="*")
    args = parser.parse_args()

    project_packages = args.project / "Packages"
    if not (args.project / "ProjectSettings").is_dir() or not project_packages.is_dir():
        fail(f"{args.project} is not a Unity project")
    if (args.project / "Temp" / "UnityLockfile").exists():
        fail("Close Unity for this project first")

    action = {"push": push, "pull": pull, "clean": clean}[args.action]
    for package in select_packages(args.packages):
        action(project_packages, package)


if __name__ == "__main__":
    main()
