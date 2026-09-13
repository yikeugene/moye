# Moye 1.3.0 — Notebook Home & Paper Templates

Moye now starts at **Your Notebooks**, where you can open a notebook cover or create a new one. Empty libraries stay on the home screen until you choose to create a notebook.

## Changes

- A notebook home screen with covers, title/category search, an empty state, backup restore and library backup.
- **All Notes** saves pending changes before returning home. A save failure keeps the editor and unsaved content available for retry or backup.
- Visual **Blank**, **Ruled**, and **Grid** paper cards when creating a notebook, adding a page, or changing its background. New notebooks default to Ruled; existing content and PDF backgrounds are preserved.
- A **Fit Width** button beside **Fit Page**. Paper expands to the writing area's width and refits when the window or sidebar changes. Manual zoom leaves width-fitting mode.
- English interface with existing Unicode input, notebook databases, and editable backup compatibility.

## Download

Download `Moye-1.3.0-win-x64.zip`, extract the entire archive, and run `Moye.exe` on Windows 11 x64. The .NET runtime is included. The `.sha256` file can be used to verify the archive.

## Verification

The Release build and 59 automated tests passed locally. Tests include empty and populated library startup, chosen paper persistence, returning home after saving, failed-save recovery, and reopening a notebook. Detached WPF renders check the library, editor, and template layouts at supported window sizes. See the README's verification and limitations summary for the scope of these checks.

Physical pen, palm rejection, touch gestures, and real IME composition still require device testing. PDF limitations from earlier versions remain unchanged.
