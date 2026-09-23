# Moye 1.11.0 — Section Export, Stepped Pen Sizes and Interface Polish

Download **Moye-1.11.0-Setup-win-x64.exe** to install or update Moye on Windows 11 x64. Setup includes the .NET runtime, installs for your Windows account and automatically creates desktop and Start menu shortcuts.

## Export the selected section

**Export now saves only the selected section**, in its displayed page order. Select a section in **Contents**, then choose **Export**. The suggested filename is **Notebook - Section.pdf**, and an empty section displays a message before the save dialog opens.

Pending edits are committed before the section is captured. That snapshot stays independent of later navigation or edits, and assets in other sections are excluded. Original PDF backgrounds, vector ink, images and outlined added text retain the existing export behavior. Editable `.moye` notebook backups continue to contain every section. Earlier releases exported the whole notebook from this button.

## Choose from 21 pen sizes

Pen Settings, the floating focus tools and Manage pens share 21 fixed thickness levels, visible ticks and a live stroke preview. Sizes range from approximately 0.13 to 6.35 mm, with fine increments for writing, including 0.35 and 0.45 mm, and broader sizes such as 3 mm for highlighting.

Arrow keys choose the adjacent size; Home and End choose the smallest and largest. Existing custom widths retain their exact value until you adjust the slider. A completed drag applies a lasso width change once, preserving a single undo step.

## Refined buttons and controls

- Consistent button typography, rounded corners and separate keyboard focus rings.
- Hover and pressed states retain the selected tool's background.
- Better spacing for icon buttons, sidebar tabs and section selection.
- Favorite pens show outlined color dots, shortcut badges and a clearer active border.
- Notebook cards, dialog actions, color-picker focus indicators and wrapped tooltips follow the existing green and warm-neutral theme. Ink colors are unchanged.

## Updating and verification

Close Moye and run the installer. Database schema 2, `.moye` backup format 2 and writing-preference format 1 are unchanged from 1.7.0–1.10.0. Existing libraries remain in place, and uninstalling retains notebook data.

If upgrading from 1.6.2 or earlier, first export and retain a backup using that older version. Opening its library upgrades the schema; the older application cannot open the upgraded library or format 2 backups. Version 1 backups remain readable.

Regression coverage checks section selection, page ordering, snapshot isolation, exclusion of other sections' assets, mixed PDF/native-page export, fixed pen levels, keyboard direction and preservation of custom widths. The release page records full test, detached WPF layout, packaged storage and install/reinstall/uninstall results for the published commit.

The installer remains unsigned. Live interaction with the latest button styling, section export dialogs and stepped slider has not been fully validated. Physical pen/touch, high DPI, broader IME behavior and representative LibreOffice conversion remain unverified; automated checks do not establish compatibility with every Windows application-control policy.
