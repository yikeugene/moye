# Moye

An offline handwriting notebook for Windows 11. Moye combines pressure-sensitive ink, PDF annotation, text boxes, and images in a native WPF app designed for a touchscreen laptop and an active pen.

[Download Windows x64](https://github.com/yikeugene/moye/releases/latest/download/Moye-1.3.1-win-x64.zip) · [Releases](https://github.com/yikeugene/moye/releases/latest) · [User guide](docs/USER_GUIDE.md) · [Contributing](https://github.com/yikeugene/moye/blob/master/CONTRIBUTING.md)

## Get started

1. Download `Moye-1.3.1-win-x64.zip` from [Releases](https://github.com/yikeugene/moye/releases).
2. Extract the **entire** ZIP, then open `Moye.exe`. The portable package includes the .NET runtime; no separate .NET installation is needed.
3. On **Your Notebooks**, open a notebook cover or choose **New Notebook** and select Blank, Ruled or Grid paper. Select **Pen** and start writing. Use **Pen Settings** to change the color and width. A mouse works too.

Moye requires Windows 11 x64. An active pen compatible with Windows Ink is needed for pressure input. You can use the app and save notes without an account or an internet connection.

## Features

- Notebooks with titles and categories; search by either. Add, duplicate, reorder, and delete pages.
- Pressure-sensitive pen, highlighter, partial and whole-stroke erasers, and lasso selection. Move, resize, recolor, duplicate, or delete selected ink.
- Up to 100 undo and redo steps per notebook editing session.
- A4 blank, ruled, and grid paper; continuous pages, thumbnails, fit-page and fit-width views, and a focus mode.
- Editable text boxes, PNG/JPEG images, and pasted screenshots, with move and resize handles.
- PDF import and annotation, PDF export for sharing, and `.moye` backups that preserve editable content.
- Background autosave to a local SQLite database, with unsaved snapshots retained if a write fails.

The English interface puts the document title at the top, with **Pages** and **Notebooks** tabs in the sidebar. **All Notes** saves and returns to the notebook home screen; opening another notebook returns to its page thumbnails. **Insert** contains page, PDF, and image commands. **Page Options** contains page management and paper styles. Choose paper from visual template cards when adding notebooks or pages. **Fit Width**, beside **Fit Page** in the footer, fits paper to the writing area and responds to window/sidebar resizing. Click the zoom percentage for 100% view. The pen and highlighter keep separate colors while switching tools, and the eraser remembers its selected mode.

## Your notes stay local

Notes are stored in `%LOCALAPPDATA%\Moye\moye.db`, with SQLite journal files alongside it. Moving the app folder does not move your notebooks. Use **More → Back Up All Notebooks** to create a portable `.moye` backup. Restoring creates new copies and does not overwrite existing notebooks.

Backups include editable ink, pressure, text, images, page order, and original PDFs. They are not encrypted. Moye does not include cloud sync, recording, handwriting recognition, AI features, or an infinite canvas. Version 1.3 keeps the existing database and backup formats.

## Build from source

Use PowerShell on Windows, from the repository root:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
```

The scripts prefer `.tools\dotnet\dotnet.exe`, then look for an installed .NET 10 SDK. If neither is available, run `.\scripts\build.ps1 -InstallSdk` to install the SDK under `.tools` using Microsoft's official HTTPS installer. Initial SDK installation, package restore, and the first self-contained publish need internet access; normal use of the app does not.

CLI and NuGet caches are kept in `.tools\cli` and `.tools\nuget`. CLI telemetry is disabled. The scripts preserve your existing PowerShell security settings. Add `-NoRestore` once the required packages and runtime packs have been restored.

| Command | Result |
|---|---|
| `build.ps1 -Configuration Release` | Build the Release configuration. |
| `test.ps1 -Filter 'FullyQualifiedName~StorageTests'` | Run selected tests; TRX output is written to `artifacts\TestResults`. |
| `publish.ps1` | Read the version from the project and create `artifacts\Moye-win-x64`, a versioned ZIP, and its SHA-256 file. Close any app running from the output folder first. |
| `preview-ui.ps1` | Render the real WPF layout with an in-memory sample at two sizes, with a button-size report in `artifacts`. It does not open a desktop window or read your notes database. |

For an isolated test library, use `Moye.exe --data-dir .\sample-library`. The app creates a separate `moye.db` there.

## Verification and limitations

The automated suite covers notebooks, SQLite storage, autosave recovery, editable backups, ink operations, history, and PDF import/export. Detached WPF layout checks cover the notebook home, paper templates, and editor at two window sizes. These checks do not establish successful live pen, touch, or IME interaction. Build and validation status is available in [GitHub Actions](https://github.com/yikeugene/moye/actions); generated test results, layout images, and internal QA records stay local under ignored paths.

Physical pen feel, fast small handwriting, pressure response, palm rejection, touch gestures, alignment after zoom, pen buttons, and sleep recovery still need validation on real hardware. Direct Unicode text entry and persistence have been checked; real IME composition and candidate windows have not.

Encrypted PDFs, interactive forms, digital signatures, and internal or cross-page annotation actions are unsupported. Ordinary URI links can be retained. Newly added text boxes export as vector outlines, so their text cannot be selected or searched in the exported PDF; original PDF text retains its existing capabilities. Use `.moye` when you need an editable backup.

## License

Moye's original code is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [Third-party notices](docs/THIRD-PARTY-NOTICES.md). The portable distribution includes the applicable runtime and dependency notices.
