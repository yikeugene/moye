# Moye 1.4.0 — Draw and Hold & Eraser Modes

## Draw a line, hold, and straighten

Use Pen or Highlighter to draw a line, then keep the tip near its endpoint for about 0.65 seconds. The line straightens while held; drag to adjust its length and angle, then lift to finish. A mouse supports the same left-button gesture.

Short marks, closed loops and strongly curved writing remain freehand. Pen Settings now includes **Draw and Hold**, enabled by default. Turning it off disables recognition for both drawing tools in the current session.

The final result is one editable ink stroke with its pressure, color and highlighter attributes preserved. Saving, undo, backups and PDF export use the normal ink path. Interrupted input clears the preview and lets native InkCanvas finish the stroke it collected.

## Choose what your eraser removes

Click the eraser icon for a dedicated picker:

- **Pixel Eraser:** remove the touched portion and keep the remaining fragments.
- **Stroke Eraser:** remove the entire stroke when any part is touched.

The toolbar shows the current mode; `E` recalls it. Erasing remains undoable. A tool-switch fix also prevents clearing a lasso selection from leaving the canvas in selection mode after choosing an eraser.

## Download and validation

Extract all files from `Moye-1.4.0-win-x64.zip` and run `Moye.exe` on Windows 11 x64. The .NET runtime is included. Existing notebooks and `.moye` backups remain compatible.

Local validation: **89 automated tests passed**, plus **14 detached WPF layout scenes**. These cover gesture timing, jitter, zoom thresholds, rejected shapes, pressure/highlighter preservation, one-stroke save behavior, capture-loss cleanup, eraser modes and undo. Tests and renders do not replace active-pen hardware testing: live hold feel, palm/touch behavior and renderer transitions still need validation on your device.
