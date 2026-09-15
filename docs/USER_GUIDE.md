# Moye user guide

This guide describes the English interface in Moye 1.6.2 for Windows 11 x64.

Version 1.6.0 adds the **Type** button and text-formatting workflow described below.

## Install, update and uninstall

Download `Moye-1.6.2-Setup-win-x64.exe` from the GitHub Release and run it. Setup installs Moye for your Windows account and automatically creates desktop and Start menu shortcuts. No administrator password or separate .NET runtime installation is required. The default application folder is `%LOCALAPPDATA%\Programs\Moye`.

Open the **Moye** desktop shortcut after installation. When updating, close the app and run the newer installer. Existing notes stay in `%LOCALAPPDATA%\Moye`; the installer does not move or replace them. If you previously used a portable ZIP with the default library, the installed app uses that same library. A custom `--data-dir` library still needs its custom launch argument.

Remove Moye through **Windows Settings → Apps → Installed apps → Moye → Uninstall**. This removes installed program files and shortcuts while retaining notebook data and writing preferences. Use Moye's backup commands to make a portable copy of your notes.

## Notebooks and pages

Open `Moye.exe` to see **Your Notebooks**. Moye always starts on this home screen and waits for you to choose a notebook. An empty library shows a **Create a Notebook** button; it does not create a notebook automatically.

Click a notebook cover to open it, or **New Notebook** to choose a name, category, and first-page paper. The visual template cards offer six choices; new notebooks initially select Ruled. Canceling leaves the library unchanged.

| Paper | Layout |
|---|---|
| Blank | White paper without guides. |
| Ruled | Horizontal lines for writing. |
| Grid | Even square guides for diagrams and calculations. |
| Dot Grid | Evenly spaced dots for flexible layouts. |
| Cornell | A cue column, ruled notes area and summary area. |
| Graph | Fine squares with stronger major guides. |

New paper pages are A4, and each template uses fixed spacing. The same geometry appears in page backgrounds, thumbnails and exported PDFs. Custom sizes, adjustable spacing and saved custom templates remain future work.

Click **All Notes** to save your changes and return home. You can also switch notebooks using the editor's **Notebooks** sidebar tab. Opening a notebook shows its first page and the **Pages** thumbnails. Click the document title, or choose **More → Rename and Category**, to change its title and category. Search covers notebook titles and categories, not handwriting recognition or full-text search. The home screen also offers **Restore Backup** and **Library Options → Back Up All Notebooks**.

Select a page thumbnail, then use **Insert → Add Page**, or **Add Page** beneath the thumbnails. Choose a paper preview and click **Add Page** to insert after the current page. The picker starts with the current ordinary page's style, or Ruled when viewing a PDF. The **Page Options** menu above the thumbnails lets you duplicate, move, or delete the selected page. Page deletion can be undone. Deleting the last page leaves a new blank page.

Choose **Page Options → Paper Style**, select a visual template, and click **Apply Paper** to change an existing page. Your writing, text and images stay in place. This changes only the current ordinary page; PDF pages keep their original background.

To delete a notebook, click **Delete Notebook…** beneath its cover on the home screen, or open it and choose **More → Delete Notebook…**. The confirmation names the notebook and defaults to **No**. Choosing **Yes** deletes that notebook and all its pages; this cannot be undone. Back it up first if you need a copy. Deleting the open notebook returns you to the home screen. If saving or deletion fails, the notebook stays available so you can retry.

## Pen presets

The favorite toolbar starts with four tools:

| Preset | Thickness |
|---|---:|
| Black Pen | 0.45 mm |
| Blue Pen | 0.45 mm |
| Red Pen | 0.35 mm |
| Yellow Highlighter | 3 mm |

Click a favorite or press its displayed number, `1`–`9`, to select it. The toolbar shows the first nine presets marked as favorites. Drag a favorite onto another to change their order. If a favorite is outside the visible area, scroll the toolbar horizontally.

