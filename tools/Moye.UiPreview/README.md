# Moye offscreen WPF layout preview

Run from the repository root on Windows:

```powershell
./scripts/preview-ui.ps1
```

This tool uses the existing .NET SDK and package cache in `.tools` to build a separate STA console renderer. It creates the real `MainWindow`, detaches its `Content`, and renders it with the application's `App.xaml` resources, an in-memory notebook fixture, and `PageEditor` through `Measure`, `Arrange`, and `RenderTargetBitmap`.

The tool does not call `Window.Show` or `Application.Run`, send input, capture the desktop, use a UI Automation client, or read or write a user database. It checks that the content has no `PresentationSource` and the main window's native handle remains zero. The three ruled, grid, and blank pages and their English text are synthetic test data.

Startup is initialized through the real view model and must leave the library visible without opening a notebook. The renderer captures a four-notebook library with several categories and a long title, a first-run empty library, and a search with no results, then explicitly opens the fixture through `OpenAsync` to capture the editor at Fit Page and Fit Width. A library that exceeds the available height is also captured after scrolling to its last notebook. Separate detached previews show the reusable paper picker with Ruled selected and the real New Notebook dialog content. The dialog is constructed without an owner and never shown.

Output files in `artifacts/`:

- `ui-preview-1400.png`: 1400 × 960 DIP.
- `ui-preview-1024.png`: 1024 × 700 DIP.
- `ui-preview-fit-width-{1400,1024}.png`: the editor with paper fitted to the workspace width.
- `ui-preview-typing-{1400,1024}.png`: Type mode with the contextual font, size, style, alignment and list toolbar.
- `ui-preview-typing-empty.png` and `ui-preview-typing-overflow.png`: the first text box on an empty page and the warning for text exceeding a page.
- `ui-preview-library-{1400,1024}.png`: notebook selection home.
- `ui-preview-library-bottom-{1400,1024}.png`: the last notebook when the library needs vertical scrolling (generated only for overflowing layouts).
- `ui-preview-library-empty-{1400,1024}.png`: empty library with a create action.
- `ui-preview-library-search-{1400,1024}.png`: search with no matching notebook.
- `ui-preview-library-empty-search-{1400,1024}.png`: a search in a library with no notebooks, retaining the Clear Search recovery action.
- `ui-preview-paper-templates.png`: the six visual paper choices, 454 × 374 DIP.
- `ui-preview-focus-{1400,1024}.png`: focus chrome with the paper viewport reclaiming the header, tools and footer space.
- `ui-preview-presets.png`: the actual preset manager content, 744 × 620 DIP. A round-trip check preserves a minimum-width pen and 65% opacity.
- `ui-preview-new-notebook.png`: actual dialog content at 544 DIP wide, measured to its desired height within the desktop work area.
- `ui-preview-eraser-settings.png`: the actual Eraser Settings popup content at 330 DIP wide, with Pixel Eraser and Stroke Eraser explanations.
- `ui-preview-pen-settings.png`: the actual Pen Settings popup content at 310 DIP wide, including the Draw and Hold checkbox.
- `ui-preview-document-import-{1400,1024}.png`: document conversion progress with an enabled Cancel Import button above the busy overlay.
- `ui-preview-layout.md` and `ui-preview-layout.json`: button bounds, 44 DIP checks, overlaps, and controls outside the content area.

These images support internal layout review; they do not validate live UI interaction. Touch, pen input, Windows scaling, the system title bar, popup menus, and keyboard focus require separate verification. Images are rendered at 96 DPI, and their dimensions describe the content area without operating-system window borders. Button checks use declared `Visibility` and ancestor visibility because `IsVisible` does not describe layout in an offscreen tree without a presentation source.

Each scene must contain its expected controls: the home requires New Notebook and notebook options; the editor requires every writing tool, Pen Settings, Undo, Redo, zoom controls, Fit Width, and Focus Mode; focus requires Exit Focus; the paper picker requires six radio choices with exactly one selected; the eraser popup requires both eraser mode buttons; the pen popup requires Draw and Hold. Required actions must be visible without horizontal scrolling, including at 1024 × 700 DIP. Touch-target checks include the template radio cards, checkboxes and regular buttons, excluding native scrollbar parts. The process exits unsuccessfully when required controls are missing or button bounds are too small, overlapping, or outside the rendered area.

Library checks distinguish the first-run Create a Notebook action from the no-results Clear Search action, verify the current count and explanation are displayed, and require the visible create/search/recovery controls to be keyboard tab stops with readable labels and 44 DIP targets. Both clear-search buttons are invoked through their real routed click handlers; all four notebook summaries must return. Title and category matching are checked without case sensitivity. These detached checks verify command behavior and declared keyboard accessibility, not actual native focus or key routing.

Each notebook options menu is assigned its real placement target without opening it; the menu and both Open/Delete entries must receive the matching notebook summary. Neither action is invoked. The four cover backgrounds must resolve to four distinct colors through the actual item templates. Searching an empty library must still offer Clear Search; clearing it must restore the first-run create action without adding a notebook.

The import scene also hit-tests the rendered center of Cancel Import and checks that the enabled button is above any blocking overlay. This is a detached visual-tree check; it does not start or cancel an actual document conversion.

Settings previews detach the real `Popup.Child` while `IsOpen` remains false, inherit the main-window typography, and use the content's natural height. They render content without creating a native popup or invoking click/keyboard handlers. The Pixel Eraser highlight represents the application's initial remembered mode because the main window's Loaded handler is intentionally not dispatched.

Typing previews call the real window commands against a registered synthetic page editor. They verify that Type resumes text without duplicates, formatting updates the model, new boxes copy style without note content, and overflow raises a warning. The font, size and alignment pickers must also be at least 44 DIP. These command checks do not exercise native focus, input-method composition or keyboard routing.

Fit Width validation measures the realized page host and editor in viewport coordinates: their horizontal edges must align, each page gutter must be between 0 and 60 DIP, and the page must cover at least 85% of the viewport width. This verifies the rendered result rather than only comparing numeric zoom formulas. The dialog preview checks its real template choices and Create Notebook action using the same button layout rules.

For separate live desktop checks, run the built preview executable with the repository root followed by `--interactive`; append `--compact` to start at the application's 1024 × 700 minimum window size. This opens the real application shell with a disposable in-memory notebook repository; it does not access SQLite or the user's notebook library. Writing preferences are kept under `artifacts/interactive-preview/`. Use this mode to check Tab/Shift+Tab, visible focus, search recovery, tool selection, menus, dialogs, and focus mode. Close the synthetic window before rebuilding the preview.
