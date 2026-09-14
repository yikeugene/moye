# University Note-Taking Roadmap

**Write fast. Organize easily. Review quickly.**

Moye should support a complete university workflow: write during a lecture, annotate course material, organize knowledge, and revisit it without losing time or notes. This roadmap preserves all 41 requested feature areas. It is a development plan, not a claim that every listed feature is available.

This status reflects **Moye 1.6.0**, reviewed on **2026-09-14**. Checked items describe implemented behavior, including the first writing-workflow milestone below; unchecked items remain work to complete. Implementation and automated coverage do not establish successful physical pen or desktop interaction.

Version 1.6.0 adds an explicit Type action, contextual text controls and whole-box formatting. Basic live desktop typing and Chinese IME candidate selection have been checked. Its scope and remaining acceptance work are recorded in area 24; publication does not mark those outstanding checks complete.

## Status and evidence

- **Existing:** the described behavior is implemented. This does not establish performance on every device.
- **Partial:** an area has usable foundations and remaining features.
- **Planned:** the requested area has no complete user-facing implementation yet.
- **Hardware validation:** code exists, but physical pen, palm, touch, display, or IME acceptance remains outstanding. These checks stay unchecked.

Code and tests provide the following evidence:

| Evidence | Source |
|---|---|
| Native pen/touch separation, capture cleanup and live ink | [PenInkCanvas](src/Moye/Controls/PenInkCanvas.cs), [PageEditor](src/Moye/Controls/PageEditor.cs), [ink tests](tests/Moye.Tests/InkTests.cs) |
| Draw-and-hold recognition and native commit lifecycle | [HoldToStraightenSession](src/Moye/Controls/HoldToStraightenSession.cs), [gesture tests](tests/Moye.Tests/HoldToStraightenTests.cs), [lifecycle tests](tests/Moye.Tests/StraightInkLifecycleTests.cs) |
| Library, pages, history, navigation and shortcuts | [MainViewModel](src/Moye/ViewModels/MainViewModel.cs), [NotebookHistory](src/Moye/ViewModels/NotebookHistory.cs), [MainWindow](src/Moye/MainWindow.xaml.cs), [library tests](tests/Moye.Tests/LibraryTests.cs), [history tests](tests/Moye.Tests/HistoryTests.cs) |
| Paper, text, images and persistent document fields | [DocumentModels](src/Moye/Models/DocumentModels.cs), [PaperVisual](src/Moye/Controls/PaperVisual.cs), [PaperTemplatePicker](src/Moye/Controls/PaperTemplatePicker.cs), [NoteItemFrame](src/Moye/Controls/NoteItemFrame.cs) |
| Typing UI, whole-box typography and plain line prefixes | [typing UI](src/Moye/MainWindow.Typing.cs), [PageEditor](src/Moye/Controls/PageEditor.cs), [text editing helpers](src/Moye/Controls/TextEditing.cs), [typing tests](tests/Moye.Tests/TypingTests.cs), [text persistence tests](tests/Moye.Tests/TextPersistenceTests.cs) |
| Autosave, transaction integrity and recovery after write failure | [AutosaveCoordinator](src/Moye/Services/AutosaveCoordinator.cs), [SQLite repository](src/Moye/Services/SqliteNotebookRepository.cs), [storage tests](tests/Moye.Tests/StorageTests.cs) |
| Persistent writing presets and settings recovery | [WritingPreferences](src/Moye/Models/WritingPreferences.cs), [WritingPreferencesStore](src/Moye/Services/WritingPreferencesStore.cs), [preset manager](src/Moye/Controls/PresetManagerDialog.cs), [preference tests](tests/Moye.Tests/WritingPreferencesTests.cs) |
| Preset application, editable ink clipboard, selection width, eraser filtering and thumbnail tool preservation | [writing UI](src/Moye/MainWindow.Writing.cs), [writing workflow tests](tests/Moye.Tests/WritingWorkflowTests.cs) |
| Monotonic autosave scheduling and canonical library identity | [autosave timing tests](tests/Moye.Tests/AutosaveTimingTests.cs), [library location tests](tests/Moye.Tests/LibraryLocationTests.cs), [application startup](src/Moye/App.xaml.cs) |
| PDF rendering/export and editable backup packages | [PdfService](src/Moye/Services/PdfService.cs), [BackupService](src/Moye/Services/BackupService.cs), [PDF tests](tests/Moye.Tests/PdfTests.cs), [PDF performance fixtures](tests/Moye.Tests/PdfPerformanceTests.cs), [backup tests](tests/Moye.Tests/BackupTests.cs) |

