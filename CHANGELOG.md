# Changelog

Changes in this fork compared to the official Span Finder (as of June 2026).

---

## Summary of fork changes

### Right-click menu (Explorer style)

- Right-click on files and folders now opens the **normal Windows context menu** (like File Explorer or One Commander), including entries from installed apps (e.g. 7-Zip, TortoiseGit).
- At the **bottom** of that menu: **Copy path** and **Add to favorites** / **Remove from favorites**.
- **Shift + right-click** still opens the **full Span menu** (Open, Cut, Copy, Delete, Properties, and more).
- **Shift+F10** opens the Windows menu with the same footer items.
- In **Settings > Tools** you can switch back to the old Span flyout menu if you prefer.

### Favorite groups (sidebar)

- Favorites can be organized into **custom groups** (create, rename, delete).
- Move a favorite **into a group** or back to the ungrouped list via the context menu.
- Groups can be **expanded and collapsed**.
- Context menu entries: **New group**, **Move to group**, rename/delete group on the group header.
- **Fix:** Removing one favorite no longer moves items in other groups back to the ungrouped list.

### Paper theme

- New **Paper** color theme (inspired by the One Commander Paper palette).

### Miller columns and tabs

- **Column width** in Miller view is **remembered** when you navigate into subfolders.
- Column width is also kept when you **switch between tabs** (each tab keeps its layout).
- Resize the divider between columns as before; the chosen width is saved.

### Fonts

- Your chosen **font** (including monospace fonts like Consolas) is applied more reliably in file lists and the UI.

### Development and local install

- **build-dev.bat** / **run-dev.bat** — build and run the app without Visual Studio (only .NET 8 SDK required).
- **install-local.bat** — build a Release version and install it locally (Start menu: **SpanFinder Personal**).
- **tools/fix-install-local-bat.bat** — repairs batch files if an editor saved them in the wrong encoding.
- Dev builds run **without the Microsoft Store package**; settings are stored in a local JSON file under `%LOCALAPPDATA%\Span\`.

### SpanFinder app icon (June 2026)

- Fork icon: **teal dual-pane** (split view) plus amber **finder lens** — deliberately **not** the official three-column Miller logo (see `LICENSE.md` trademark section).
- Taskbar, title bar, Start Menu shortcut, and `Span.exe` use `Assets\app.ico`.
- Unpackaged / personal install: icon loads from the app folder (fixed blank taskbar).
- Regenerate assets: `python tools/generate-spanfinder-icons.py` (requires Pillow).
- **Fix:** Start menu / pinned taskbar showed a white tile (bad shortcut icon path + transparent ICO corners); icons are now opaque and `install-local` sets `app.ico,0` correctly.

### Settings persistence (June 2026)

- **Unpackaged** builds (dev + **SpanFinder Personal** from `install-local.bat`) now **always** read and write `%LOCALAPPDATA%\Span\settings.json` instead of per-installation package storage.
- Packaged runs **mirror** every setting change to that same JSON file and **merge** missing keys on startup (survives LocalSettings corruption wipes).
- **Favorite groups** layout prefers `%LOCALAPPDATA%\Span\favorites-layout.json` on load.
- After updating, set your options once; they should survive reboots. Always launch **SpanFinder Personal** (not the Microsoft Store **Span**) for fork features.

### Clipboard, shortcuts, delete errors (June 2026)

- **Fix:** Cut/Copy via native Windows shell context menu now enables **Paste** in SpanFinder (syncs OS clipboard / `CF_HDROP`).
- **Ctrl+Shift+C** — copy path (selected item or current folder); rebind in Settings → Shortcuts.
- **Fix:** Delete failures show **file in use** / read-only messages instead of misleading “admin required” when another program locks the file.

---

## Notes

- **Microsoft Store** = official release from LumiBear Studio.
- **This fork** = personal customizations; upstream updates may need to be merged manually.

---

## Git history (reference)

| Commit   | Topic |
|----------|--------|
| 4a65185  | Favorite groups, Paper theme, Miller column width, dev scripts |
| cfbf070  | install-local.bat, .gitattributes for batch files |
| 952dbc5  | Native shell context menu, this changelog |