# Moye 1.12.0 — Context Menus and Section Actions

Download **Moye-1.12.0-Setup-win-x64.exe** to install or update Moye on Windows 11 x64. Setup includes the .NET runtime, installs for your Windows account and automatically creates desktop and Start menu shortcuts.

## Clearer context menus

Menus now use rounded white panels, subtle shadows, icons, green hover and focus states, and muted unavailable actions. Delete commands stand out in red. Action rows retain a 44 DIP minimum height, and menus support scrolling, submenus, checkmarks and keyboard shortcut labels.

Page menus identify the clicked page and keep their existing actions. **Delete Page** explains in its tooltip that Undo is available while the notebook stays open. Section headings retain literal names, including underscores, with full titles available on hover.

## Manage a section from its own menu

Right-click a section in **Contents**, or focus it and press **Shift+F10**, to rename it, move it up or down, or delete it. **Section Options (⋯)** opens the same menu for the selected section.

Opening a menu keeps the current page in view. Each action stays associated with the clicked section and notebook, and the target is checked again after pending edits and modal dialogs. Moving beyond the first or last position is disabled, as is deleting the only remaining section. Deletion still asks for confirmation and supports notebook Undo.

When a section row has keyboard focus, page ink editing shortcuts no longer act on the current page. Notebook Undo/Redo, Save and the existing global view commands remain available.

## Updating and verification

Close Moye and run the installer. Database schema 2, `.moye` backup format 2 and writing-preference format 1 are unchanged from 1.7.0–1.11.0. Selected-section PDF export and the 21 pen thickness levels from 1.11.0 are retained. Existing libraries remain in place, and uninstalling retains notebook data.

If upgrading from 1.6.2 or earlier, first export and retain a backup using that older version. Opening its library upgrades the schema; the older application cannot open the upgraded library or format 2 backups. Version 1 backups remain readable.

The release page records full test, detached WPF layout, packaged storage and install/reinstall/uninstall results for the published commit. The detached page-menu checks cover headings, icons, Undo guidance, mixed menu/separator containers and minimum action heights.

The installer remains unsigned. Native interaction with the redesigned menus and section actions has not been validated. Physical pen/touch, high DPI, broader IME behavior and representative LibreOffice conversion remain unverified; automated checks do not establish compatibility with every Windows application-control policy.
