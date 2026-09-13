# Moye 1.3.1

Moye is an offline handwriting notebook for Windows 11 with pressure-sensitive ink, PDF annotation, text boxes, images, editable backups, and a notebook home screen with visual paper templates.

## Changes

- Clean source distribution with stronger exclusions for local notebooks, backups, credentials, development caches, generated packages, and internal QA records.
- Portable packages include only the application, required runtime dependencies, public user documentation, and third-party license notices.
- Retains the notebook home, Blank/Ruled/Grid paper templates, Fit Width, and all existing 1.3 notebook and backup formats.

## Download

Download `Moye-1.3.1-win-x64.zip`, extract the entire archive, and run `Moye.exe` on Windows 11 x64. The .NET runtime is included. Verify the archive with the accompanying `.sha256` file.

## Limitations

Physical pen feel, palm rejection, touch gestures, and real IME composition require device validation. Encrypted PDFs, interactive forms, and digital signatures are unsupported. New text boxes export as vector outlines; use `.moye` backups to retain editable content.
