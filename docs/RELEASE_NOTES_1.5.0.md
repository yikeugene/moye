# Moye 1.5.0 — Presets and Faster Editing

This release delivers the first implementation slice of the [university note-taking roadmap](../ROADMAP.md). The roadmap covers 41 feature areas; it includes future work and is not a list of fully delivered features. Infinite canvas remains planned. Moye continues to use fixed notebook pages.

## Keep your everyday pens ready

- Start with Black and Blue Pens at **0.45 mm**, Red Pen at **0.35 mm**, and Yellow Highlighter at **3 mm**.
- Use **Pen Settings → Save Current as Preset…** to save a new named tool. **Presets…** supports renaming, duplication, deletion, moving up/down, and hiding favorites without deleting them.
- Keep up to 40 presets. The first nine favorites appear in the toolbar; select them with `1`–`9` and drag to reorder. Use **Presets → Save and Use** to activate any preset, including hidden ones. Switching Pen/Highlighter recalls the most recently used pen in the session.
- Set thickness from approximately **0.132292 to 6.35 mm**, pen opacity from **10% to 100%**, pressure sensitivity and stroke smoothing. Highlighter opacity remains at its native **50%**.
- Preserve presets, favorite order, the last chosen preset, eraser settings and the Draw and Hold toggle between launches.

Writing settings live in `writing-preferences.json` alongside the local library. They are separate from notebook data and are not included in `.moye` backups. Saves use an atomic file replacement; damaged settings are preserved and recovery is reported. Unreadable or unsupported settings files are protected from being overwritten; tools remain usable for that session and notebook saving/closing remains available.

## Six paper templates

The visual paper picker now offers **Blank**, **Ruled**, **Grid**, **Dot Grid**, **Cornell** and **Graph**. Dot Grid adds evenly spaced dots. Cornell provides a cue column, ruled notes and a summary area. Graph adds finer squares with stronger major guides.

Template geometry is consistent across the editor, thumbnails and vector PDF export. New paper pages remain A4 with fixed spacing; custom paper sizes and spacing are future work. Imported PDF pages keep their original backgrounds.

## More precise erasing and quicker editing

- Adjust eraser size independently from **12 to 120 DIP**, starting at 20 DIP, and enable **Erase highlighter only** to protect ordinary pen strokes.
- Pixel and Stroke modes remain available. The pen's tail eraser follows the selected mode when the device reports inverted-pen input.
- Copy or cut lasso-selected ink with `Ctrl+C` / `Ctrl+X`, then paste an editable copy onto another page with `Ctrl+V`. Pressure and stroke attributes are retained. This clipboard format covers ink; text boxes keep native text clipboard and IME behavior, and image pasting remains the fallback when the clipboard contains no Moye ink.
- Use `Ctrl+Shift+Z` for redo, or hold **Space** and drag with the left mouse button for temporary panning.
- `F11` hides the notebook header, toolbar, sidebar and footer. **Exit Focus** and save errors remain available.

## Reliability and compatibility

Autosave deadlines and temporary touch suppression now use monotonic elapsed time, so system-clock changes cannot extend those waiting periods. A canonical library identity makes equivalent default and explicit data-directory paths share single-instance protection.

Fixed a native InkCanvas mode change during background thumbnail refresh: refreshing a page preview now keeps the selected writing or eraser mode active. Regression tests cover this transition.

Existing notebook databases, ISF ink and `.moye` backups remain compatible. Extract the entire `Moye-1.5.0-win-x64.zip` and run `Moye.exe` on Windows 11 x64; the .NET runtime is included.

## Validation

Release build completed without warnings or errors. All **141 automated tests** passed, including settings recovery, monotonic autosave, canonical library identity, editable ink/eraser workflows and new paper-template PDF round trips. All **17 detached UI scenes** passed the 44-DIP button, overlap and clipping checks. The preview runner also verifies remembered pen switching, safe read-only preferences and exact preset-manager values.

Physical active-pen, palm/touch, tail-eraser and live IME validation remains device-dependent and outstanding. Automated tests and detached layout renders do not establish real-device pen feel, latency or palm rejection.
