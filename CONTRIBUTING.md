# Contributing to Moye

Moye is a native, offline Windows handwriting notebook. Contributions that improve writing, document editing, reliability, accessibility, or the clarity of the interface are welcome.

## Set up a development environment

Use Windows 11 x64 and a .NET 10 SDK. Clone the repository, then run these commands in PowerShell from its root:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

If a .NET 10 SDK is unavailable, `.\scripts\build.ps1 -InstallSdk` installs one into the repository's `.tools` directory from Microsoft's official HTTPS source. The first restore requires internet access. Build tools use repository-local caches and leave PowerShell security settings unchanged.

Start the app with a separate sample library when testing:

```powershell
.\src\Moye\bin\Debug\net10.0-windows10.0.26100.0\Moye.exe --data-dir .\sample-library
```

Keep sample libraries, generated packages, logs, build output, credentials, editor/assistant settings, and internal QA records out of commits. The repository's `.gitignore` excludes these local files, including `docs/TEST_REPORT.md`. Use synthetic notes and documents in examples and test fixtures. Review `git diff --cached --name-only` before committing; ignore rules do not remove files that are already tracked.

## Make a focused change

- Describe the problem and the behavior you intend to change in an issue or pull request. For larger changes, discuss the approach before restructuring existing features.
- Keep document models, persistence, PDF services, and input controls separate. Ink arrays are immutable snapshots: replace them rather than editing their bytes in place.
- Preserve existing notebooks and `.moye` backups. Changes to either format need an explicit versioning and compatibility approach.
- Keep visible interface text and user documentation in natural English. Do not translate or alter text that belongs to a user's notebook.
- Preserve third-party copyright notices and license files when updating dependencies.

## Verify the change

Run the tests relevant to your change, then the complete suite before submitting:

```powershell
.\scripts\test.ps1 -NoRestore
```

For layout changes, `.\scripts\preview-ui.ps1` renders the actual WPF interface with synthetic content. Also check the changed interaction in a real desktop window. Internal renders and accessibility-tree reads do not establish that a popup, touch gesture, or input method works interactively.

For pen or touch changes, report the laptop, active pen, driver, and the exact actions tested. Mouse drawing is not evidence of pressure response or palm rejection. Similarly, direct Unicode entry is not evidence of IME composition. Leave untested behavior explicitly unverified.

Use the [verification and limitations](README.md#verification-and-limitations) summary to understand existing coverage and outstanding checks. Generated TRX results and layout reports stay in `artifacts`; keep detailed internal QA notes local. Add regression tests where they can meaningfully catch the issue; avoid tests that merely repeat the implementation.

## Submit a pull request

Explain what changed, the user-visible result, and how it was checked. Include concise reproduction steps or before/after screenshots when useful. Remove personal information, machine-specific paths, and real notebook content from attachments and logs.

The release build is produced by `.\scripts\publish.ps1`. It creates a self-contained Windows x64 folder, a versioned ZIP, and a SHA-256 file in `artifacts`. The package includes an explicit list of user documentation and required third-party notices; keep this list current when adding public release documentation. Inspect the archive before uploading only the versioned ZIP and its checksum to GitHub Releases. Release publication is handled by the maintainers.

By submitting a contribution for inclusion, you agree that it can be distributed under the project's [MIT License](LICENSE). Third-party code remains subject to its own license.
