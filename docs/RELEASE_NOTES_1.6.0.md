# Moye 1.6.0 — Typing and Text Formatting

Write typed notes alongside handwriting with a visible **Type** button and a contextual text-formatting bar.

## Changes

- Start or resume a text box with **Type**. Add another box with **＋ Text box** or click an empty part of the page while Type is selected.
- Choose font family, size, bold, italic, color and alignment for the entire text box. Formatting is applied to the whole box, including when only part of its text is selected.
- Add plain bullet or numbered line prefixes, continue lists with Enter, and use native text undo. Use `Ctrl+B` / `Ctrl+I` for bold and italic, and `Ctrl+Enter` or `Esc` to finish typing and return to Pen after completing any active composition or control edit.
- Text boxes grow to the page boundary and scroll internally when full. Continue in a new box on the next page for longer notes; automatic text flow between pages is not available.
- Preserve text formatting through notebook saving/reopening, editable `.moye` backups and vector PDF export. Existing notebooks and backups remain compatible.
- Updated application icon. Existing pen presets, six paper templates, handwriting and PDF tools remain available.

## Download

Download `Moye-1.6.0-win-x64.zip`, extract the entire archive, and run `Moye.exe` on Windows 11 x64. The .NET runtime is included. The accompanying `.sha256` file verifies the archive.

## Validation and limitations

Release validation on 2026-09-14 passed **178 automated tests**, with no failures or skips, and **21 detached WPF scenes**, with no undersized main buttons, overlap or clipping. The build completed without warnings or errors. Coverage includes typing helpers, formatting, text persistence, backups, PDF export, the typing toolbar, empty and overflowing text boxes, and the existing notebook workflows. Release CI results are available in [GitHub Actions](https://github.com/yikeugene/moye/actions).

Earlier live desktop checks on 2026-09-14 covered basic Chinese IME candidate selection, English input, selected formatting controls, list continuation, native undo and returning to Pen. Those checks do not establish broader clipboard, long/interrupted composition or cross-page input acceptance. Physical pen, palm rejection, touch and tail-eraser validation remain outstanding.

Formatting applies to whole text boxes; per-word rich text is not available. Newly added text boxes export as vector outlines rather than searchable or selectable PDF text. Encrypted PDFs, interactive forms and digital signatures remain unsupported.
