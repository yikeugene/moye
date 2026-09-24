# Moye

An offline handwriting notebook for Windows 11. Moye combines pressure-sensitive ink, PDF annotation, text boxes, and images in a native WPF app designed for a touchscreen laptop and an active pen.

[Download Windows installer](https://github.com/yikeugene/moye/releases/latest/download/Moye-1.12.0-Setup-win-x64.exe) · [Releases](https://github.com/yikeugene/moye/releases/latest) · [User guide](docs/USER_GUIDE.md) · [Roadmap](ROADMAP.md) · [Contributing](https://github.com/yikeugene/moye/blob/master/CONTRIBUTING.md)

## Get started

1. Download and run `Moye-1.12.0-Setup-win-x64.exe` from [Releases](https://github.com/yikeugene/moye/releases).
2. Complete the installer, then open **Moye** from the desktop shortcut it creates automatically. A Start menu shortcut is also added. The .NET runtime is included; no separate .NET installation or administrator account is needed.
3. On **Your Notebooks**, open a notebook cover or choose **New Notebook** and select Blank, Ruled, Grid, Dot Grid, Cornell or Graph paper. Choose a favorite pen and start writing. Open **Manage pens** to customize your tools, or **Pen Settings** to change the current color and width. A mouse works too.

Moye requires Windows 11 x64. An active pen compatible with Windows Ink is needed for pressure input. You can use the app and save notes without an account or an internet connection.

The installer places the app in `%LOCALAPPDATA%\Programs\Moye` by default. To update, close Moye and run the newer installer. To remove the app, use **Windows Settings → Apps → Installed apps → Moye → Uninstall**. Your notebooks in `%LOCALAPPDATA%\Moye` are retained. The installer is currently unsigned; the release includes a SHA-256 file for checking the download.

## Features

Moye 1.12.0 adds rounded context menus with icons, clearer focus and delete states, and section actions on right-click or **Shift+F10**. Rename, reorder or delete the section you clicked without changing the reading position when opening its menu. **Section Options** offers the same actions for the selected section.

Moye 1.11.0 refines button spacing, hover/pressed states, keyboard focus, favorite pen badges and dialog actions while keeping the existing green and warm-neutral workspace.

Moye 1.11.0 changes **Export** to save only the selected section, in its displayed page order, with **Notebook - Section.pdf** as the suggested filename. Earlier releases export the whole notebook. Editable `.moye` notebook backups still include all sections.

Version 1.11.0 also adds 21 fixed thickness levels with visible ticks and a live stroke preview in **Pen Settings** and **Manage pens**. Fine pen sizes have closer increments, including **0.35** and **0.45 mm**; broader sizes include **3 mm** for highlighting. Arrow keys select the next size. Existing custom preset widths remain exact until you adjust the slider.

Moye 1.8.0 adds a visual color palette for ink, presets and text, plus a draggable thickness control with a live stroke preview.

Version 1.8.0 also improves finger navigation: touch movement is combined per display update, one-finger swipes slow down after release, and thumbnail generation waits until scrolling settles. These changes have synthetic regression coverage; touch smoothness and pen/palm behavior still require device testing.

Moye 1.9.0 includes PDF compatibility fixes: exported pressure ink keeps its original fill rule, avoiding white holes at overlapping stroke contours. Import accepts harmless empty form metadata and reports the affected page when page validation fails.

Version 1.9.0 also adds **Insert → Import Document…** for DOCX, PPTX, PPSX, ODT and ODP files. DOCX needs installed Microsoft Word or LibreOffice; PPTX/PPSX need PowerPoint or LibreOffice; ODT/ODP need LibreOffice. Conversion runs locally and appends fixed PDF pages to the current section for annotation. The original file stays untouched; the library and backups retain the converted PDF. Moye does not bundle or download these converters. See [Office document import](docs/USER_GUIDE.md#office-document-import) for cancellation and format limits.

The **1.9.0 interface refresh** adds a warm neutral workspace, forest green controls, clearer notebook covers, readable secondary text and keyboard focus indicators. Search offers a clear action and distinct no-results guidance; notebook deletion moves into each card's options menu. In 1.9.0, **My notebooks** replaces **All Notes**, **Manage pens** replaces **Presets…**, and **Export** is a labelled header action. See the [updated navigation guide](docs/USER_GUIDE.md#notebooks-sections-and-pages). Native desktop checks on 2026-09-23 covered the new focus tools and page menus using synthetic notebooks. See the Release and GitHub Actions results for verification of the published commit; compatibility with every Windows application-control policy remains unverified.

- **Notebook → Section → Page**: use a notebook for a course, sections for topics, and pages for lecture notes.
- Notebooks with titles and categories; search by either. Delete a notebook from its home card or **More → Delete Notebook…**, with confirmation. Add, duplicate, reorder, and delete pages.
- Create, rename, reorder or delete sections; move pages between sections. Section changes support notebook undo/redo, local autosave and editable backups. Existing pages appear in **General**.
- Pressure-sensitive pen and highlighter with draw-and-hold straight lines, Pixel and Stroke erasers, and lasso selection. Move, resize, recolor, duplicate, or delete selected ink.
- Persistent named pen presets, with favorite buttons, drag-to-reorder and `1`–`9` shortcuts. Rename, duplicate, delete or hide favorites; adjust pen opacity, pressure sensitivity and smoothing.
- Independent eraser size, highlighter-only erasing, and a remembered mode for the pen's tail eraser.
- Editable ink copy/cut/paste between pages, `Ctrl+Shift+Z` redo, and temporary mouse panning while holding Space.
- Up to 100 undo and redo steps per notebook editing session.
- Six A4 paper templates: Blank, Ruled, Grid, Dot Grid, Cornell and Graph. Continuous pages, thumbnails, fit-page and fit-width views, and a focus mode.
- Editable text boxes, PNG/JPEG images, and pasted screenshots, with move and resize handles.
- PDF import and annotation, PDF export for sharing, and `.moye` backups that preserve editable content.
- Background autosave to a local SQLite database, with unsaved snapshots retained if a write fails.

The English interface puts the document title at the top, with **Contents** and **Notebooks** tabs in the sidebar. **My notebooks** saves and returns to the notebook home screen. **Insert** contains page, import, and image commands. **Fit Width**, beside **Fit Page**, fits paper to the writing area and responds to window/sidebar resizing.

**New in 1.10.0:** right-click a page thumbnail or the paper to duplicate, move, delete or change its paper style. Actions belong to the page you clicked. This replaces the separate **Page Options** button. `F11` hides the main editing chrome and keeps a compact floating toolbar for Pen, Highlighter, Eraser, Lasso, color/thickness, Undo, Redo and Exit Focus. Save errors remain accessible.

The starting presets are Black Pen **0.45 mm**, Blue Pen **0.45 mm**, Red Pen **0.35 mm** and Yellow Highlighter **3 mm**. Preset widths range from approximately **0.132292 to 6.35 mm**. Pen opacity is adjustable from **10% to 100%**; highlighters use native **50%** transparency. Use **Pen Settings → Save Current as Preset…** to keep a new tool. **Manage pens** manages up to 40 presets; the first nine favorites appear in the toolbar.

Paper previews, page backgrounds and exported PDFs use the same template geometry. Dot Grid offers dotted guides, Cornell separates cues, notes and a summary, and Graph uses fine squares with stronger major guides. New paper pages are A4 with fixed spacing; custom paper sizes, spacing and template management remain future work.

With Pen or Highlighter, pause at a line endpoint for about 0.65 seconds to straighten it, drag to adjust, then lift. **Pen Settings → Draw and Hold** toggles this behavior and remembers the choice. **Eraser Settings** offers Pixel or Stroke erasing, a **12–120 DIP** size control, and **Erase highlighter only**.

## Typing notes

Moye **1.6.0** adds a visible **Type** button and a text-formatting bar for typed notes alongside handwriting.

Click **Type** to start or continue a text box, then type directly. Click elsewhere on the page or use **＋ Text box** for another box. The text bar offers font family, size, bold, italic, color, alignment, and plain bullet or numbered line prefixes. Formatting applies to the **entire text box**, including when only a word is selected; this is not per-word rich text. Use `Ctrl+B` / `Ctrl+I` for bold and italic, and `Ctrl+Enter` or `Esc` to finish typing and return to Pen.

Text boxes grow down to the page boundary and scroll internally when their content exceeds that space. They do not flow automatically onto another page; continue in a new box on the next page to make the remaining text visible in the page layout and exported PDF. Text editing uses the native text control for clipboard, undo and IME handling. Basic typing, Chinese IME candidate selection and selected formatting/focus flows have passed a live desktop check; broader input and clipboard acceptance remains open.

## Your notes stay local

Notes are stored in `%LOCALAPPDATA%\Moye\moye.db`, with SQLite journal files alongside it. Moving the app folder does not move your notebooks. Use **More → Back Up All Notebooks** to create a portable `.moye` backup. Restoring creates new copies and does not overwrite existing notebooks.

Backups include editable ink, pressure, text, images, section/page order, and original PDFs. They are not encrypted. Writing tools and preferences are saved separately in `%LOCALAPPDATA%\Moye\writing-preferences.json`; they are not included in `.moye` notebook backups. Moye 1.12.0 keeps the same formats as 1.7.0–1.11.0 and reads older libraries and version 1 backups, assigning old pages to **General**. It upgrades libraries to database schema 2 and writes version 2 `.moye` backups. Moye 1.6.2 and earlier cannot open the upgraded library or new backups; use this build or a newer compatible one. See the [format guide](docs/FILE_FORMAT.md).

**If upgrading from 1.6.2 or earlier, export a backup with that version and keep it separately if you need to return to it.** Uninstalling the app does not reverse a library migration.

The [roadmap](ROADMAP.md) describes **41 feature areas**, including future work. Version 1.5.0 delivers the first slice of that plan: persistent tools, faster editing shortcuts and selected reliability improvements. The complete roadmap is not implemented. Infinite canvas is planned; the current editor uses fixed pages. Cloud sync, recording, handwriting recognition and AI features are also unavailable.

Autosave deadlines and temporary touch suppression now use elapsed time that is unaffected by system-clock adjustments. Equivalent default and explicit library paths share the same single-instance identity, helping prevent two app instances from opening the same library through different launch forms. Background thumbnail refresh also preserves the selected writing or eraser mode.

## Build from source

Use PowerShell on Windows, from the repository root:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish-installer.ps1 -InstallCompiler
```

The scripts prefer `.tools\dotnet\dotnet.exe`, then look for an installed .NET 10 SDK. If neither is available, run `.\scripts\build.ps1 -InstallSdk` to install the SDK under `.tools` using Microsoft's official HTTPS installer. Initial SDK installation, package restore, and the first self-contained publish need internet access; normal use of the app does not.

CLI and NuGet caches are kept in `.tools\cli` and `.tools\nuget`. CLI telemetry is disabled. The scripts preserve your existing PowerShell security settings. Add `-NoRestore` once the required packages and runtime packs have been restored. `-InstallCompiler` installs the pinned Inno Setup 7.1.0 compiler into `.tools` from its official release after verifying SHA-256; omit it on later builds.

| Command | Result |
|---|---|
| `build.ps1 -Configuration Release` | Build the Release configuration. |
| `test.ps1 -Filter 'FullyQualifiedName~StorageTests'` | Run selected tests; TRX output is written to `artifacts\TestResults`. |
| `publish.ps1` | Read the version from the project and create `artifacts\Moye-win-x64`, a versioned ZIP, and its SHA-256 file. Close any app running from the output folder first. |
| `publish-installer.ps1 -InstallCompiler` | Build the self-contained payload and `artifacts\Moye-1.12.0-Setup-win-x64.exe` with its SHA-256 file. |
| `test-installer.ps1 -AllowDesktopChanges` | In a disposable Windows account, verify installation, automatic shortcuts, reinstallation, uninstallation and retained notebook data. CI runs this automatically. |
| `preview-ui.ps1` | Render the real WPF layout with an in-memory sample at two sizes, with a button-size report in `artifacts`. It does not open a desktop window or read your notes database. |

For a separate library, use `Moye.exe --data-dir .\sample-library`. The app keeps its `moye.db` and writing preferences in that directory.

## Verification and limitations

The 1.11.0 release passed **447 automated tests** and **39 detached WPF layout scenes** in CI on **2026-09-23**. The current Release links verification for the complete 1.12.0 build, including updated page-menu layout assertions. Native interaction with the redesigned menus and section actions remains unverified; live acceptance of section export dialogs and the stepped slider also remains incomplete.

On **2026-09-23**, the 1.10.0 source passed **437 automated tests** and **39 detached WPF layout scenes**. Native mouse/keyboard checks covered the floating focus toolbar, tool settings and shortcuts, page context menus, duplicate/delete/undo, text editing menus and save/reopen. A synthetic six-page PDF verified navigation annotation import, unchanged original bytes and preserved appearance after export with internal actions removed. These checks used isolated synthetic libraries.

Before the UI refresh, the document-import source passed **408 automated tests** and **35 detached WPF layout scenes** on **2026-09-21**. Synthetic DOCX and PPTX documents were also converted using installed Microsoft 365 Word/PowerPoint (16.0.20326.20144), including Chinese text, images, tables and a hidden slide. Page dimensions, unchanged source bytes, save/reopen, editable backups and annotated PDF round trips passed. LibreOffice routing and failure behavior have automated coverage; actual LibreOffice conversion remains unverified on this machine.

On **2026-09-21**, the local 1.8.0 Release run passed **292 tests** with no failures or skips, and **33 detached WPF layout scenes** with no undersized visible controls, overlaps or clipping. Detached thumbnail scheduling checks also passed. These automated checks do not establish physical pen or touch interaction, or measured hardware frame rates.

Earlier mouse checks on **2026-09-21** covered the visual color controls, Apply/Cancel, thickness preview, saved ink/text/preset values after reopening a synthetic library, and a scroll/draw/thumbnail regression. They did not exercise physical finger or stylus input.

The 1.12.0 release workflow checks document validation/converter routing, PDF import/export, library search recovery, visual color selection, stroke-width gestures, touch movement/inertia, section navigation and undo, SQLite schema migration and rollback, save/reopen, editable backup formats 1 and 2, viewport stability, selected-section PDF ordering and export snapshot isolation. It also runs detached WPF layouts and executes the actual packaged app before accepting the installer. Installation and reinstallation checks verify automatic shortcuts and storage access; uninstallation checks retain synthetic notebook data. See [release notes](docs/RELEASE_NOTES_1.12.0.md) and [GitHub Actions](https://github.com/yikeugene/moye/actions) for results for the published commit.

SQLitePCLRaw 3.0.5 replaces the older dependency that Windows application control blocked on the affected machine. Local storage checks passed with the updated package without changing security settings. The app reports the underlying error and writes an error log beside the selected library when writable; compatibility with every device policy is not established.

A live Windows desktop check on **2026-09-14**, using an isolated sample notebook, verified Type creating/resuming one box, Chinese IME composition and candidate selection (`t`, then Space, committed `他`), switching to English input, `Ctrl+B`, setting 24 pt and continuing to type after Enter, bullet insertion, Enter list continuation, native `Ctrl+Z` undo of that continuation, and `Ctrl+Enter` returning to Pen. The packaged app reopened the saved text and formatting, accepted `T` from the page workspace with the Chinese IME active, and cancelled a short composition and an uncommitted font-size change with `Esc` while keeping Type mode.

Physical pen feel, fast small handwriting, pressure response, palm rejection, touch gestures, alignment after zoom, pen buttons, and sleep recovery still need validation on real hardware. Long or interrupted IME composition, broader candidate selection, text clipboard operations and cross-page input transitions also remain unverified by this desktop check.

Moye 1.10.0 accepts supported PDF annotations containing internal links or other actions. It retains the original PDF bytes in the library and backups, and preserves annotation appearances. Exported copies remove internal destinations and interactive actions so reordered or duplicated pages do not retain broken jumps; ordinary URI links remain.

Encrypted PDFs, interactive forms, digital signatures and unsupported interactive annotation types remain unsupported. Newly added text boxes export as vector outlines, so their text cannot be selected or searched in the exported PDF; original PDF text retains its existing capabilities. Use `.moye` when you need an editable backup.

## License

Moye's original code is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [Third-party notices](docs/THIRD-PARTY-NOTICES.md). The portable distribution includes the applicable runtime and dependency notices.
