# Moye 1.9.0 — Refreshed Workspace and Document Import

Download **Moye-1.9.0-Setup-win-x64.exe** to install or update Moye on Windows 11 x64. Setup includes the .NET runtime, installs for your Windows account and automatically creates desktop and Start menu shortcuts.

## Refreshed workspace

- Warm neutral surfaces, forest green controls, clearer notebook covers and visible keyboard focus.
- Notebook search has clear actions, **Ctrl+F**, and separate first-run and no-results guidance. Notebook options contain Open and Delete, with confirmation before deletion.
- **My notebooks**, **Manage pens**, labelled **Insert** and **Export** actions, and millimeter pen-width labels make editor commands easier to find.
- Updated notebook, rename and paper-selection dialogs, more readable secondary text, and layouts for both supported window sizes.
- Import progress keeps **Cancel Import** accessible while other editor input is disabled; focus returns to an available control when the operation finishes.

## Import Office documents locally

Choose **Insert → Import Document…** to import a PDF or one of these document formats:

| Format | Required installed application |
|---|---|
| DOCX | Microsoft Word or LibreOffice |
| PPTX, PPSX | Microsoft PowerPoint or LibreOffice |
| ODT, ODP | LibreOffice |

Moye converts a disposable copy locally and appends static PDF pages to the current section. The original document remains unchanged. Add editable Moye ink, highlights, text and images over the imported background; source Word text and slide objects are not directly editable. Only the converted PDF and Moye annotations enter the notebook and its backups. No converter is bundled or downloaded; PDF import needs neither Office nor LibreOffice.

Use **Cancel Import** or **Esc** to cancel. Conversion has a two-minute timeout. Password protection, macros and linked external resources are rejected. Animations, video and internal slide/page jumps are omitted; ordinary web links can remain. An existing PowerPoint session may need to be closed before conversion. Fonts and layout depend on the installed converter. If no converter is available, export a PDF in the source application and import it.

## PDF compatibility fixes

- Preserve the pressure-ink fill rule during PDF export, avoiding white holes where stroke contours overlap.
- Accept harmless empty form metadata and optional null entries while continuing to reject active forms, encryption, signatures and unsupported annotation actions.
- Validate pages before storing an imported asset, report the affected page when validation fails, and bound preview dimensions for extremely narrow or wide pages.

## Updating and verification

Close Moye and run the installer. Database schema 2, `.moye` backup format 2 and writing-preference format 1 are unchanged from 1.7.0–1.8.0. Existing libraries remain in place; uninstalling keeps notebook data.

If upgrading from 1.6.2 or earlier, first export and retain a backup using the older version. Opening that library upgrades its schema; the older application cannot open the upgraded library or format 2 backups. Version 1 backups remain readable.

The release page links the full application suite, detached layout checks, packaged storage checks and actual install/reinstall/uninstall checks for the published commit. Earlier synthetic DOCX/PPTX conversion checks on 2026-09-21 used installed Microsoft 365 Word/PowerPoint and verified source preservation, Chinese text, tables/images, hidden slides, save/reopen, editable backup and annotated PDF round trips.

Local Windows Application Control blocked loading the new application during the UI refresh verification. GitHub CI results do not establish compatibility with that local signing policy. The installer remains unsigned. Native interaction with the refreshed UI, actual LibreOffice conversion and ODT/ODP/PPSX conversion acceptance, physical pen/touch, high DPI and broader IME behavior remain unverified. On cancellation or timeout, an unresponsive hidden Microsoft Office process may remain; Moye does not force-close user Office sessions.
