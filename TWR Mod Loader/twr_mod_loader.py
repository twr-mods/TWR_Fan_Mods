from __future__ import annotations

import json
import os
import re
import shutil
import stat
import sys
import tempfile
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable

import tkinter as tk
from tkinter import filedialog, messagebox, ttk


APP_NAME = "Those Who Rule Mod Loader"
GAME_EXE = "Those Who Rule.exe"
CONFIG_NAME = "mod_loader_config.json"
MANIFEST_NAME = "managed_mods.json"
BEPI_ZIP_NAME = "BepInEx.zip"


class ModLoaderError(Exception):
    """An error that can be shown directly to the user."""


@dataclass
class SyncResult:
    installed: set[str]
    copied: int
    removed: int
    errors: list[str]


def loader_directory() -> Path:
    # PyInstaller extracts bundled code under _MEI..., so frozen apps must use
    # sys.executable to find files distributed beside the actual EXE.
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def app_data_directory() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "ThoseWhoRuleModLoader"
    return Path.home() / ".those_who_rule_mod_loader"


def resolve_build_directory(selected_path: str | os.PathLike[str]) -> Path:
    selected = Path(selected_path).expanduser()
    if not selected.is_dir():
        raise ModLoaderError("The selected game directory does not exist.")

    selected = selected.resolve()
    child_build = selected / "Build"
    if (child_build / GAME_EXE).is_file():
        return child_build
    if (selected / GAME_EXE).is_file():
        return selected

    raise ModLoaderError(
        f'"{GAME_EXE}" could not be found. Select either the Those Who Rule '
        "folder or its Build folder."
    )


def scan_mods(mods_path: str | os.PathLike[str]) -> list[Path]:
    folder = Path(mods_path).expanduser()
    if not folder.is_dir():
        raise ModLoaderError("The selected mods folder does not exist.")
    try:
        mods = [
            item
            for item in folder.rglob("*")
            if item.is_file() and item.suffix.casefold() == ".dll"
        ]
    except OSError as exc:
        raise ModLoaderError(f"The mods folder could not be read: {exc}") from exc

    by_filename: dict[str, list[Path]] = {}
    for mod in mods:
        by_filename.setdefault(mod.name.casefold(), []).append(mod)
    duplicates = [paths for paths in by_filename.values() if len(paths) > 1]
    if duplicates:
        details = []
        for paths in duplicates:
            relative_paths = sorted(
                (str(path.relative_to(folder)) for path in paths), key=str.casefold
            )
            details.append(f"{paths[0].name}: " + ", ".join(relative_paths))
        raise ModLoaderError(
            "Multiple mod DLLs have the same filename. BepInEx's plugins folder "
            "cannot contain both copies. Remove or rename one of these files:\n\n"
            + "\n".join(details)
        )

    return sorted(mods, key=lambda item: (item.name.casefold(), str(item).casefold()))


def load_json(path: Path, default: dict) -> dict:
    try:
        with path.open("r", encoding="utf-8") as handle:
            value = json.load(handle)
        return value if isinstance(value, dict) else default.copy()
    except (FileNotFoundError, OSError, json.JSONDecodeError, UnicodeError):
        return default.copy()


def save_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            "w", encoding="utf-8", dir=path.parent, delete=False, suffix=".tmp"
        ) as handle:
            json.dump(value, handle, indent=2, sort_keys=True)
            handle.write("\n")
            temporary = Path(handle.name)
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink(missing_ok=True)


def preferred_or_fallback_path(filename: str) -> Path:
    preferred = loader_directory() / filename
    fallback = app_data_directory() / filename
    return preferred if preferred.exists() else fallback


def save_portable_json(filename: str, value: dict) -> Path:
    preferred = loader_directory() / filename
    try:
        save_json(preferred, value)
        return preferred
    except OSError:
        fallback = app_data_directory() / filename
        save_json(fallback, value)
        return fallback


def normalize_zip_name(name: str) -> PurePosixPath:
    if "\x00" in name:
        raise ModLoaderError("BepInEx.zip contains an invalid path.")
    normalized = name.replace("\\", "/")
    if normalized.startswith("/") or re.match(r"^[A-Za-z]:", normalized):
        raise ModLoaderError("BepInEx.zip contains an absolute path.")
    path = PurePosixPath(normalized)
    if any(part == ".." for part in path.parts):
        raise ModLoaderError("BepInEx.zip contains an unsafe '..' path.")
    return path