The linked public test sources describe reproducible automated coverage. Tests and detached layout renders do not prove current hardware latency, palm rejection, high-refresh rendering, sleep recovery, or live IME composition. Benchmark numbers from small synthetic fixtures must not be presented as guarantees for scanned textbooks or long lectures.

## First writing-workflow milestone

- [x] Persistent named pen/highlighter presets with rename, duplicate, delete, reorder and favorite visibility; favorite preset buttons support drag reordering.
- [x] Per-preset pen opacity, pressure sensitivity and smoothing, plus exact preset widths. Highlighters retain native 50% compositing.
- [x] Independent 12–120 DIP eraser size and highlighter-only erasing for pixel/stroke modes.
- [x] Editable ink copy/cut/paste across pages, preserving pressure and custom metadata; oversized pasted ink fits the destination page. Selected ink width can be changed.
- [x] Ctrl+Shift+Z redo, Space plus mouse drag to pan, and 1–9/numpad preset access, with existing text editing shortcuts protected.
- [x] Focus mode hides notebook and writing chrome, provides an exit control, and keeps save failures accessible.
- [x] Monotonic autosave deadlines and temporary touch suppression; canonical default/explicit library paths share the single-instance identity.
- [x] Thumbnail generation preserves Pen, Highlighter, Pixel Eraser, Stroke Eraser and Lasso modes without changing saved ink or losing lasso selection.

The in-memory writing workflow suite passed **24 cases** on 2026-09-14. It uses no desktop windows or system clipboard actions; eraser filtering invokes the compiled cancellable event hook. This is method-level regression evidence. Real clipboard interaction, drag reordering, shortcut focus transitions and physical input still need desktop/device acceptance. The wider roadmap remains open.

## Prioritized milestones

### P0 — Dependable lecture writing

The core is ink, palm handling, pen/highlighter, erasing, lasso, undo, zoom, autosave, and page management. Reliability work has priority over adding more tools.

- [ ] Validate fast small handwriting, pressure transitions, pen lift/out-of-range, palm before/after pen, one-finger pan, pinch zoom, eraser input, and alignment after zoom on real Windows Ink hardware.
- [ ] Measure input callback time, frame pacing, memory and save delay with long handwritten notebooks and representative 100-page scanned PDFs at normal and high DPI. Record hardware and document size with each result.
- [ ] Reduce unnecessary work on the UI input path. The baseline copies every page's ISF in `AutosaveCoordinator.Schedule`; the repository snapshots again and hashes every page. Profile this cost before moving to immutable changed-page snapshots.
- [x] Use monotonic elapsed time for autosave deadlines and temporary touch suppression, so clock adjustment cannot extend a hold-off or the continuous-edit save window.
- [x] Resolve one canonical library identity for single-instance protection, including equivalent explicit/default data directories and path casing; cover the mapping with automated tests.
- [ ] Exercise normal close, notebook switching, return home, forced termination after a committed save, disk-full/write denial, save retry and recovery backup. Clearly distinguish committed data from memory-only pending edits.
- [ ] Keep one undoable edit per completed ink/erase/selection operation, and prevent hidden editors or popup shortcuts from mutating a notebook unintentionally.

P0 acceptance requires recorded device checks and failure-path evidence. Existing automated tests remain regression gates; a passing build alone does not close these items.

### P1 — Faster course work

- [x] Provide persistent named pen presets and a quick-access favorite-preset toolbar.
- [ ] Extend customization to the fixed tool buttons and separate favorite/recent color management.
- [ ] Extend the existing draw-and-hold line feature into explicit shape tools with predictable undo and pressure behavior.
- [ ] Add richer paper templates and template management without changing existing notebooks unexpectedly.
- [ ] Improve PDF page selection, image/clipboard workflows and export choices.
- [ ] Add bookmarks for quick review and split view for reading material beside handwritten notes.

Each feature must retain editing, save/reopen, backup and PDF behavior where applicable. Keep primary actions usable with a pen and at least 44 DIP touch targets. Introduce these changes in small releases rather than marking the whole milestone complete at once.

### P2 — Organize and revisit knowledge

- [ ] Add tags, document content search, page titles, internal links and navigation history.
- [ ] Add durable version history with safe restore and comparison.
- [ ] Add infinite canvas as an optional document mode with a minimap and a defined export strategy.
- [ ] Add selection export, customizable shortcuts and advanced arrangement tools.

