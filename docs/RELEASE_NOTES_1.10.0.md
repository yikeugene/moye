# Moye 1.10.0 — Focus Tools, Page Menus and PDF Import Fixes

Download **Moye-1.10.0-Setup-win-x64.exe** to install or update Moye on Windows 11 x64. Setup includes the .NET runtime, installs for your Windows account and automatically creates desktop and Start menu shortcuts.

## Write in focus mode

Press **F11** to open the full-screen workspace with a compact floating toolbar on the left. Pen, Highlighter, Eraser, Lasso, Pen Settings, Undo, Redo and Exit Focus remain available. Tool settings and the remembered eraser mode are shared with the main toolbar, and keyboard tool shortcuts continue to work after clicking a floating tool. A reserved margin keeps the paper clear of the controls; save errors remain accessible.

## Manage the page you click

Right-click a page thumbnail or the paper to duplicate, reorder, move it to another section, change its paper style or delete it. This replaces the Page Options button. The menu identifies its target page, and right-clicking a thumbnail does not first move the writing viewport. Actions retain the clicked page even if the selected page changes; stale targets are rejected. Page changes keep the existing undo and autosave behavior. Text boxes retain their native editing menu, and **Shift+F10** opens a focused thumbnail's page menu.

## Import PDFs with navigation annotations

Supported PDF annotations containing internal destinations or interactive actions no longer block import. The library and editable backups retain the original PDF bytes. Export preserves page content and annotation appearances while removing internal destinations and interactive actions, so reordering or duplicating pages does not leave broken jumps. Ordinary URI links remain, with chained actions removed. Office-generated PDFs use the same normalization. Error dialogs avoid repeating an underlying explanation already included in a page-specific error.

Encrypted PDFs, active forms, signatures and unsupported interactive annotation types remain unsupported. Newly added text boxes still export as vector outlines; keep a `.moye` backup for editable content.

## Updating and verification

Close Moye and run the installer. Database schema 2, `.moye` backup format 2 and writing-preference format 1 are unchanged from 1.7.0–1.9.0. Existing libraries remain in place, and uninstalling retains notebook data.

If upgrading from 1.6.2 or earlier, first export and retain a backup using that older version. Opening its library upgrades the schema; the older application cannot open the upgraded library or format 2 backups. Version 1 backups remain readable.

On 2026-09-23, the source passed 437 automated tests and 39 detached WPF layout scenes. Native desktop checks with synthetic notebooks covered focus tools, popups, keyboard shortcuts, thumbnail and paper menus, duplicate/delete/undo, text editing menus, and save/reopen. A six-page PDF fixture verified navigation annotation import, unchanged original bytes, retained appearance and export without internal actions. The release page records CI and installer verification for the published commit.

The installer remains unsigned. Physical pen/touch, high DPI, broader IME behavior and representative LibreOffice conversion remain unverified; automated checks do not establish compatibility with every Windows application-control policy. The PDF checks used synthetic fixtures, not the original user-reported document.