Use **Pen Settings → Save Current as Preset…** to name and save a new tool from the current writing settings. Open **Presets…** to rename a preset, choose Pen or Highlighter, edit its color and thickness, duplicate it, delete it, or move it with **↑** and **↓**. Clear **Show in favourite toolbar** to hide a preset without deleting it. **Save and Use** applies your changes and activates the selected preset, including hidden presets; **Cancel** leaves your saved presets unchanged. You can keep up to 40 presets and must retain at least one.

Thickness is shown in millimeters, from approximately **0.132292 to 6.35 mm**. The app stores widths in fixed page coordinates, so zooming changes their display size rather than the saved stroke width. Pens support **10–100% opacity**, **Pressure sensitivity**, and **Stroke smoothing**. Highlighters use native **50% transparency**; their opacity field is not adjustable. Color input accepts `#RRGGBB` or `#AARRGGBB`.

Presets, their order and favorites, the last selected preset, eraser settings, and Draw and Hold are saved locally between launches. They are writing preferences rather than notebook content; see [Autosave and backups](#autosave-and-backups) for their separate settings file.

## Writing and touch

Select **Pen** or **Highlighter**, then open **Pen Settings** to choose a color and width. The two tools retain separate colors when you switch between them. Pressure input requires a Windows Ink-compatible active pen. A mouse can also draw.

Page thumbnails update in the background without changing your selected writing or eraser mode.

Finger input is reserved for navigation: one finger scrolls and two fingers zoom. Page gestures pause while the pen is down. Pen response and palm rejection depend on your laptop, pen, and drivers, and still need validation on the device you use.

With **Pen** or **Highlighter**, draw a line and keep the tip down near its endpoint for about **0.65 seconds**. It straightens while you are still holding. Continue dragging to adjust its length and angle, then lift to finish. The same gesture works by holding the left mouse button. Pressure and ink appearance are retained, and the result saves as one editable stroke. Short marks, circles, and strongly curved handwriting stay freehand. **Pen Settings → Draw and Hold** is enabled by default; turn it off for uninterrupted freehand drawing. This switch applies to both Pen and Highlighter and is remembered between launches.

Click the eraser icon to open **Eraser Settings**:

- **Pixel Eraser** removes only the touched portion of ink, leaving the surviving fragments editable. It does not erase the paper, PDF background, text boxes, or images.
- **Stroke Eraser** removes the entire ink stroke when you touch any part of it.

The toolbar label shows **Pixel** or **Stroke**. Click the eraser again to close its picker, or select a mode and continue writing. Press `E` to recall the last eraser mode. **Eraser Size** independently adjusts the erasing area from **12 to 120 DIP**, with a default of 20 DIP. Turn on **Erase highlighter only** to protect ordinary pen strokes while removing highlighting. Both modes remain undoable. Size, mode and the highlighter-only choice are remembered between launches.

The remembered mode also applies to the pen's tail eraser when the device sends inverted-pen events. Actual tail-eraser and side-button behavior still depends on your device and drivers.

Use **Lasso** to circle ink. Drag the selection to move it, or drag its boundary handles to resize it. Press `Ctrl+D` to duplicate or `Delete` to remove the selection. With ink selected, you can also change its color or thickness through **Pen Settings**.

Press `Ctrl+C` to copy selected ink, or `Ctrl+X` to cut it. Open another page and press `Ctrl+V` to paste an editable copy with its pressure and stroke attributes. These commands transfer selected **ink**; they do not copy an entire page or a mixed selection of text boxes and images. If the clipboard contains no Moye ink, `Ctrl+V` falls back to image/screenshot pasting. Text boxes keep their normal text clipboard and IME behavior while being edited.

## Viewing a page

Notebooks open in a fit-page view. The footer combines page navigation, save status, and zoom controls. **Fit Page** shows the whole sheet. **Fit Width**, directly beside it, expands the paper to the available writing area's width with a small margin on both sides; scroll vertically to see the rest of the page. While Fit Width is active, resizing the window or showing/hiding the sidebar refits the paper. **−**, **+**, pinch zoom, `Ctrl+mouse wheel`, or **Fit Page** leave width-fitting mode. Click the percentage for zoom options, including **Actual Size · 100%**.

The sidebar button or `F9` hides and shows the sidebar. Hold **Space** and drag with the left mouse button to pan temporarily; release Space to return to your writing tool.

The **Focus Mode** icon or `F11` switches to a full-screen writing workspace and hides the notebook header, writing toolbar, sidebar and footer. **Exit Focus** remains available, and save errors remain visible. Click **Exit Focus**, press `F11`, or press `Esc` to leave. When typing in a text box, `Esc` first leaves text editing. These controls change the display, not the paper's dimensions.

## Typing notes

Click **Type** to begin typing on the current page or continue editing the selected or most recently used text box on that page. If no text box exists, Moye creates one. Click an existing box to place the caret there. To add another box, use **＋ Text box** in the text bar or click an empty part of the page while Type is selected.

The text bar controls the selected box's font family, font size (**6–96 pt**), bold, italic, color and Left/Center/Right alignment. **All formatting applies to the entire box**, even if you have selected just one word. To use different formatting for a heading and body, create separate text boxes.

**• List** and **1. List** toggle plain text prefixes on the current line or selected lines. `Enter` continues a marked line; pressing `Enter` on an empty marked item ends the list. The markers remain editable characters, not structured rich-text list objects or an automatic outline.

While editing an ordinary paragraph, `Enter` inserts a line break. `Ctrl+B` toggles bold for the box and `Ctrl+I` toggles italic. `Ctrl+Enter` or `Esc` finishes typing, moves focus out of the text box and returns to Pen. Standard text selection, copy/cut/paste and undo continue to act on the text editor; notebook ink shortcuts do not replace them. Finish typing before using notebook Undo to reverse box formatting. Windows IME composition uses the native text control. A live desktop check confirmed basic Chinese candidate selection followed by English typing; the broader input checks below remain open.

A box grows vertically while you type, up to the bottom of its page. Additional text scrolls within the box. It does **not** create a continuation on the next page. Move the overflow into another text box on the next page when you need the full text visible in the page layout or PDF output.

Font, size, bold, italic, color and alignment are stored with the text box. Existing notes remain compatible. A `.moye` backup keeps text editable; added PDF text is still exported as vector outlines rather than searchable or selectable text.

## Images and object placement

Choose **Insert → Insert Image** for PNG or JPEG files. When you are not editing text, `Ctrl+V` pastes Moye ink if present on the clipboard, otherwise an image or screenshot.

Choose **Select** and click a text box or image. Drag its upper-right move handle to reposition it, or its lower-right handle to resize it. Press `Ctrl+D` to duplicate the selected object or `Delete` to remove it.

Images are limited to 20,000 pixels per side and 80 million pixels in total. The original image bytes are retained; resizing its display does not rewrite the source.

## PDF annotation and export

Choose **Insert → Import PDF** to insert the document's pages after the selected page. The original PDF is stored in your local library. You can add ink, highlighting, text boxes, and images over it.

Click the share icon at the top, whose tooltip is **Share: Export PDF**, to export the whole notebook. Added ink and text become PDF page content rather than editable Moye objects. New text boxes are exported as vector outlines: their appearance is retained, but the exported text cannot be selected or searched. Original PDF text retains its existing capabilities. Keep a `.moye` backup if you need to edit the contents later.

Encrypted documents, interactive forms, digital signatures, and internal or cross-page annotation actions are not supported. If appropriate, flatten existing annotations in another PDF tool before importing. Ordinary URI links and supported page annotations can be retained. Moye displays an error for unsupported or damaged PDFs.

## Autosave and backups

Completed edits are queued for background saving, with a coalescing delay of at most two seconds. Completion time depends on the disk and document size. The saved status appears only after a successful database transaction. Switching notebooks, leaving the window, and closing normally also attempt to save.

The default library is `%LOCALAPPDATA%\Moye\moye.db`. SQLite may create `moye.db-wal` and `moye.db-shm` beside it. Do not move only the database or delete its journal files while the app is open. Version **1.6.2** keeps existing notebooks and `.moye` backups compatible and preserves text-box formatting when saving and restoring.

Writing preferences are saved separately in `%LOCALAPPDATA%\Moye\writing-preferences.json`. This file contains presets and writing settings, and is **not included in `.moye` backups**. For a library started with `--data-dir`, both the database and preferences stay in that selected directory. A damaged settings file is preserved before defaults are offered; an unreadable or unsupported-version file is protected from replacement. A writing-settings warning offers details and retry when available. When an existing preferences file cannot be read or belongs to a newer version, tool changes apply to the current session only; notebooks still save and the app can close normally. Restart after resolving that file. A later write failure retains pending settings for retry.

Autosave deadlines and temporary touch suppression use elapsed-time measurements, so changing the system clock does not extend those waiting periods. Launching the same library through equivalent default or explicit paths uses a shared single-instance identity.

Use the **More** menu to move or preserve your notes:

- **Back Up This Notebook** exports the current notebook, including its current unsaved content.
- **Back Up All Notebooks** exports the library, including pending revisions.
- **Restore .moye Backup** validates the backup and creates new notebooks with new identifiers. Existing notebooks are not overwritten.

A `.moye` backup preserves ink, pressure, editable text, images, page order, and original PDFs. Its uncompressed contents are limited to 2 GB, with a 512 MB limit per asset and 64 MB per notebook metadata file or page of ISF ink. Backups are not encrypted.

If saving fails, pending content stays in memory. Use **Retry Save**, or export a `.moye` backup to a writable location. Normal closing keeps the window open if pending content cannot be saved. Force-quitting can lose recent uncommitted edits. Keep regular backups in another storage location.

## Keyboard shortcuts

| Action | Shortcut |
|---|---|
| Pen / highlighter / eraser in its selected mode | `B` / `H` / `E` |
| Lasso / Type / select objects | `L` / `T` / `V` |
| First nine favorite presets | `1`–`9` or numeric keypad `1`–`9` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` or `Ctrl+Shift+Z` |
| Duplicate selected ink or object | `Ctrl+D` |
| Select all ink on the current page | `Ctrl+A` |
| Delete the selection | `Delete` |
| Copy / cut selected editable ink | `Ctrl+C` / `Ctrl+X` |
| Paste editable ink, otherwise an image | `Ctrl+V` |
| Save now | `Ctrl+S` |
| Show or hide the sidebar | `F9` |
| Focus mode | `F11` |
| Zoom | `Ctrl+mouse wheel` |
| Temporary mouse pan | Hold `Space` and drag with the left mouse button |
| Bold / italic for the whole text box while typing | `Ctrl+B` / `Ctrl+I` |
| Finish typing | `Ctrl+Enter` |
| Leave text-box focus | `Esc` |

## Device checks still needed

Physical pen and touch validation remains outstanding. A live desktop check on 2026-09-14 confirmed basic Chinese IME composition and candidate selection, English typing, Type resume, bold, size entry with restored text focus, bullet continuation and its native undo, and Ctrl+Enter returning to Pen. The packaged app also reopened saved text/formatting, accepted T from the page workspace with the Chinese IME active, and cancelled short composition and font-size edits with Esc. This covers a short typing session; the following device and broader input checks remain open:

1. Fast continuous writing, small characters, light and heavy pressure, highlighting, and both eraser modes.
2. Resting a palm before and after pen contact, without producing finger ink or moving the page.
3. One-finger scrolling, two-finger zoom, and pen alignment after scrolling and zooming.
4. Moving the pen beyond the page, lifting it, switching windows, and resuming from sleep without a stuck input state.
5. The specific pen's tail eraser and side buttons, which depend on driver events.
6. Long or interrupted IME composition, additional candidate choices, text clipboard operations, and retaining composition/focus correctly across page changes. The short candidate and cancellation checks do not establish these longer or interrupted workflows.
7. Draw-and-hold preview before lifting, endpoint adjustment, freehand release, pressure variation, and pen-up/capture-loss cleanup at different zoom levels. The default hold timing and jitter tolerance may need tuning for your digitizer.

See the README's [verification and limitations](../README.md#verification-and-limitations) summary for the scope of automated checks and outstanding device validation.