P2 changes that introduce new persistent fields or document modes need a documented format version, compatibility behavior and backup round trips. Infinite canvas is now a future product direction; it is not available in the current fixed-page editor.

## Feature areas

### 1. Writing Experience

**Status: Partial · Hardware validation. Priority: P0.**

- [x] Native WPF pressure-aware ink, vector stroke storage, pen-priority handling, finger pan/pinch routing, and page-coordinate zoom.
- [ ] Measure and tune low latency, high-refresh frame pacing, smoothing, prediction and crisp rendering across zoom levels.
- [ ] Validate palm rejection and pen priority on real devices; add handedness and sensitivity preferences where needed.

### 2. Pen Tools

**Status: Partial. Priority: P0 for the basic pen/highlighter; P1 for presets and toolbar.**

- [x] Pen and highlighter, color and width controls, native pressure for pen, and separate remembered pen/highlighter colors during a session.
- [x] Per-preset pen opacity, pressure-sensitivity and smoothing toggles; highlighter presets use their exact stored width and native half-opacity.
- [x] Persistent named presets with rename, reorder, duplicate and delete; favorite preset quick access, drag reordering and per-preset toolbar visibility.
- [ ] Add distinct ballpoint, fountain pen, pencil and marker behavior, configurable pressure-response curves and richer smoothing controls.
- [ ] Add drag/reorder/hide customization for the fixed tool buttons, recent colors and favorite colors.

### 3. Eraser

**Status: Partial · Hardware validation. Priority: P0.**

- [x] Pixel/partial and whole-stroke erasing; toolbar mode selection and remembered eraser mode.
- [x] Native inverted-pen erasing is configured in the ink control.
- [x] Highlighter-only erasing and independent eraser size controls, persisted with writing preferences.
- [ ] Validate tail erasers and side buttons; support configurable pen-button behavior where the device exposes it.

### 4. Undo and Redo

**Status: Partial. Priority: P0.**

- [x] Up to 100 history steps in the open notebook session, with toolbar and keyboard undo/redo, including Ctrl+Y and Ctrl+Shift+Z redo.
- [ ] Add deliberate two-finger-tap undo and three-finger-tap redo, with gesture recognition that does not conflict with pan/zoom or palm contact.
- [ ] Validate coherent history for every new object, clipboard and shape operation. Session undo is separate from durable version history in area 32.

### 5. Lasso

**Status: Partial. Priority: P0 for existing selection; P1 for extensions.**

- [x] Select ink, move, resize, duplicate, delete and recolor it.
- [x] Selected-stroke width changes and editable ink copy/cut/paste across pages, with pressure/metadata retained and a single undoable paste.
- [ ] Add rotation, grouping and ungrouping.
- [ ] Extend cross-page copy/move to mixed ink, text, image and shape selections, with consistent coordinates, asset ownership and undo.

### 6. Natural Gestures

**Status: Partial · Hardware validation. Priority: P1.**

- [x] Draw and hold a line for about 650 ms, adjust its endpoint, then lift to commit one straight stroke; the behavior can be disabled.
- [ ] Validate recognition timing, false positives and live preview transitions with an active pen.
- [ ] Add scribble-to-delete and circle-to-select with clear cancellation and undo behavior.

### 7. Shapes

**Status: Partial. Priority: P1.**

- [x] Straight lines through draw-and-hold.
- [ ] Add explicit line, arrow, rectangle, square, circle, ellipse, triangle and polygon tools.
- [ ] Extend draw-and-hold recognition to appropriate shapes without changing ordinary handwriting or intentionally rough sketches.

### 8. Ruler and Measurement Tools

**Status: Planned. Priority: P1.**

- [ ] Add a straightedge, ruler and protractor.
- [ ] Support edge snapping, visible angles, and common-angle constraints while keeping tools easy to move away from writing.

### 9. Zoom and Pan

**Status: Partial · Hardware validation. Priority: P0.**

- [x] Finger pan, pinch zoom, Ctrl+wheel, zoom buttons, Fit Page, Fit Width and 100% view.
- [x] Fit Width responds to the available writing area; page-coordinate anchors account for fixed page gaps during zoom.
- [ ] Add double-tap reset and optional smooth transitions that do not delay pen input.
- [ ] Validate touch interaction, edge cases during capture loss, and alignment after resize/zoom on hardware.

### 10. Focus

**Status: Partial. Priority: P0 for usable writing space; P1 for refinements.**

