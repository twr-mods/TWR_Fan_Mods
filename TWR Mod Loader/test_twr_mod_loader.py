import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from twr_mod_loader import (
    GAME_EXE,
    ModLoaderError,
    install_bepinex,
    load_json,
    read_managed_mods,
    resolve_build_directory,
    save_json,
    save_portable_json,
    scan_mods,
    synchronize_mods,
    validate_bepinex_zip,
    write_managed_mods,
)


class ModLoaderTests(unittest.TestCase):
    def test_resolves_game_root_and_build_folder(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "Those Who Rule"
            build = root / "Build"
            build.mkdir(parents=True)
            (build / GAME_EXE).write_bytes(b"game")
            self.assertEqual(resolve_build_directory(root), build.resolve())
            self.assertEqual(resolve_build_directory(build), build.resolve())

    def test_rejects_invalid_game_folder(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ModLoaderError):
                resolve_build_directory(temporary)

    def test_dll_scan_is_recursive_case_insensitive_and_sorted(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / "zeta.DLL").write_bytes(b"z")
            (folder / "Alpha.dll").write_bytes(b"a")
            (folder / "notes.txt").write_text("ignore", encoding="utf-8")
            nested = folder / "project" / "bin"
            nested.mkdir(parents=True)
            (nested / "Nested.DlL").write_bytes(b"nested")
            self.assertEqual(
                [item.name for item in scan_mods(folder)],
                ["Alpha.dll", "Nested.DlL", "zeta.DLL"],
            )

    def test_dll_scan_rejects_duplicate_filenames_from_different_folders(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            first = folder / "first"
            second = folder / "second"
            first.mkdir()
            second.mkdir()
            (first / "Example.dll").write_bytes(b"one")
            (second / "example.DLL").write_bytes(b"two")
            with self.assertRaisesRegex(ModLoaderError, "same filename"):
                scan_mods(folder)

    def test_bepinex_install_extracts_root_and_creates_plugins(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build = root / "Build"
            build.mkdir()
            package = root / "BepInEx.zip"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("BepInEx/core/BepInEx.dll", b"core")
                archive.writestr("doorstop_config.ini", b"config")
                archive.writestr("winhttp.dll", b"doorstop")
            existing = build / "BepInEx" / "plugins" / "Existing.dll"
            existing.parent.mkdir(parents=True)
            existing.write_bytes(b"keep")

            plugins, warnings = install_bepinex(package, build)
            self.assertEqual(warnings, [])
            self.assertTrue((build / "BepInEx" / "core" / "BepInEx.dll").is_file())
            self.assertTrue((build / "doorstop_config.ini").is_file())
            self.assertTrue(plugins.is_dir())
            self.assertEqual(existing.read_bytes(), b"keep")
            self.assertTrue(package.is_file())
            self.assertFalse((build / ".twr_mod_loader_BepInEx.zip").exists())

    def test_zip_rejects_enclosing_directory_and_traversal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            enclosed = root / "enclosed.zip"
            with zipfile.ZipFile(enclosed, "w") as archive:
                archive.writestr("BepInEx_x64/BepInEx/core/BepInEx.dll", b"x")
            with self.assertRaises(ModLoaderError):
                validate_bepinex_zip(enclosed)

            traversal = root / "traversal.zip"
            with zipfile.ZipFile(traversal, "w") as archive:
                archive.writestr("BepInEx/core/BepInEx.dll", b"x")
                archive.writestr("../escape.dll", b"x")
            with self.assertRaises(ModLoaderError):
                validate_bepinex_zip(traversal)

            corrupt = root / "corrupt.zip"
            corrupt.write_bytes(b"this is not a zip file")
            with self.assertRaises(ModLoaderError):
                validate_bepinex_zip(corrupt)

    def test_sync_copies_checked_and_only_removes_managed_or_detected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            mods = root / "Mods"
            plugins = root / "Build" / "BepInEx" / "plugins"
            mods.mkdir()
            plugins.mkdir(parents=True)
            (mods / "Checked.dll").write_bytes(b"new")
            (mods / "Unchecked.dll").write_bytes(b"off")
            (plugins / "Unchecked.dll").write_bytes(b"old")
            (plugins / "Stale.dll").write_bytes(b"stale")
            (plugins / "Manual.dll").write_bytes(b"manual")

            result = synchronize_mods(
                mods,
                plugins,
                {"Checked.dll"},
                {"Unchecked.dll", "Stale.dll"},
            )
            self.assertEqual(result.errors, [])
            self.assertEqual((plugins / "Checked.dll").read_bytes(), b"new")
            self.assertFalse((plugins / "Unchecked.dll").exists())
            self.assertFalse((plugins / "Stale.dll").exists())
            self.assertTrue((plugins / "Manual.dll").is_file())
            self.assertEqual(result.installed, {"Checked.dll"})

    def test_config_and_manifest_round_trip(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = root / "mod_loader_config.json"
            expected = {"selected_game_path": "C:/Game", "checkbox_states": {"A.dll": True}}
            save_json(config, expected)
            self.assertEqual(load_json(config, {}), expected)

            manifest = root / "managed_mods.json"
            write_managed_mods(manifest, {"Z.dll", "A.dll"})
            self.assertEqual(read_managed_mods(manifest), {"A.dll", "Z.dll"})
            self.assertEqual(json.loads(manifest.read_text(encoding="utf-8"))["managed_mods"], ["A.dll", "Z.dll"])

            portable_root = root / "portable"
            with patch("twr_mod_loader.loader_directory", return_value=portable_root):
                portable = save_portable_json("portable_test.json", {"ok": True})
            self.assertEqual(portable, portable_root / "portable_test.json")
            self.assertEqual(load_json(portable, {}), {"ok": True})

            corrupt_config = root / "corrupt_config.json"
            corrupt_config.write_text("{not valid json", encoding="utf-8")
            self.assertEqual(load_json(corrupt_config, {"safe": True}), {"safe": True})


if __name__ == "__main__":
    unittest.main()
