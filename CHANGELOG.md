# Changelog

Changes in this fork compared to the official Span Finder (as of June 2026).

---

## Summary of fork changes

### Sort and grouping per folder (October 2026)

- **Each folder remembers its sort and grouping**: change them in a folder (column header, toolbar sort button, right-click menu) and that folder opens the same way next time. Your latest choice is also the default for folders you have not customized. The view mode (Miller/Details/List/Icons) is not changed per folder.
- **Folder templates** for folders without their own setting, detected from the content like Explorer's folder types:
  - **Downloads**: newest first, grouped by date, folders and files mixed (as before)
  - **Pictures** (mostly images): newest first
  - **Music / Videos** (mostly audio / video files): the Details view adds the **Length** column for that folder
- Stored in `%LOCALAPPDATA%\Span\folder-views.json` (up to 3,000 recently used folders).
- **Fix:** leaving Span while in Downloads no longer turns "group by date" on for every folder (the old Downloads special case saved its grouping as the global setting).

### Collapsible groups, more group-by options (October 2026)

- **Group headers work like Explorer** in Details, List and Icons: the arrow collapses/expands a group, clicking the header selects all its items (Ctrl adds to the selection), double-click also collapses/expands, right-click offers **Collapse all groups** / **Expand all groups**. Headers show the item count. Collapsed groups stay collapsed while browsing until the group-by option changes.
- New group-by options: **Date created** and **Color tag** (header menu, toolbar sort button, empty-area menu). Items without a date are grouped as "Unknown" instead of "A long time ago".
- **Fix:** with grouping on, navigating to another folder kept showing the previous folder's groups (Details, List, Icons). Details now also keeps its own sort when you navigate (it used to fall back to the Miller sort), and List/Icons pick up the saved grouping when they are opened.

### Drive usage bars in the sidebar (October 2026)

- Local and mapped drives in the sidebar show a thin **usage bar** under the name (OneCommander style); it turns **red from 90 %** full, like Explorer's "This PC". The tooltip shows "X free of Y" in the app language (was English only, also on Home and in the drive context menu).
- Free space is **kept current**: re-read after copy/move/delete/extract operations and when the window becomes active again (at most every 5 s), using the same timeout as drive loading.
- **Fix:** when the drive list was unchanged, the drive entries restored from the cache kept their old sizes, so Home showed stale free space until the drive list itself changed.

### Commands between panes (October 2026)

Total Commander / OneCommander-style commands for Dual and Quad layouts. "The other pane" is the pane that was active before the current one, so in Quad you click the target first, then the source.

| Shortcut | Command |
|----------|---------|
| `Shift+F5` | Copy selection to the other pane (asks for the target; in Quad all other panes are offered, Enter confirms) |
| `Shift+F6` | Move selection to the other pane (same dialog) |
| `Shift+F2` | Compare folders: selects everything here that is missing or older in the other pane, then `Shift+F5` copies the differences. The toast also counts what exists only there or is newer there. Files are matched by name, then by date modified (2 s tolerance for FAT) and size; subfolders present on both sides are not compared |
| `Ctrl+U` | Swap the folders of the two panes |
| `Ctrl+Shift+U` | Navigate the other pane to this folder |

- Right-click a file or folder in split view: **Copy to “‹folder›”** / **Move to “‹folder›”** (native Windows menu footer and Span menu).
- All shortcuts can be changed in Settings → Shortcuts and are in the command palette (`Ctrl+K`).
- **Fix:** copying a file now keeps its **Date modified** like Explorer does (the progress-reporting copy used to stamp the copy time; also affected moves between drives).

### Details view: choose columns like Windows Explorer (October 2026)

- **Right-click the column header** to show or hide columns, like Explorer: *Date modified*, *Type*, *Size*, plus new **Date created**, **Attributes** (R/H/S/A/…), **Dimensions** (images, videos), **Length** (audio, video), **Authors** and **Title**. *Reset columns* restores the Explorer defaults.
- Dimensions, length, authors and title come from the Windows property system (the same values Explorer shows). They are read in the background only for rows on screen; cloud-only files are skipped so nothing gets downloaded, and files are never kept locked.
- **Sorting by any column**, including the new ones. Sorting by a property column first reads the values for the whole folder, then re-sorts (as Explorer does).
- **Column widths, visible columns, sort and grouping are now saved** (also in the unpackaged dev/Personal build, where the Details sort was previously lost on restart). All Details panes (tabs, Dual/Quad) share the same columns live; each pane keeps its own sort.
- Header menu is localized (was English-only).

### Drag & drop like Windows Explorer (October 2026)

- **Drag starts anywhere on a row or tile** (text, icon or the empty part of the row), like Explorer's full-row select. The rubber-band selection now only starts on empty space.
- **Dragging an unselected item selects it** first (Ctrl adds it to the selection and drags the whole selection).
- **Right-button drag**: drop with the right mouse button to get Explorer's menu — *Copy here*, *Move here*, *Create shortcuts here*, *Cancel*. The default action is shown bold.
- **Default action for drags from Explorer**: same drive = move, other drive = copy (previously always copy).
- **Ctrl+Shift** while dragging creates shortcuts (in addition to Alt).
- **Auto-scroll** when dragging near the top/bottom (or left/right) edge of a list, in all views and Miller columns.
- **New drop targets**: drives in the sidebar, breadcrumb segments of the address bar, and the empty area behind the Miller columns (drops into the last column's folder).
- **Dragging out of Span no longer rearranges windows.** The previous "reveal" logic pushed Span behind other windows and raised whichever window the pointer reached first, which could cover the target you were aiming at and also broke drops on Span's own tab strip, address bar and sidebar near the window edges. Like Explorer, the drop now lands in whatever window is visible under the pointer, so a target sitting partly visible behind Span stays reachable.
- **Taskbar drag-hover works:** hover a taskbar button while dragging and Windows switches to that app; Span keeps it in front for the rest of the drag so the drop lands in it.

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