- [x] Sidebar toggle and fullscreen focus mode with the system window frame, writing toolbar and notebook title hidden; an exit control restores editing chrome and save failures remain accessible.
- [ ] Add independent visibility preferences for the writing toolbar, notebook title and other chrome outside the combined focus mode.
- [ ] Add temporary/hover tool access suitable for both pen and keyboard use.

### 11. Keyboard

**Status: Partial. Priority: P2; useful low-risk improvements may ship earlier.**

- [x] Basic tool shortcuts, undo/redo, duplicate, delete, save, sidebar and focus shortcuts; text-box shortcuts are protected.
- [x] Space plus mouse drag pans temporarily; 1–9/numpad keys select favorite presets; Ctrl+C/X/V transfers editable ink between pages.
- [ ] Complete standard copy/cut/paste behavior for mixed note-object selections and validate focus/clipboard transitions in desktop use.
- [ ] Add a visible shortcut reference and user-customizable mappings, with conflict detection and IME-safe handling.

### 12. Better Than Paper

**Status: Partial — continuing design goal. Priority: applies to every milestone.**

- [x] Erase without damaging a page, undo edits, rearrange pages, mix media and make editable backups.
- [ ] Evaluate changes against the full lecture-to-review workflow: fewer interruptions while writing, less effort organizing, and quicker retrieval while studying.
- [ ] Preserve immediate writing access and predictable local saving as the feature set expands.

### 13. Notebook System

**Status: Partial. Priority: P0 for the library; P1 for organization.**

- [x] Startup notebook home, explicit creation/opening, titles, category strings, title/category search and recently modified ordering.
- [ ] Add real folders/subfolders, notebook duplication, move and delete controls, favorites and a dedicated recent-notebooks view.
- [ ] Make organizational changes reversible where possible and preserve library search/selection during updates.

### 14. Page Management

**Status: Partial. Priority: P0.**

- [x] Page thumbnails, add, delete, duplicate and reorder using move-up/down commands.
- [ ] Add drag reordering, multi-page selection, cross-notebook/page move and copy, and page rotation.
- [ ] Preserve ink, text, image and PDF alignment during page transformations and bulk operations.

### 15. Bookmarks

**Status: Planned. Priority: P1.**

- [ ] Add and rename page bookmarks, show a bookmark list, and jump directly to the bookmarked page/location.
- [ ] Keep targets stable when pages are reordered and define behavior when a target is deleted.

### 16. Tags

**Status: Planned. Priority: P2.**

- [ ] Support multiple tags on notebooks and pages.
- [ ] Add tag management, filtering and search, without treating the current single category string as a complete tagging system.

### 17. Templates

**Status: Partial. Priority: P1.**

- [x] Visual choices for Blank, Ruled, Grid, Dot Grid, Cornell and Graph on new notebooks/pages and existing non-PDF pages.
- [x] Share template geometry across the editor, thumbnails and vector PDF export, with PDF round-trip regression coverage.
- [ ] Add background color, line/grid spacing, paper size and orientation choices. Keep rendering and PDF export consistent.

### 18. Custom Templates

**Status: Planned; import primitives exist. Priority: P1.**

- [x] PDF pages and imported images can be included in notebooks.
- [ ] Turn imported PDF/image material into reusable templates; save a page as a template.
- [ ] Add template naming, preview, management and removal. Ordinary content import is not yet a template library.

### 19. PDF Import

**Status: Partial. Priority: P1.**

- [x] Import all supported PDF pages into the current notebook after the selected page; retain the original PDF asset.
- [ ] Add selected-page/range import and creation of a new notebook directly from a PDF.
- [ ] Keep clear validation for unsupported files. Encrypted PDFs, interactive forms, signatures and internal/cross-page annotation actions are currently unsupported.

### 20. PDF Annotation

**Status: Partial. Priority: P1.**

- [x] Freehand ink/highlighter, erasing and lasso over imported pages, plus text boxes and images.
- [ ] Add the planned shape tools and richer text/object controls to PDF pages.
- [ ] Continue crop/rotation alignment and transparency regression checks; distinguish editable `.moye` content from flattened PDF sharing.

### 21. PDF Export

**Status: Partial. Priority: P1.**

- [x] Whole-notebook export with original PDF content, vector ink outlines, images and flattened annotations.
- [x] Added text boxes export as vector glyph outlines; original PDF text keeps its original capabilities.
- [ ] Add selected-page/range export and explicit export options where supported. Added text is currently not searchable/selectable in the exported PDF.