def validate_bepinex_zip(zip_path: Path) -> tuple[list[zipfile.ZipInfo], list[str]]:
    try:
        with zipfile.ZipFile(zip_path, "r") as archive:
            infos = archive.infolist()
            if not infos:
                raise ModLoaderError("BepInEx.zip is empty.")

            normalized: list[PurePosixPath] = []
            for info in infos:
                path = normalize_zip_name(info.filename)
                if not path.parts:
                    continue
                mode = (info.external_attr >> 16) & 0o170000
                if mode == stat.S_IFLNK:
                    raise ModLoaderError("BepInEx.zip contains an unsupported symbolic link.")
                normalized.append(path)

            has_root_bepinex = any(
                path.parts and path.parts[0].casefold() == "bepinex"
                for path in normalized
            )
            if not has_root_bepinex:
                nested = next(
                    (
                        path
                        for path in normalized
                        if any(part.casefold() == "bepinex" for part in path.parts[1:])
                    ),
                    None,
                )
                if nested is not None:
                    raise ModLoaderError(
                        "BepInEx.zip has an extra enclosing directory. Its BepInEx "
                        "folder must be at the archive root."
                    )
                raise ModLoaderError(
                    "BepInEx.zip does not contain a root-level BepInEx folder."
                )

            root_files = {
                path.parts[0].casefold()
                for path in normalized
                if len(path.parts) == 1 and not str(path).endswith("/")
            }
            warnings: list[str] = []
            missing = [
                name
                for name in ("doorstop_config.ini", "winhttp.dll")
                if name.casefold() not in root_files
            ]
            if missing:
                warnings.append(
                    "The archive has a root-level BepInEx folder but is missing the "
                    "usual loader file(s): " + ", ".join(missing) + "."
                )
            corrupt_member = archive.testzip()
            if corrupt_member is not None:
                raise ModLoaderError(
                    f"BepInEx.zip is corrupt near {corrupt_member}."
                )
            return infos, warnings
    except (zipfile.BadZipFile, RuntimeError) as exc:
        raise ModLoaderError("BepInEx.zip is not a valid ZIP archive.") from exc
    except OSError as exc:
        raise ModLoaderError(f"BepInEx.zip could not be read: {exc}") from exc


def install_bepinex(zip_path: Path, build_dir: Path) -> tuple[Path, list[str]]:
    _, warnings = validate_bepinex_zip(zip_path)
    temporary_zip = build_dir / ".twr_mod_loader_BepInEx.zip"
    build_root = build_dir.resolve()
    try:
        shutil.copy2(zip_path, temporary_zip)
        with zipfile.ZipFile(temporary_zip, "r") as archive:
            for info in archive.infolist():
                relative = normalize_zip_name(info.filename)
                if not relative.parts:
                    continue
                destination = (build_dir / Path(*relative.parts)).resolve()
                try:
                    destination.relative_to(build_root)
                except ValueError as exc:
                    raise ModLoaderError("BepInEx.zip contains an unsafe path.") from exc

                if info.is_dir() or info.filename.endswith(("/", "\\")):
                    destination.mkdir(parents=True, exist_ok=True)
                    continue
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info, "r") as source, destination.open("wb") as target:
                    shutil.copyfileobj(source, target)

        plugins_dir = build_dir / "BepInEx" / "plugins"
        plugins_dir.mkdir(parents=True, exist_ok=True)
        return plugins_dir.resolve(), warnings
    except ModLoaderError:
        raise
    except (OSError, zipfile.BadZipFile, RuntimeError) as exc:
        raise ModLoaderError(
            "BepInEx installation failed. Close Those Who Rule and retry. "
            f"Details: {exc}"
        ) from exc
    finally:
        try:
            temporary_zip.unlink(missing_ok=True)
        except OSError:
            pass


def read_managed_mods(path: Path) -> set[str]:
    data = load_json(path, {"managed_mods": []})
    values = data.get("managed_mods", [])
    if not isinstance(values, list):
        return set()
    return {value for value in values if isinstance(value, str) and Path(value).name == value}


def write_managed_mods(path: Path, names: Iterable[str]) -> None:
    save_json(path, {"managed_mods": sorted(set(names), key=str.casefold)})


