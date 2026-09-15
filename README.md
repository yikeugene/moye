# Moye

An offline handwriting notebook for Windows 11. Moye combines pressure-sensitive ink, PDF annotation, text boxes, and images in a native WPF app designed for a touchscreen laptop and an active pen.

[Download Windows installer](https://github.com/yikeugene/moye/releases/latest/download/Moye-1.6.2-Setup-win-x64.exe) · [Releases](https://github.com/yikeugene/moye/releases/latest) · [User guide](docs/USER_GUIDE.md) · [Roadmap](ROADMAP.md) · [Contributing](https://github.com/yikeugene/moye/blob/master/CONTRIBUTING.md)

## Get started

1. Download and run `Moye-1.6.2-Setup-win-x64.exe` from [Releases](https://github.com/yikeugene/moye/releases).
2. Complete the installer, then open **Moye** from the desktop shortcut it creates automatically. A Start menu shortcut is also added. The .NET runtime is included; no separate .NET installation or administrator account is needed.
3. On **Your Notebooks**, open a notebook cover or choose **New Notebook** and select Blank, Ruled, Grid, Dot Grid, Cornell or Graph paper. Choose a favorite pen and start writing. Open **Presets…** to customize your tools, or **Pen Settings** to change the current color and width. A mouse works too.

Moye requires Windows 11 x64. An active pen compatible with Windows Ink is needed for pressure input. You can use the app and save notes without an account or an internet connection.

The installer places the app in `%LOCALAPPDATA%\Programs\Moye` by default. To update, close Moye and run the newer installer. To remove the app, use **Windows Settings → Apps → Installed apps → Moye → Uninstall**. Your notebooks in `%LOCALAPPDATA%\Moye` are retained. The installer is currently unsigned; the release includes a SHA-256 file for checking the download.

## Features

- Notebooks with titles and categories; search by either. Delete a notebook from its home card or **More → Delete Notebook…**, with confirmation. Add, duplicate, reorder, and delete pages.
- Pressure-sensitive pen and highlighter with draw-and-hold straight lines, Pixel and Stroke erasers, and lasso selection. Move, resize, recolor, duplicate, or delete selected ink.
- Persistent named pen presets, with favorite buttons, drag-to-reorder and `1`–`9` shortcuts. Rename, duplicate, delete or hide favorites; adjust pen opacity, pressure sensitivity and smoothing.
- Independent eraser size, highlighter-only erasing, and a remembered mode for the pen's tail eraser.
- Editable ink copy/cut/paste between pages, `Ctrl+Shift+Z` redo, and temporary mouse panning while holding Space.
- Up to 100 undo and redo steps per notebook editing session.
- Six A4 paper templates: Blank, Ruled, Grid, Dot Grid, Cornell and Graph. Continuous pages, thumbnails, fit-page and fit-width views, and a focus mode.
- Editable text boxes, PNG/JPEG images, and pasted screenshots, with move and resize handles.
- PDF import and annotation, PDF export for sharing, and `.moye` backups that preserve editable content.
- Background autosave to a local SQLite database, with unsaved snapshots retained if a write fails.

The English interface puts the document title at the top, with **Pages** and **Notebooks** tabs in the sidebar. **All Notes** saves and returns to the notebook home screen. **Insert** contains page, PDF, and image commands; **Page Options** contains page management and paper styles. **Fit Width**, beside **Fit Page**, fits paper to the writing area and responds to window/sidebar resizing. `F11` hides the header, toolbar, sidebar and footer, while keeping **Exit Focus** and save errors available.

The starting presets are Black Pen **0.45 mm**, Blue Pen **0.45 mm**, Red Pen **0.35 mm** and Yellow Highlighter **3 mm**. Preset widths range from approximately **0.132292 to 6.35 mm**. Pen opacity is adjustable from **10% to 100%**; highlighters use native **50%** transparency. Use **Pen Settings → Save Current as Preset…** to keep a new tool. **Presets…** manages up to 40 presets; the first nine favorites appear in the toolbar.

Paper previews, page backgrounds and exported PDFs use the same template geometry. Dot Grid offers dotted guides, Cornell separates cues, notes and a summary, and Graph uses fine squares with stronger major guides. New paper pages are A4 with fixed spacing; custom paper sizes, spacing and template management remain future work.

With Pen or Highlighter, pause at a line endpoint for about 0.65 seconds to straighten it, drag to adjust, then lift. **Pen Settings → Draw and Hold** toggles this behavior and remembers the choice. **Eraser Settings** offers Pixel or Stroke erasing, a **12–120 DIP** size control, and **Erase highlighter only**.

## Typing notes

Moye **1.6.0** adds a visible **Type** button and a text-formatting bar for typed notes alongside handwriting.

Click **Type** to start or continue a text box, then type directly. Click elsewhere on the page or use **＋ Text box** for another box. The text bar offers font family, size, bold, italic, color, alignment, and plain bullet or numbered line prefixes. Formatting applies to the **entire text box**, including when only a word is selected; this is not per-word rich text. Use `Ctrl+B` / `Ctrl+I` for bold and italic, and `Ctrl+Enter` or `Esc` to finish typing and return to Pen.

Text boxes grow down to the page boundary and scroll internally when their content exceeds that space. They do not flow automatically onto another page; continue in a new box on the next page to make the remaining text visible in the page layout and exported PDF. Text editing uses the native text control for clipboard, undo and IME handling. Basic typing, Chinese IME candidate selection and selected formatting/focus flows have passed a live desktop check; broader input and clipboard acceptance remains open.

## Your notes stay local

Notes are stored in `%LOCALAPPDATA%\Moye\moye.db`, with SQLite journal files alongside it. Moving the app folder does not move your notebooks. Use **More → Back Up All Notebooks** to create a portable `.moye` backup. Restoring creates new copies and does not overwrite existing notebooks.

Backups include editable ink, pressure, text, images, page order, and original PDFs. They are not encrypted. Writing tools and preferences are saved separately in `%LOCALAPPDATA%\Moye\writing-preferences.json`; they are not included in `.moye` notebook backups. Version **1.6.2** keeps the existing notebook and backup formats.

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
| `publish-installer.ps1 -InstallCompiler` | Build the self-contained payload and `artifacts\Moye-1.6.2-Setup-win-x64.exe` with its SHA-256 file. |
| `test-installer.ps1 -AllowDesktopChanges` | In a disposable Windows account, verify installation, automatic shortcuts, reinstallation, uninstallation and retained notebook data. CI runs this automatically. |
| `preview-ui.ps1` | Render the real WPF layout with an in-memory sample at two sizes, with a button-size report in `artifacts`. It does not open a desktop window or read your notes database. |

For a separate library, use `Moye.exe --data-dir .\sample-library`. The app keeps its `moye.db` and writing preferences in that directory.

## Verification and limitations

The local automated run on **2026-09-14** passed **184 tests and 21 detached UI scenes**. The suite covers notebooks, deletion and failed-save recovery, SQLite storage, editable backups, ink operations, history, writing-preference persistence, typing helpers and PDF import/export. Detached WPF layout checks cover the notebook home, paper templates, settings, editor and text controls. Automated checks alone do not establish successful live pen, touch, or IME interaction. See [release notes](docs/RELEASE_NOTES_1.6.2.md) and [GitHub Actions](https://github.com/yikeugene/moye/actions) for release validation.

A live Windows desktop check on **2026-09-14**, using an isolated sample notebook, verified Type creating/resuming one box, Chinese IME composition and candidate selection (`t`, then Space, committed `他`), switching to English input, `Ctrl+B`, setting 24 pt and continuing to type after Enter, bullet insertion, Enter list continuation, native `Ctrl+Z` undo of that continuation, and `Ctrl+Enter` returning to Pen. The packaged app reopened the saved text and formatting, accepted `T` from the page workspace with the Chinese IME active, and cancelled a short composition and an uncommitted font-size change with `Esc` while keeping Type mode.

Physical pen feel, fast small handwriting, pressure response, palm rejection, touch gestures, alignment after zoom, pen buttons, and sleep recovery still need validation on real hardware. Long or interrupted IME composition, broader candidate selection, text clipboard operations and cross-page input transitions also remain unverified by this desktop check.

Encrypted PDFs, interactive forms, digital signatures, and internal or cross-page annotation actions are unsupported. Ordinary URI links can be retained. Newly added text boxes export as vector outlines, so their text cannot be selected or searched in the exported PDF; original PDF text retains its existing capabilities. Use `.moye` when you need an editable backup.

## License

Moye's original code is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [Third-party notices](docs/THIRD-PARTY-NOTICES.md). The portable distribution includes the applicable runtime and dependency notices.
