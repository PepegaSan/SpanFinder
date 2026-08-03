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
- **Unified sidebar list** (July 2026): one flat list with section headers (OneCommander-style) instead of nested ListViews — drag to reorder pins **and** groups in a single list.
- **Drop into group:** dragging a folder onto a group header or a pin inside a group adds it to that group (not only to ungrouped favorites). The drag tooltip shows the target group name.

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

### Split view, rename, and live folders (July 2026)

- **Quad toolbar button:** one-click **2×2** split next to the other split controls; click again to leave split view.
- **Per-pane New button:** each Dual/Quad pane header has a **New…** menu (new folder + Shell New types), without needing an empty-area right-click.
- **Shell rename:** “Rename” from the native Windows context menu now starts Span’s **inline rename** (raw shell rename with no UI did nothing in WinUI).
- **Tab view-mode bleed fix:** switching tabs no longer leaves the wrong host visible or overwrites another tab’s saved view mode when Dual/Quad layouts differ.
- **Resume refresh:** after the window was backgrounded for a few seconds, returning to the foreground **reaffirms file-system watchers** and refreshes visible panes so external changes are not missed.

### Merged from upstream v1.6.4 / v1.6.5 (July 2026)

- **Polish localization** (language option + strings).
- **Column View scroll** when opening a new column (Issue #53) and when reselecting a previous column (Issue #57) — kept fork Dual/Quad left-pane scroll targeting.
- **Thumbnails / preview** for image-editor formats: `.clip`, `.psd`, `.jfif` (Issue #56).

### Search, Recycle Bin (Quad), and clipboard (August 2026)

- **Live search click:** selecting a result in the filtered Miller column no longer clears the filter (GotFocus only restores when focus moves to a *different* column).
- **Second recursive search (Quad):** Enter search no longer overwrites the pre-search Miller column snapshot with the results column — leaving search restores the real folders again.
- **Delete in SearchBox:** Entf / Backspace / Ctrl+C/X/V/A while typing in the search box no longer trigger file commands (global `handledEventsToo` handler).
- **Recycle Bin in Dual/Quad:** opening the bin is left-pane only; explorer hosts stay collapsed so the bin no longer overlays Miller. Esc with an empty SearchBox leaves the bin.
- **Clipboard / Easy Tagger:** Ctrl+C writes **`CF_HDROP` first** (before slow StorageItem resolve). Miller selection falls back to `SelectedChild` after live-filter. With Recycle Bin open in split/quad, Ctrl+C and explorer shortcuts work again in the *focused secondary pane* (bin key blocking no longer applies globally).

### Outbound drag helper (August 2026)

- Shared `OutboundFileDragHelper` for drag-out (eager StorageItems, deferred fallback). Classic OLE/`CF_HDROP` drag for stubborn importers (e.g. HitPaw) remains an open follow-up — WinUI StorageItems alone is not always enough.

### Productivity & Miller polish (August 2026)

- **Command Palette** (`Ctrl+K`) enabled by default for quick commands and setting toggles.
- **Preview folder info:** when a folder is selected, the preview panel can show icon, item count, and notes (Settings → Browsing).
- **Color tags:** Red / Green / Blue tags on files and folders via context menu; badges in Miller, Details, List, and Icons.
- **Folder notes:** per-folder text notes (context menu **Folder note…** and preview panel); stored under `%LOCALAPPDATA%\Span\item-annotations.json`.
- **Long-path support:** local I/O uses Windows long-path prefixes so deep trees beyond ~260 characters work more reliably.
- **Everything search bridge:** recursive search (toolbar Enter) uses Voidtools **Everything** via `es.exe` when it is running; otherwise falls back to the built-in folder walk. Toggle in Settings → Browsing.
- **Miller meta columns:** each row shows **size** (files) / **child count** (folders) plus a compact **relative age** badge (One Commander–style pastel background, dark text) — useful when comparing folders in Dual pane.
- **Filter in Miller:** `Ctrl+Shift+F` works in Miller Columns; the filter applies only to the **last (leaf) column** so the path hierarchy stays intact.

---

## Notes

- **Microsoft Store** = official release from LumiBear Studio.
- **This fork** = personal customizations; upstream updates may need to be merged manually.

---

## Git history (reference)

| Commit   | Topic |
|----------|--------|
| 15e34c2  | Search / Recycle Bin Quad / clipboard (Easy Tagger) / outbound drag helper |
| b8f4b56  | Note August productivity commit in CHANGELOG git history table |
| 59a7efe  | Tags, folder notes, Everything search, long paths, Miller age badges |
| 4a65185  | Favorite groups, Paper theme, Miller column width, dev scripts |
| cfbf070  | install-local.bat, .gitattributes for batch files |
| 952dbc5  | Native shell context menu, this changelog |