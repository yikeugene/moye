# Moye 1.6.2 — Windows Installer

Download **Moye-1.6.2-Setup-win-x64.exe** and run it to install Moye. Setup automatically adds **Moye** shortcuts to your desktop and Start menu. You no longer need to extract a ZIP.

## Installation and updates

- Installs for the current Windows account, without requesting administrator privileges.
- Includes the .NET runtime and required dependencies for Windows 11 x64.
- Uses `%LOCALAPPDATA%\Programs\Moye` for program files by default. Existing notebooks remain in `%LOCALAPPDATA%\Moye`.
- Run a newer installer to update the same installation. Uninstall through Windows Settings to remove the app and shortcuts while keeping your notes and writing preferences.
- Existing portable users with the default library can open the same notebooks from the installed app. Custom `--data-dir` libraries still require that launch argument.

This is a packaging update based on Moye 1.6.1. Notebook deletion, typing, handwriting, templates, PDF tools, database and editable backup formats are unchanged.

## Verification

The build includes an installer smoke test for automatic desktop and Start menu shortcuts, exact installed file contents, reinstallation, Windows uninstall registration and preservation of synthetic notebook data after uninstall. Application tests and detached WPF layout checks run in GitHub Actions alongside it.

The accompanying `.exe.sha256` file verifies the downloaded installer. This release is not code-signed. Physical pen, palm rejection, touch and broader IME/clipboard device validation remain outside this packaging update.
