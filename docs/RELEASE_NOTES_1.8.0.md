# Moye 1.8.0 — Visual Color Tools and Touch Navigation

Download **Moye-1.8.0-Setup-win-x64.exe** and run it to install or update Moye on Windows 11 x64. Setup includes the .NET runtime and automatically creates desktop and Start menu shortcuts for your Windows account, without requiring administrator privileges.

## Visual colors and stroke thickness

- Choose ink, preset and text colors from a shared visual picker with 16 palette swatches, a draggable saturation/brightness field, a hue slider, and current/new color comparisons. **Apply** saves the choice; **Cancel** preserves the original color, including its transparency.
- Use 10 quick color swatches in **Pen Settings**, or open **More Colors…** for a custom color. Keyboard adjustment is also supported.
- Drag the **Thickness** slider in Pen Settings or the preset editor and see a live stroke sample reflecting color, pen/highlighter, opacity, pressure and smoothing settings.
- A thickness drag on lasso-selected ink produces one undoable change when released. Stored stroke widths remain independent of document zoom.

## Touch navigation and background work

- Combine incoming finger movement once per display update while retaining the full movement, including final release coordinates. Two-finger pan/zoom keeps its center as the fingers move.
- Continue a quick one-finger swipe with elapsed-time inertia. New contact, pen input, navigation and other interruptions stop the motion; holding still before release does not fling.
- Defer thumbnail and higher-resolution PDF refresh work while the viewport is moving, reuse clean cached thumbnails, and reject stale background results. These changes reduce work during navigation; no hardware frame-rate improvement is claimed without device measurements.

## Updating and your notes

Close Moye and run the installer. Notebook sections and the SQLite startup repair from 1.7.0 are included. **Database schema 2, `.moye` backup format 2 and writing-preference format 1 are unchanged from 1.7.0.** Existing notebooks and saved tools remain in their current library directory.

If upgrading from **1.6.2 or earlier**, export a backup with that version first and keep it separately. Opening an older library upgrades it to schema 2; those older releases cannot open the upgraded library or format 2 backups. Version 1 backups remain readable. See the [format guide](https://github.com/yikeugene/moye/blob/v1.8.0/docs/FILE_FORMAT.md).

Uninstalling removes the program and shortcuts while keeping notebook data; it does not reverse a data-format migration.

## Verification and limitations

The local Release run on 2026-09-21 passed **292 tests**, with no failures or skips, and **33 detached WPF layout scenes** with no undersized visible controls, overlaps or clipping. Detached thumbnail scheduling checks also passed.

The release workflow runs the complete application suite, detached WPF layout checks, packaged executable storage checks, and actual installation/reinstallation/uninstallation checks, including automatic shortcuts and retained synthetic notebook data. The release page links the results for the exact published commit.

Earlier mouse checks on 2026-09-21 covered color Apply/Cancel, thickness preview, saved preset/text/ink colors after reopen, and a scroll/draw/thumbnail regression using only a synthetic library. Automated touch tests cover movement accumulation, contact transitions, inertia and cancellation. Physical finger gestures, stylus handoff, palm rejection, high-refresh displays and large-PDF device performance still require target-hardware validation.

The installer is not code-signed; use the accompanying `.exe.sha256` file to verify the download.
