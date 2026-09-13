# Moye offscreen WPF layout preview

Run from the repository root on Windows:

```powershell
./scripts/preview-ui.ps1
```

This tool uses the existing .NET SDK and package cache in `.tools` to build a separate STA console renderer. It creates the real `MainWindow`, detaches its `Content`, and renders it with the application's `App.xaml` resources, an in-memory notebook fixture, and `PageEditor` through `Measure`, `Arrange`, and `RenderTargetBitmap`.

The tool does not call `Window.Show` or `Application.Run`, send input, capture the desktop, use a UI Automation client, or read or write a user database. It checks that the content has no `PresentationSource` and the main window's native handle remains zero. The three ruled, grid, and blank pages and their English text are synthetic test data.

Startup is initialized through the real view model and must leave the library visible without opening a notebook. The renderer captures the populated library, a first-run empty library, and a search with no results, then explicitly opens the fixture through `OpenAsync` to capture the editor at Fit Page and Fit Width. Separate detached previews show the reusable paper picker with Ruled selected and the real New Notebook dialog content. The dialog is constructed without an owner and never shown.

Output files in `artifacts/`:

- `ui-preview-1400.png`: 1400 × 960 DIP.
- `ui-preview-1024.png`: 1024 × 700 DIP.
- `ui-preview-fit-width-{1400,1024}.png`: the editor with paper fitted to the workspace width.
- `ui-preview-library-{1400,1024}.png`: notebook selection home.
- `ui-preview-library-empty-{1400,1024}.png`: empty library with a create action.
- `ui-preview-library-search-{1400,1024}.png`: search with no matching notebook.
- `ui-preview-paper-templates.png`: the three visual paper choices, 454 × 260 DIP.
- `ui-preview-new-notebook.png`: actual dialog content at 544 DIP wide, measured to its desired height within the desktop work area.
- `ui-preview-layout.md` and `ui-preview-layout.json`: button bounds, 44 DIP checks, overlaps, and controls outside the content area.

These images support internal layout review; they do not validate live UI interaction. Touch, pen input, Windows scaling, the system title bar, popup menus, and keyboard focus require separate verification. Images are rendered at 96 DPI, and their dimensions describe the content area without operating-system window borders. Button checks use declared `Visibility` and ancestor visibility because `IsVisible` does not describe layout in an offscreen tree without a presentation source.

Each scene must contain its expected controls: the home requires New Notebook; the editor requires Pen, Pen Settings, and Fit Width; the paper picker requires three radio choices with exactly one selected. Touch-target checks include the template radio cards and regular buttons, excluding native scrollbar parts. The process exits unsuccessfully when required controls are missing or button bounds are too small, overlapping, or outside the rendered area.

Fit Width validation measures the realized page host and editor in viewport coordinates: their horizontal edges must align, each page gutter must be between 0 and 60 DIP, and the page must cover at least 85% of the viewport width. This verifies the rendered result rather than only comparing numeric zoom formulas. The dialog preview checks its real template choices and Create Notebook action using the same button layout rules.
