"""Compiles the packages against the references of an avatar project, without opening Unity.

Reads the defines and references from the newest Assembly-CSharp-Editor.rsp in the project's Library, so that
project must have been compiled by Unity at least once. Each package is compiled alone, following the asmdef
references, which catches a package using something it doesn't declare.

Usage: python scripts/compile_check.py <project> [--unity F:/Unity/2022.3.22f1] [package ...]
"""
import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES_DIR = ROOT / "Packages"
PACKAGE_PREFIX = "com.dosker."
# The references an asmdef in this repository can't see, because they come from the project
LOCAL_DEFINES = {"com.vrchat.avatars": "DOSKER_HAS_VRC_AVATARS"}


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def read_project_options(project):
    response_files = sorted(
        (project / "Library" / "Bee" / "artifacts").glob("*E.dag/Assembly-CSharp-Editor.rsp"),
        key=lambda path: path.stat().st_mtime,
    )
    if not response_files:
        fail(f"No compiled scripts found in {project}. Open it in Unity once.")

    defines = []
    references = []
    for line in response_files[-1].read_text(encoding="utf-8").splitlines():
        if line.startswith("-define:"):
            defines.append(line)
        elif line.startswith("-r:"):
            path = Path(line[3:].strip('"'))
            references.append(path if path.is_absolute() else project / path)
    return defines, references


def find_asmdefs():
    asmdefs = {}
    for path in PACKAGES_DIR.glob(f"{PACKAGE_PREFIX}*/**/*.asmdef"):
        data = json.loads(path.read_text(encoding="utf-8"))
        asmdefs[data["name"]] = (path, data)
    return asmdefs


def compile_order(asmdefs):
    done = []

    def visit(name):
        if name in done or name not in asmdefs:
            return
        for reference in asmdefs[name][1].get("references", []):
            visit(reference)
        done.append(name)

    for name in sorted(asmdefs):
        visit(name)
    return done


def compile_assembly(name, asmdefs, defines, references, unity, output_dir):
    path, data = asmdefs[name]
    sources = sorted(path.parent.rglob("*.cs"))
    output = output_dir / f"{name}.dll"
    local = [output_dir / f"{reference}.dll" for reference in data.get("references", []) if reference in asmdefs]
    # Names that this repository also builds must come from this run, not from the project
    external = [reference for reference in references if reference.stem not in asmdefs]
    extra = [f"-define:{define}" for define in LOCAL_DEFINES.values()]

    response = output_dir / f"{name}.rsp"
    lines = ["-target:library", "-nologo", "-langversion:9.0", "-nostdlib+", "-warn:0", f'-out:"{output}"']
    lines += defines + extra
    lines += [f'-r:"{reference}"' for reference in external + local]
    lines += [f'"{source}"' for source in sources]
    response.write_text("\n".join(lines), encoding="utf-8")

    compiler = unity / "Editor" / "Data" / "DotNetSdkRoslyn" / "csc.dll"
    result = subprocess.run(["dotnet", str(compiler), f"@{response}"], capture_output=True, text=True)
    errors = [line for line in result.stdout.splitlines() if ": error " in line]
    print(f"{name}: {'ok' if result.returncode == 0 else f'{len(errors)} errors'}")
    for line in errors[:40]:
        print(f"  {line}")
    if result.returncode != 0 and not errors:
        print(result.stdout + result.stderr)
    return result.returncode == 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    parser.add_argument("packages", nargs="*")
    parser.add_argument("--unity", type=Path, default=Path("F:/Unity/2022.3.22f1"))
    args = parser.parse_args()

    defines, references = read_project_options(args.project)
    asmdefs = find_asmdefs()
    wanted = [
        name for name in compile_order(asmdefs)
        if not args.packages or any(package in str(asmdefs[name][0]) for package in args.packages)
    ]

    with tempfile.TemporaryDirectory() as temp:
        output_dir = Path(temp)
        passed = True
        for name in compile_order(asmdefs):
            if name not in wanted and not any(name in asmdefs[other][1].get("references", []) for other in wanted):
                continue
            passed &= compile_assembly(name, asmdefs, defines, references, args.unity, output_dir)
    sys.exit(0 if passed else 1)


if __name__ == "__main__":
    main()
