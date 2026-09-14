# Moye 1.6.1 — Notebook Deletion and Icon Update

Manage your notebook library with confirmed deletion from the home screen or editor, and an updated application icon with a transparent background.

## Changes

- Delete a notebook from **Delete Notebook…** beneath its home cover, or open it and choose **More → Delete Notebook…**.
- The confirmation names the notebook and defaults to **No**. Choosing **Yes** removes that notebook and all its pages. Deletion cannot be undone; back up first to keep a copy. A recycle bin is not available.
- Deleting the open notebook returns home and clears its undo/redo history. Pending saves finish before deletion, so an in-flight save cannot recreate the deleted notebook.
- If saving or deletion fails, the notebook remains available for retry. Deleting another notebook preserves the current editor and updates the filtered library.
- Updated the PNG and Windows application icon with a transparent background.

Existing notebook databases, editable `.moye` backups, typing and formatting, pen presets, paper templates and PDF tools remain compatible.

## Download

Extract the entire `Moye-1.6.1-win-x64.zip` and run `Moye.exe` on Windows 11 x64. The .NET runtime is included. Use the accompanying `.sha256` file to verify the archive.

## Validation and limitations

The notebook-deletion update was checked on 2026-09-14 with **184 automated tests** and **21 detached WPF scenes**. Coverage includes failed-save/delete recovery, pending-save ordering, history cleanup, preserving other notebooks, and the home-screen delete button.

Earlier live desktop checks with an isolated synthetic library covered canceling deletion, confirming deletion from the editor and home card, and returning to an empty library. These recorded checks do not establish broader device acceptance. Physical pen, palm rejection, touch and tail-eraser checks remain outstanding, as do broader clipboard, long/interrupted IME composition and cross-page input checks.

Text formatting applies to whole boxes. Newly added text boxes export as vector outlines rather than searchable or selectable PDF text. Encrypted PDFs, interactive forms and digital signatures remain unsupported.
