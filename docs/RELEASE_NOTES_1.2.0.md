# Moye 1.2.0 — English Open Source Release

Moye is an offline Windows notebook app for handwriting, PDF annotation, text and images. This is its first public open source release, under the MIT license.

## Download

Download **Moye-1.2.0-win-x64.zip**, extract the entire archive, and open **Moye.exe**. Requires Windows 11 x64. The package includes the .NET runtime; no separate .NET installation is needed.

The accompanying `.sha256` file can be used to verify the download. GitHub's automatically generated source archives contain source code, not the ready-to-run application.

## What's included

- English interface, tooltips, dialogs, save status, error messages and documentation.
- Notebook and page navigation, a compact writing toolbar, and full-page viewing.
- Pressure-sensitive pen, highlighter, partial and whole-stroke erasers, lasso, undo and redo.
- Text boxes with Unicode support, images and clipboard screenshots.
- Local SQLite autosave, editable `.moye` backups, and PDF import and export.
- Source code, build scripts, contribution guide, MIT license and a Windows CI workflow.

Existing notebooks and backups remain compatible. Existing titles and note content are preserved in their original language. Chinese text and input support remain available.

## Validation and current limits

All 48 automated tests passed locally for this release, covering persistence, backup restore, ink data, editing history and PDF processing. See the README's verification and limitations summary for the scope of checks, and GitHub Actions for current CI results.

Real active-pen feel, palm rejection, touch gestures and IME composition still need validation on target hardware. PDF export flattens new annotations; added text is rendered as vector outlines and is not searchable. Encrypted PDFs, interactive forms, digital signatures and internal cross-page PDF links are not supported. Use `.moye` backups to retain editable content.

Application source is MIT licensed. Bundled dependencies retain their own licenses; the Windows package includes third-party notices.