### 22. Images

**Status: Partial. Priority: P1.**

- [x] PNG/JPEG import, clipboard image paste, move, resize, duplicate and delete.
- [ ] Add drag-and-drop import, rotation, cropping and locking.
- [ ] Preserve original image assets and define whether transformations remain reversible in editable backups.

### 23. Screenshots

**Status: Partial. Priority: P1.**

- [x] Paste a clipboard screenshot as an image; fit it within the page from a fixed initial position.
- [ ] Improve placement at the cursor or visible writing area and offer cropping immediately after paste.
- [ ] Keep pasted text, images and notebook selections distinct so standard clipboard behavior remains predictable.

### 24. Text

**Status: Partial · Hardware validation for IME. Priority: P1.**

Implemented locally: Type creates or resumes a box; the contextual bar formats the whole box; plain list markers support Enter continuation; boxes grow to the page boundary and scroll after reaching it. The local run on **2026-09-14** passed **178 automated tests and 21 detached UI scenes**, including the linked typing and text-persistence regressions. A live Windows desktop check that day confirmed a short Chinese IME candidate selection (`t` then Space committed `他`), English input, Type resume, bold, 24 pt size entry followed by more typing, bullet continuation and native undo, and Ctrl+Enter returning to Pen. The acceptance items below stay open because clipboard, longer/interrupted composition, cross-page transitions and the complete end-to-end desktop workflow still require their respective verification.

- [x] Editable Unicode text boxes, line wrapping, movement/resizing and selected text-object color changes. The model stores font family and size.
- [ ] Complete and verify the local Type workflow: start/continue a text box, add another box, and expose font family/size, bold, italic, color and alignment for the whole box. Preserve formatting through save/reopen, editable backup and vector PDF export.
- [ ] Verify local plain bullet/number prefixes, Ctrl+B/Ctrl+I whole-box formatting, Ctrl+Enter to finish, and Escape focus handling with native text clipboard and undo.
- [ ] Verify automatic box-height growth to the page boundary and internal overflow scrolling. Page continuation is manual; there is no automatic flow to the next page.
- [ ] Add per-range rich text and structured list behavior if needed; plain line prefixes and whole-box font controls do not complete those capabilities.
- [ ] Extend the short live candidate/cancellation checks to long or interrupted composition, additional candidate choices and mixed-language editing across pages. Direct Unicode persistence tests do not cover composition, and short desktop checks do not complete IME acceptance.

### 25. Search

**Status: Partial. Priority: P2.**

- [x] Notebook-title and category search.
- [ ] Search pages, bookmarks, tags, typed content and PDF text; show useful result context and navigation targets.
- [ ] Consider handwriting/OCR search later, with an explicit offline capability and storage design rather than an assumed external service.

### 26. Page Titles

**Status: Partial. Priority: P2.**

- [x] Page numbers and template/PDF captions in page navigation.
- [ ] Add manually editable page titles and optional date/number naming patterns.
- [ ] Preserve names when pages move and make them usable in bookmarks and search.

### 27. Links

**Status: Planned. Priority: P2.**

- [ ] Add links to pages and notebooks, copyable internal links, and back navigation.
- [ ] Define stable targets, broken-link handling, and behavior when a notebook is restored as a new copy. Retained external URI annotations in a source PDF are not internal notebook links.

### 28. Split View

**Status: Planned. Priority: P1.**

- [ ] Show notebook beside notebook, PDF beside notebook, or two pages of the same notebook.
- [ ] Support adjustable ratios and horizontal/vertical arrangements.
- [ ] Define the active editing pane, shared-document save/history ownership, independent navigation and safe pen focus transfer.

### 29. Infinite Canvas

**Status: Planned. Priority: P2.**

- [ ] Add a separate canvas mode with navigation on both axes, zoom and a minimap.
- [ ] Define spatial loading, selection across large distances, meaningful limits and export/page slicing.
- [ ] Preserve compatibility with existing fixed-page notebooks and make document-mode conversion explicit. The current continuous-page workspace is not an infinite canvas.

### 30. Page Mode

**Status: Partial. Priority: P1.**

- [x] Fixed A4 pages; imported PDFs retain their page dimensions, and the data model stores per-page width/height.
- [ ] Expose A4, A5, Letter and custom sizes, portrait and landscape orientation.
- [ ] Define how existing content behaves when changing size or orientation, including undo and PDF output.

### 31. Autosave

**Status: Partial. Priority: P0.**

