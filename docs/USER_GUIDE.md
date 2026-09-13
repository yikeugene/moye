# Moye user guide

This guide describes the English interface in Moye 1.3 for Windows 11 x64.

## Notebooks and pages

Open `Moye.exe` to see **Your Notebooks**. Moye always starts on this home screen and waits for you to choose a notebook. An empty library shows a **Create a Notebook** button; it does not create a notebook automatically.

Click a notebook cover to open it, or **New Notebook** to choose a name, category, and first-page paper. The visual template cards offer **Blank**, **Ruled**, and **Grid**; new notebooks initially select Ruled. Canceling leaves the library unchanged.

Click **All Notes** to save your changes and return home. You can also switch notebooks using the editor's **Notebooks** sidebar tab. Opening a notebook shows its first page and the **Pages** thumbnails. Click the document title, or choose **More → Rename and Category**, to change its title and category. Search covers notebook titles and categories, not handwriting recognition or full-text search. The home screen also offers **Restore Backup** and **Library Options → Back Up All Notebooks**.

Select a page thumbnail, then use **Insert → Add Page**, or **Add Page** beneath the thumbnails. Choose a paper preview and click **Add Page** to insert after the current page. The picker starts with the current ordinary page's style, or Ruled when viewing a PDF. The **Page Options** menu above the thumbnails lets you duplicate, move, or delete the selected page. Page deletion can be undone. Deleting the last page leaves a new blank page.

Choose **Page Options → Paper Style**, select a visual template, and click **Apply Paper** to change an existing page. Your writing, text and images stay in place. This changes only the current ordinary page; PDF pages keep their original background.

## Writing and touch

Select **Pen** or **Highlighter**, then open **Pen Settings** to choose a color and width. The two tools retain separate colors when you switch between them. Pressure input requires a Windows Ink-compatible active pen. A mouse can also draw.

Finger input is reserved for navigation: one finger scrolls and two fingers zoom. Page gestures pause while the pen is down. Pen response and palm rejection depend on your laptop, pen, and drivers, and still need validation on the device you use.

The **Eraser** initially uses partial erasing. Choose partial or whole-stroke erasing in **Pen Settings**; the eraser button and `E` shortcut then reuse that mode.

Use **Lasso** to circle ink. Drag the selection to move it, or drag its boundary handles to resize it. Press `Ctrl+D` to duplicate or `Delete` to remove the selection. With ink selected, you can also change its color through **Pen Settings**.

## Viewing a page

Notebooks open in a fit-page view. The footer combines page navigation, save status, and zoom controls. **Fit Page** shows the whole sheet. **Fit Width**, directly beside it, expands the paper to the available writing area's width with a small margin on both sides; scroll vertically to see the rest of the page. While Fit Width is active, resizing the window or showing/hiding the sidebar refits the paper. **−**, **+**, pinch zoom, `Ctrl+mouse wheel`, or **Fit Page** leave width-fitting mode. Click the percentage for zoom options, including **Actual Size · 100%**.

The sidebar button or `F9` hides and shows the sidebar. The **Focus Mode** icon or `F11` switches to a full-screen workspace. These controls change the display, not the paper's dimensions.

## Text and images

Select **Text**, then click the page to create a text box, or click an existing text box to edit it. Text boxes support line breaks and Windows text input. While editing text, text-entry and text-editing shortcuts act on that box.

Choose **Insert → Insert Image** for PNG or JPEG files. When you are not editing text, `Ctrl+V` pastes an image or screenshot from the clipboard.

Choose **Select** and click a text box or image. Drag its upper-right move handle to reposition it, or its lower-right handle to resize it. Press `Ctrl+D` to duplicate the selected object or `Delete` to remove it.

Images are limited to 20,000 pixels per side and 80 million pixels in total. The original image bytes are retained; resizing its display does not rewrite the source.

## PDF annotation and export

Choose **Insert → Import PDF** to insert the document's pages after the selected page. The original PDF is stored in your local library. You can add ink, highlighting, text boxes, and images over it.

Click the share icon at the top, whose tooltip is **Share: Export PDF**, to export the whole notebook. Added ink and text become PDF page content rather than editable Moye objects. New text boxes are exported as vector outlines: their appearance is retained, but the exported text cannot be selected or searched. Original PDF text retains its existing capabilities. Keep a `.moye` backup if you need to edit the contents later.

Encrypted documents, interactive forms, digital signatures, and internal or cross-page annotation actions are not supported. If appropriate, flatten existing annotations in another PDF tool before importing. Ordinary URI links and supported page annotations can be retained. Moye displays an error for unsupported or damaged PDFs.

## Autosave and backups

Completed edits are queued for background saving, with a coalescing delay of at most two seconds. Completion time depends on the disk and document size. The saved status appears only after a successful database transaction. Switching notebooks, leaving the window, and closing normally also attempt to save.

The default library is `%LOCALAPPDATA%\Moye\moye.db`. SQLite may create `moye.db-wal` and `moye.db-shm` beside it. Do not move only the database or delete its journal files while the app is open. Version 1.3 uses the existing library and backup formats.

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
| Lasso / text / select objects | `L` / `T` / `V` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` |
| Duplicate selected ink or object | `Ctrl+D` |
| Select all ink on the current page | `Ctrl+A` |
| Delete the selection | `Delete` |
| Paste a screenshot | `Ctrl+V` |
| Save now | `Ctrl+S` |
| Show or hide the sidebar | `F9` |
| Focus mode | `F11` |
| Zoom | `Ctrl+mouse wheel` |

## Device checks still needed

Physical pen and touch validation, and real IME composition, remain outstanding. Automated checks do not establish these behaviors on a particular laptop:

1. Fast continuous writing, small characters, light and heavy pressure, highlighting, and both eraser modes.
2. Resting a palm before and after pen contact, without producing finger ink or moving the page.
3. One-finger scrolling, two-finger zoom, and pen alignment after scrolling and zooming.
4. Moving the pen beyond the page, lifting it, switching windows, and resuming from sleep without a stuck input state.
5. The specific pen's tail eraser and side buttons, which depend on driver events.
6. IME composition, candidate selection, line breaks, editing existing text, and retaining content across page changes. Direct Unicode entry is not the same as testing IME composition.

See the README's [verification and limitations](../README.md#verification-and-limitations) summary for the scope of automated checks and outstanding device validation.
