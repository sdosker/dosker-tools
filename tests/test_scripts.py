import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import build_listing  # noqa: E402
import build_packages  # noqa: E402
import bump  # noqa: E402
import check_vrcfury  # noqa: E402
import compile_check  # noqa: E402
import sync_to_project  # noqa: E402


def package_dir(root, changelog, source=""):
    root.mkdir(parents=True, exist_ok=True)
    (root / "CHANGELOG.md").write_text(changelog, encoding="utf-8")
    (root / "Code.cs").write_text(source, encoding="utf-8")
    return root


class CheckVersions(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "com.dosker.test"

    def assert_fails(self, changelog, version="1.1.0", source=""):
        with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
            build_packages.check_versions(package_dir(self.root, changelog, source), version)

    def test_accepts_ordered_changelog(self):
        changelog = "## [1.1.0] - 2026-11-01\n\n## [1.0.0] - 2026-10-31\n"
        build_packages.check_versions(package_dir(self.root, changelog), "1.1.0")

    def test_rejects_version_mismatch(self):
        self.assert_fails("## [1.0.0] - 2026-10-31\n")

    def test_rejects_missing_date(self):
        self.assert_fails("## [1.1.0]\n")

    def test_rejects_wrong_order(self):
        self.assert_fails("## [1.1.0] - 2026-11-01\n\n## [1.2.0] - 2026-10-31\n")

    def test_rejects_repeated_version(self):
        self.assert_fails("## [1.1.0] - 2026-11-01\n\n## [1.1.0] - 2026-10-31\n")

    def test_rejects_stale_constant_in_code(self):
        self.assert_fails("## [1.1.0] - 2026-11-01\n", source='public const string Version = "1.0.0";')


class CheckDependencies(unittest.TestCase):
    def manifests(self, required, current):
        return {
            "com.dosker.a": {"name": "com.dosker.a", "version": "1.0.0", "vpmDependencies": {"com.dosker.b": required}},
            "com.dosker.b": {"name": "com.dosker.b", "version": current},
        }

    def test_accepts_satisfied_range(self):
        build_packages.check_dependencies(self.manifests(">=0.6.0", "0.7.0"))

    def test_rejects_range_above_current_version(self):
        with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
            build_packages.check_dependencies(self.manifests(">=0.8.0", "0.7.0"))

    def test_rejects_unknown_range_format(self):
        with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
            build_packages.check_dependencies(self.manifests("^0.6.0", "0.7.0"))


class ChangelogSection(unittest.TestCase):
    def test_takes_only_the_requested_version(self):
        with tempfile.TemporaryDirectory() as temp:
            root = package_dir(Path(temp), "# Log\n\n## [1.1.0] - 2026-11-01\n\n- New\n\n## [1.0.0] - 2026-10-31\n\n- Old\n")
            self.assertEqual(build_packages.changelog_section(root, "1.1.0"), "- New\n")
            self.assertEqual(build_packages.changelog_section(root, "1.0.0"), "- Old\n")


class Bump(unittest.TestCase):
    def test_zeroes_the_fields_to_the_right(self):
        self.assertEqual(bump.next_version("1.2.3", "major"), "2.0.0")
        self.assertEqual(bump.next_version("1.2.3", "minor"), "1.3.0")
        self.assertEqual(bump.next_version("1.2.3", "patch"), "1.2.4")


class Listing(unittest.TestCase):
    def test_newest_version_is_numeric(self):
        self.assertEqual(max(["1.9.0", "1.10.0"], key=build_listing.version_key), "1.10.0")

    def test_page_hides_replaced_packages_but_listing_keeps_them(self):
        def manifest(name, display, **extra):
            return {"name": name, "displayName": display, "version": "1.0.0", "url": f"https://x/{name}.zip", **extra}

        listing = build_listing.build_listing([
            manifest("com.dosker.old", "Dosker Tools - Old"),
            manifest("com.dosker.new", "Dosker Tools - New", legacyPackages=["com.dosker.old"]),
        ], "https://x/index.json")
        page = build_listing.build_page(listing, "owner/repo")
        self.assertIn("com.dosker.old", listing["packages"])
        self.assertIn("<code class=\"pkg-id\">com.dosker.new</code>", page)
        self.assertNotIn("<code class=\"pkg-id\">com.dosker.old</code>", page)


class NestedAssemblies(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.packages = Path(self.temp.name) / "Packages"
        self.tests = Path(self.temp.name) / "tests"
        editor = self.packages / "com.dosker.test" / "Editor"
        for path in [editor / "A.asmdef", editor / "Window.cs", editor / "Helpers" / "Util.cs", editor / "Menu" / "B.asmdef", editor / "Menu" / "View.cs"]:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("", encoding="utf-8")
        for path in [self.tests / "com.dosker.test" / "OuterValidation.cs", self.tests / "com.dosker.test" / "Menu" / "MenuValidation.cs"]:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("", encoding="utf-8")
        self.editor = editor

    def test_subfolder_with_asmdef_is_another_assembly(self):
        names = [path.name for path in compile_check.assembly_sources(self.editor)]
        self.assertEqual(names, ["Util.cs", "Window.cs"])
        self.assertEqual([path.name for path in compile_check.assembly_sources(self.editor / "Menu")], ["View.cs"])

    def test_tests_follow_the_asmdef_folder(self):
        with mock.patch.object(compile_check, "PACKAGES_DIR", self.packages), mock.patch.object(compile_check, "TESTS_DIR", self.tests):
            self.assertEqual(compile_check.tests_folder(self.editor), self.tests / "com.dosker.test")
            self.assertEqual(compile_check.tests_folder(self.editor / "Menu"), self.tests / "com.dosker.test" / "Menu")

    def test_validation_goes_next_to_its_asmdef(self):
        with mock.patch.object(sync_to_project, "TESTS_DIR", self.tests):
            files = sync_to_project.validation_files("com.dosker.test")
        self.assertIn(Path("Editor/Validation/OuterValidation.cs"), files)
        self.assertIn(Path("Editor/Menu/Validation/MenuValidation.cs"), files)
        self.assertIn(Path("Editor/Menu/Validation"), files)


class VrcfuryWatch(unittest.TestCase):
    def test_ignores_prereleases(self):
        listing = {"packages": {"com.vrcfury.vrcfury": {"versions": {
            "1.9.0": {"url": "a"},
            "1.10.0": {"url": "b"},
            "1.11.0-beta.1": {"url": "c"},
        }}}}
        self.assertEqual(check_vrcfury.newest_version(listing), ("1.10.0", "b"))

    def test_compat_file_matches_the_tested_version_in_code(self):
        compat = json.loads(check_vrcfury.COMPAT_FILE.read_text(encoding="utf-8"))
        patch = Path(__file__).resolve().parents[1] / "Packages/com.dosker.compressor-exclusions/Editor/CompressorExclusionPatch.cs"
        self.assertIn(f'TestedVrcfury = "{compat["version"]}"', patch.read_text(encoding="utf-8"))

    def test_compat_file_lists_every_watched_file(self):
        compat = json.loads(check_vrcfury.COMPAT_FILE.read_text(encoding="utf-8"))
        self.assertEqual(sorted(compat["files"]), sorted(check_vrcfury.WATCHED_FILES))
        self.assertNotIn("missing", compat["files"].values())


if __name__ == "__main__":
    unittest.main()