- [x] Completed edits schedule background transactional saves; pending revisions coalesce with a two-second target during continuous edits.
- [x] Notebook changes/normal close flush pending saves; errors retain memory snapshots and offer retry/backup rather than falsely reporting success.
- [x] Monotonic autosave scheduling with deterministic wall-clock-change regression coverage.
- [ ] Validate deadline behavior under heavy content, reduce snapshot overhead, and add durable recovery for unsaved sessions where feasible.
- [ ] Clearly communicate crash recovery boundaries: committed SQLite transactions survive reopening; in-memory edits are not a durable recovery journal.

### 32. Version History

**Status: Planned. Priority: P2.**

- [ ] Keep automatic historical snapshots with retention controls, a version list, comparison and restore-as-copy/safe restore.
- [ ] Separate durable versions from the current in-memory undo history and manual backup archives.

### 33. Local First

**Status: Existing. Priority: P0 — preserve throughout development.**

- [x] Writing, editing, PDF work and local saving work offline without an account or server.
- [x] Notebooks live in a local SQLite library; portable backups move editable content between installations.
- [ ] Keep future search, recovery and optional integrations explicit about local resources and network use. No cloud dependency is planned for basic note-taking.

### 34. Backup

**Status: Partial. Priority: P0 for recovery; P1 for automation.**

- [x] Manual single-notebook/all-notebook `.moye` packages, integrity checks and restore as new copies.
- [ ] Add scheduled local backups, retention, destination management and visible failure/recovery handling.
- [ ] Exercise restore against large real course libraries. Backups are currently unencrypted, and copying a live database file is not a replacement for a consistent export.

### 35. Open Format

**Status: Partial. Priority: P1 for documentation; P2 for broader interoperability.**

- [x] Versioned ZIP backups contain a JSON manifest and notebook structure, ISF page ink, assets and original PDFs; implementation is open source.
- [ ] Publish a standalone schema/format guide, examples and compatibility rules; add optional previews.
- [ ] Consider portable stroke-point interchange. ISF retains editable Windows ink, but is not already a plain JSON point format that every platform can read directly.

### 36. Vector Ink

**Status: Partial. Priority: P0 for current ink integrity; P2 for richer interchange.**

- [x] Editable stroke samples, pressure, drawing attributes and widths retained through ISF; vector outlines used for PDF sharing.
- [ ] Add explicit per-sample timestamp persistence and documented point/width/opacity representation where needed.
- [x] Pen opacity and pressure-sensitivity controls with ISF/clipboard regression coverage; highlighters keep native half-opacity compositing.
- [ ] Add configurable highlighter opacity and richer pressure response only with consistent display/export behavior. The model's hold-recognition clock is not persisted stroke timing.

### 37. Object Layers

**Status: Planned. Priority: P2.**

- [ ] Add object bring-forward/send-back controls and locking.
- [ ] Define a shared ordering model for ink, text, images and shapes. Current rendering has fixed paper/content/ink layers rather than a user-editable object layer stack.

### 38. Alignment and Snapping

**Status: Planned. Priority: P2.**

- [ ] Add optional grid snapping, alignment guides, rotation constraints and snapping between objects.
- [ ] Keep freehand input unconstrained unless explicitly requested, and make snapping state visible and reversible.

### 39. Selection Export

**Status: Planned. Priority: P2.**

- [ ] Copy a selection as an image or to the clipboard; export PNG, transparent PNG or a selection PDF.
- [ ] Define bounds, resolution, transparency and mixed-content rendering. Internal thumbnail rendering is not yet a selection-export feature.

### 40. Dark Paper

**Status: Planned. Priority: P2.**

- [ ] Add black, gray and cream paper choices with suitable pen color adaptation.
- [ ] Define readable preview/print/export behavior without silently recoloring existing ink or original PDF content.

### 41. Presentation

**Status: Partial. Priority: P2.**

- [x] Fullscreen focus and existing page navigation provide a basic starting point.
- [ ] Add presentation-specific hidden UI, direct page navigation, a laser pointer and temporary ink that does not enter saved notes unless requested.
- [ ] Keep presentation annotations separate from notebook edits and support a reliable return to writing mode.

## Completion policy

Close an unchecked item only after the implementation is usable, its persistence/export effects are verified, and any stated device acceptance is recorded. Changes to paper, shapes, links, layers or canvas modes must round-trip through saving and editable backups. Any remaining limitation should appear in release documentation. This roadmap can change in priority, but feature availability and validation status must stay factual.