def synchronize_mods(
    mods_dir: Path,
    plugins_dir: Path,
    checked_names: set[str],
    previously_managed: set[str],
) -> SyncResult:
    detected = {item.name: item for item in scan_mods(mods_dir)}
    desired = {name for name in checked_names if name in detected}
    installed = {
        name for name in previously_managed if (plugins_dir / name).is_file()
    }
    copied = 0
    removed = 0
    errors: list[str] = []

    try:
        plugins_dir.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise ModLoaderError(
            "The BepInEx plugins folder could not be created. Close Those Who Rule "
            f"and retry. Details: {exc}"
        ) from exc

    for name in sorted(desired, key=str.casefold):
        try:
            shutil.copy2(detected[name], plugins_dir / name)
            installed.add(name)
            copied += 1
        except OSError as exc:
            errors.append(f"Could not copy {name}: {exc}")

    unchecked_detected = set(detected) - desired
    stale_managed = previously_managed - desired
    removable = unchecked_detected | stale_managed
    for name in sorted(removable, key=str.casefold):
        if Path(name).name != name:
            continue
        target = plugins_dir / name
        try:
            if target.is_file():
                target.unlink()
                removed += 1
            installed.discard(name)
        except OSError as exc:
            errors.append(f"Could not remove {name}: {exc}")
            if target.is_file():
                installed.add(name)

    return SyncResult(installed, copied, removed, errors)


def enable_windows_dpi_awareness() -> None:
    if sys.platform != "win32":
        return
    try:
        import ctypes

        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


class ScrollableModList(ttk.Frame):
    def __init__(self, parent: tk.Misc) -> None:
        super().__init__(parent, style="List.TFrame")
        self.canvas = tk.Canvas(
            self, highlightthickness=0, borderwidth=0, background="#ffffff"
        )
        self.scrollbar = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas, style="List.TFrame", padding=(14, 10))
        self.window_id = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        self.canvas.grid(row=0, column=0, sticky="nsew")
        self.scrollbar.grid(row=0, column=1, sticky="ns")
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)
        self.inner.bind("<Configure>", self._update_scroll_region)
        self.canvas.bind("<Configure>", self._resize_inner)
        self.canvas.bind("<Enter>", lambda _event: self.canvas.bind_all("<MouseWheel>", self._wheel))
        self.canvas.bind("<Leave>", lambda _event: self.canvas.unbind_all("<MouseWheel>"))

    def _update_scroll_region(self, _event: tk.Event) -> None:
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _resize_inner(self, event: tk.Event) -> None:
        self.canvas.itemconfigure(self.window_id, width=event.width)

    def _wheel(self, event: tk.Event) -> None:
        self.canvas.yview_scroll(int(-event.delta / 120), "units")

    def clear(self) -> None:
        for child in self.inner.winfo_children():
            child.destroy()


class ModLoaderApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title(APP_NAME)
        self.root.geometry("1080x760")
        self.root.minsize(880, 620)

        self.game_path = tk.StringVar()
        self.mods_path = tk.StringVar()
        self.status = tk.StringVar(value="Ready.")
        self.mod_vars: dict[str, tk.BooleanVar] = {}
        self.saved_states: dict[str, bool] = {}
        self.resolved_build: Path | None = None
        self.plugins_dir: Path | None = None

        self._configure_styles()
        self._build_ui()
        self._load_configuration()
        self.root.protocol("WM_DELETE_WINDOW", self._close)

    def _configure_styles(self) -> None:
        style = ttk.Style(self.root)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        style.configure("TLabel", font=("Segoe UI", 10))
        style.configure("Title.TLabel", font=("Segoe UI Semibold", 19), foreground="#253449")
        style.configure("Step.TLabel", font=("Segoe UI Semibold", 11), foreground="#26384f")
        style.configure("TButton", font=("Segoe UI Semibold", 10), padding=(14, 9))
        style.configure("Setup.TButton", font=("Segoe UI Semibold", 12), padding=(20, 16))
        style.configure("Load.TButton", font=("Segoe UI Semibold", 11), padding=(16, 12))
        style.configure("Mod.TCheckbutton", font=("Segoe UI", 11), padding=(4, 8))
        style.configure("Status.TLabel", font=("Segoe UI", 9), foreground="#52657d")
        style.configure("List.TFrame", background="#ffffff")

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.root, padding=(24, 20, 24, 16))
        outer.grid(row=0, column=0, sticky="nsew")
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(2, weight=1)

        ttk.Label(outer, text=APP_NAME, style="Title.TLabel").grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 18)
        )

        upper = ttk.Frame(outer)
        upper.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(0, 22))
        upper.columnconfigure(0, weight=1)
        upper.rowconfigure(0, weight=1)

        paths = ttk.Frame(upper)
        paths.grid(row=0, column=0, sticky="nsew", padx=(0, 20))
        paths.columnconfigure(0, weight=1)
        self._add_path_row(
            paths,
            0,
            '1. Location of the game file "Those Who Rule"',
            self.game_path,
            self._browse_game,
        )
        self._add_path_row(
            paths,
            2,
            "2. Location of your mods file",
            self.mods_path,
            self._browse_mods,
        )

        ttk.Button(upper, text="3. Setup", style="Setup.TButton", command=self._setup).grid(
            row=0, column=1, sticky="nsew"
        )

        lower = ttk.Frame(outer)
        lower.grid(row=2, column=0, columnspan=2, sticky="nsew")
        lower.columnconfigure(0, weight=1)
        lower.rowconfigure(1, weight=1)
        ttk.Label(lower, text="Available mods", style="Step.TLabel").grid(
            row=0, column=0, sticky="w", pady=(0, 8)
        )

        self.mod_list = ScrollableModList(lower)
        self.mod_list.grid(row=1, column=0, sticky="nsew", padx=(0, 18))

        actions = ttk.Frame(lower)
        actions.grid(row=1, column=1, sticky="ns")
        actions.columnconfigure(0, weight=1)
        ttk.Button(actions, text="Recheck mods", command=self._recheck_mods).grid(
            row=0, column=0, sticky="ew", pady=(0, 12)
        )
        ttk.Button(actions, text="4. Load mods", style="Load.TButton", command=self._load_mods).grid(
            row=1, column=0, sticky="ew"
        )

        ttk.Separator(outer).grid(row=3, column=0, columnspan=2, sticky="ew", pady=(18, 10))
        ttk.Label(outer, textvariable=self.status, style="Status.TLabel", wraplength=950).grid(
            row=4, column=0, columnspan=2, sticky="ew"
        )

    def _add_path_row(
        self,
        parent: ttk.Frame,
        row: int,
        label: str,
        variable: tk.StringVar,
        command,
    ) -> None:
        ttk.Label(parent, text=label, style="Step.TLabel").grid(
            row=row, column=0, columnspan=2, sticky="w", pady=(0, 7)
        )
        ttk.Entry(parent, textvariable=variable, font=("Segoe UI", 10)).grid(
            row=row + 1, column=0, sticky="ew", padx=(0, 10), ipady=5
        )
        ttk.Button(parent, text="Browse", command=command).grid(
            row=row + 1, column=1, sticky="ew"
        )
        parent.rowconfigure(row + 1, pad=14)

    def _browse_game(self) -> None:
        selected = filedialog.askdirectory(
            title="Select Those Who Rule folder",
            initialdir=self.game_path.get() or None,
        )
        if selected:
            self.game_path.set(selected)

    def _browse_mods(self) -> None:
        selected = filedialog.askdirectory(
            title="Select folder containing mod DLLs",
            initialdir=self.mods_path.get() or None,
        )
        if selected:
            self.mods_path.set(selected)
            self._recheck_mods()

    def _load_configuration(self) -> None:
        data = load_json(preferred_or_fallback_path(CONFIG_NAME), {})
        self.game_path.set(str(data.get("selected_game_path", "")))
        self.mods_path.set(str(data.get("selected_mods_path", "")))
        self.saved_states = {
            str(name): bool(value)
            for name, value in data.get("checkbox_states", {}).items()
            if isinstance(name, str)
        } if isinstance(data.get("checkbox_states", {}), dict) else {}

        build_value = data.get("resolved_build_path", "")
        plugins_value = data.get("plugins_path", "")
        if isinstance(build_value, str) and build_value.strip():
            build = Path(build_value)
            if build.is_dir() and (build / GAME_EXE).is_file():
                self.resolved_build = build
        if isinstance(plugins_value, str) and plugins_value.strip():
            plugins = Path(plugins_value)
            if plugins.is_dir():
                self.plugins_dir = plugins
        if self.mods_path.get() and Path(self.mods_path.get()).is_dir():
            self._recheck_mods(save=False)

    def _configuration(self) -> dict:
        states = {name: variable.get() for name, variable in self.mod_vars.items()}
        return {
            "selected_game_path": self.game_path.get().strip(),
            "resolved_build_path": str(self.resolved_build or ""),
            "selected_mods_path": self.mods_path.get().strip(),
            "plugins_path": str(self.plugins_dir or ""),
            "checkbox_states": states,
        }

    def _save_configuration(self) -> None:
        save_portable_json(CONFIG_NAME, self._configuration())

    def _setup(self) -> None:
        try:
            build = resolve_build_directory(self.game_path.get().strip())
            package = loader_directory() / BEPI_ZIP_NAME
            if not package.is_file():
                raise ModLoaderError("BepInEx.zip was not found beside the mod loader.")
            plugins, warnings = install_bepinex(package, build)
            self.resolved_build = build
            self.plugins_dir = plugins
            self._save_configuration()
            self._recheck_mods(save=False)
            message = f"BepInEx setup complete. Plugins folder: {plugins}"
            if warnings:
                warning_text = " ".join(warnings)
                message += " Warning: " + warning_text
                messagebox.showwarning(
                    APP_NAME,
                    f"BepInEx setup completed.\n\n{warning_text}",
                )
            self.status.set(message)
        except (ModLoaderError, OSError) as exc:
            self._show_error(str(exc))

    def _current_states(self) -> dict[str, bool]:
        states = dict(self.saved_states)
        states.update({name: variable.get() for name, variable in self.mod_vars.items()})
        return states

    def _recheck_mods(self, save: bool = True) -> None:
        try:
            mods = scan_mods(self.mods_path.get().strip())
            prior = self._current_states()
            self.mod_vars.clear()
            self.mod_list.clear()

            if not mods:
                ttk.Label(
                    self.mod_list.inner,
                    text="No .dll mods found in the selected mods folder.",
                    foreground="#607086",
                    background="#ffffff",
                ).grid(row=0, column=0, sticky="w", pady=10)
            else:
                for row, mod in enumerate(mods):
                    variable = tk.BooleanVar(value=prior.get(mod.name, True))
                    self.mod_vars[mod.name] = variable
                    ttk.Checkbutton(
                        self.mod_list.inner,
                        text=mod.name,
                        variable=variable,
                        style="Mod.TCheckbutton",
                    ).grid(row=row, column=0, sticky="w")
            self.saved_states = {name: value.get() for name, value in self.mod_vars.items()}
            self.status.set(f"Found {len(mods)} mod{'s' if len(mods) != 1 else ''}.")
            if save:
                self._save_configuration()
        except (ModLoaderError, OSError) as exc:
            self._show_error(str(exc))

    def _resolve_plugins_for_load(self) -> Path:
        build = resolve_build_directory(self.game_path.get().strip())
        bepinex = build / "BepInEx"
        if not bepinex.is_dir():
            raise ModLoaderError("BepInEx is not set up for this game folder. Run 3. Setup first.")
        plugins = bepinex / "plugins"
        try:
            plugins.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            raise ModLoaderError(
                "The BepInEx plugins folder could not be created. Close Those Who Rule "
                f"and retry. Details: {exc}"
            ) from exc
        self.resolved_build = build
        self.plugins_dir = plugins.resolve()
        return self.plugins_dir

    def _load_mods(self) -> None:
        try:
            mods_dir = Path(self.mods_path.get().strip()).expanduser().resolve()
            current_mods = scan_mods(mods_dir)
            current_names = {item.name for item in current_mods}
            preserved = self._current_states()
            checked = {
                name for name in current_names if preserved.get(name, True)
            }
            plugins = self._resolve_plugins_for_load()
            manifest_path = preferred_or_fallback_path(MANIFEST_NAME)
            previous = read_managed_mods(manifest_path)
            result = synchronize_mods(mods_dir, plugins, checked, previous)

            if result.errors:
                # The manifest records the files that are actually still installed,
                # so a partial per-file failure remains internally consistent.
                save_portable_json(
                    MANIFEST_NAME,
                    {"managed_mods": sorted(result.installed, key=str.casefold)},
                )
                self._save_configuration()
                details = "\n".join(result.errors)
                self._show_error(
                    "Some mods could not be synchronized. Close Those Who Rule and retry.\n\n"
                    + details
                )
                return

            save_portable_json(
                MANIFEST_NAME,
                {"managed_mods": sorted(result.installed, key=str.casefold)},
            )
            self._save_configuration()
            self.status.set(f"Loaded {result.copied} mods; removed {result.removed}.")
        except (ModLoaderError, OSError) as exc:
            self._show_error(str(exc))

    def _show_error(self, message: str) -> None:
        self.status.set("Operation failed. See the error message.")
        messagebox.showerror(APP_NAME, message)

    def _close(self) -> None:
        try:
            self._save_configuration()
        except OSError:
            pass
        self.root.destroy()


def main() -> None:
    enable_windows_dpi_awareness()
    root = tk.Tk()
    ModLoaderApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
