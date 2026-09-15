# Moye 1.7.0 — Sections and SQLite Startup Repair

Download **Moye-1.7.0-Setup-win-x64.exe** and run it to install or update Moye on Windows 11 x64. Setup automatically creates desktop and Start menu shortcuts, includes the .NET runtime, and installs for your Windows account without administrator privileges.

## Before upgrading

**Back up your notebooks with your current version before opening Moye 1.7.0.** Use **More → Back Up All Notebooks** and keep that backup separately if you need to return to an older release.

Moye 1.7.0 upgrades older libraries to **SQLite schema 2** and writes **format 2 `.moye` backups**. Moye 1.6.2 and earlier cannot open the upgraded library or new backups. Existing version 1 backups remain readable in 1.7.0; restoring creates new notebook copies. Writing preferences remain format 1. See the [format guide](https://github.com/yikeugene/moye/blob/v1.7.0/docs/FILE_FORMAT.md) for details.

The installer keeps your library outside the program folder. Uninstalling removes the program and shortcuts while retaining your notebooks; it does not reverse a library migration.

## Notebook sections

- Organize notes as **Notebook → Section → Page**: one course per notebook, topics as sections, and lecture notes as pages.
- Create, rename, reorder and delete sections; move pages between sections. Section changes participate in notebook undo/redo, autosave and editable backups.
- Existing pages appear in **General** with their content and order preserved. Each section remembers its selected page during the editing session, and empty sections offer **Add Page**.
- Switching sections commits pending text and ink. Notebook PDF export includes all sections and their pages in order.

## Writing and startup reliability

- Prevent page focus from implicitly scrolling the whole writing page, and cancel stale touch/zoom navigation when pen contact begins. Explicit page navigation and text-caret scrolling remain available. These paths have automated regression coverage; physical pen and touch acceptance is still pending.
- Update SQLitePCLRaw from 2.1.13 to 3.0.5, with SQLite 3.53.4, after reproducing a Windows application-control block of the older initialization assembly. The repaired dependency passed storage checks on the affected machine without changing security settings; other device policies may differ.
- Show the underlying cause of operation errors and save the full exception to `error.log` beside the selected library when writable.
- Verify the actual packaged executable can save and reopen a synthetic notebook and attachment, and check database integrity. Installer tests repeat this after installation and reinstallation.

## Verification and limitations

The local Release run on 2026-09-15 passed **238 tests** with no failures or skips and **25 detached WPF layout scenes** with no undersized buttons, overlaps or clipping.

The release workflow runs the full application suite, detached WPF layout checks, packaged storage checks, and actual installation, reinstallation and uninstallation checks, including automatic shortcuts and retained synthetic notebook data. The release page links the results for the exact published commit.

The installer is not code-signed; its accompanying `.exe.sha256` file verifies the download. Physical pen feel, palm rejection, touch gestures, high-DPI behavior and broader IME/clipboard interactions remain subject to device validation. Infinite canvas, cloud sync, recording, handwriting recognition and AI features are not included.
