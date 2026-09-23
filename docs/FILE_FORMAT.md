# Moye notebook formats

Moye 1.7.0 introduced notebook sections; Moye 1.8.0 through 1.11.0 use the same file formats. Application versions, SQLite schema versions and backup format versions are independent.

## Compatibility

| File | Moye 1.7.0–1.11.0 writer | Moye 1.7.0–1.11.0 reader | Moye 1.6.2 and earlier |
|---|---|---|---|
| SQLite library | Schema 2 | Migrates schema 0/1; reads 2 | Reject schema 2 |
| `.moye` backup | Format 2 | Reads 1 and 2 | Reject format 2 |
| Writing preferences | Format 1, unchanged | Existing behavior | Unchanged |

Opening an older library migrates its schema in one transaction. Each notebook receives a **General** section, and every existing page keeps its ID, order, content and ISF ink. Section IDs for legacy notebooks are derived deterministically from the notebook ID. Back up with the older app first if you need a copy usable by that app. A format 1 backup remains readable by this build and is restored into General; restoration creates new IDs and never overwrites existing notebooks.

## Notebook structure

JSON uses UTF-8 and camel-case property names. The model in [DocumentModels.cs](../src/Moye/Models/DocumentModels.cs) defines the fields and defaults. A simplified example (ink and assets omitted) is:

```json
{
  "id": "course-id",
  "title": "Mathematics",
  "folder": "Semester 1",
  "sections": [
    { "id": "algebra-id", "title": "Linear Algebra" },
    { "id": "calculus-id", "title": "Calculus" }
  ],
  "pages": [
    { "id": "page-1", "sectionId": "algebra-id", "width": 793.700787, "height": 1122.519685 },
    { "id": "page-2", "sectionId": "calculus-id", "width": 793.700787, "height": 1122.519685 }
  ]
}
```

`sections` defines section order. Each page belongs to exactly one section through `sectionId`. `pages` is a flat ordered list, grouped by section order and preserving page order within each section. Section PDF export filters this sequence by the selected section ID, preserving page order. Empty sections are allowed. Titles need not be unique; IDs are unique within a notebook. Sections do not contain nested sections.

Pages also contain `template`, `texts`, `images`, and an optional `pdf` background reference. Paper templates use stable integer values: Blank 0, Ruled 1, Grid 2, Dot Grid 3, Cornell 4 and Graph 5. Geometry uses fixed 96-DPI page coordinates; display zoom never changes stored coordinates. Text includes font family, size in DIP, bold, italic, alignment and color. Images reference original assets; PDF references retain the original PDF asset and page/crop/rotation metadata. Ink is vector ISF with pressure information, not a flattened bitmap.

## SQLite schema 2

Moye 1.10.0 also accepts supported PDF annotations with internal destinations or interactive actions. Stored original PDF bytes are unchanged. Export normalizes only its in-memory source document, disabling those actions before copying pages while retaining annotation appearances and ordinary URI links. This requires no database or backup format change.

Moye 1.9.0 also imports Office documents by converting them locally to PDF. These pages use the existing PDF asset/reference fields, so no format migration is required. Backups contain the converted PDF and editable Moye annotations; the original DOCX, PPTX, PPSX, ODT or ODP file remains outside the notebook and is not modified.

The library uses `PRAGMA user_version=2`, foreign keys and WAL transactions. `notebooks` stores notebook identity, title, category and timestamps. `sections` stores `(notebook_id, id, ordinal, title)` with a composite primary key and a cascading notebook foreign key. `pages` stores notebook/page identity, global ordinal, JSON metadata, an ISF BLOB and a content hash. The metadata contains `sectionId`; section membership is validated by the application. `assets` stores original attachment bytes keyed by a SHA-256 content hash.

Each save transaction commits the notebook, section order and page content/order together. The implementation is [SqliteNotebookRepository.cs](../src/Moye/Services/SqliteNotebookRepository.cs). Use the app's backup command for a consistent portable copy, rather than copying an active database without its journal.

## `.moye` format 2

A `.moye` file is an unencrypted ZIP with:

- `manifest.json`: `format: "moye"`, `version: 2`, creation time, notebook descriptors and asset descriptors.
- Notebook JSON at paths listed in `manifest.notebooks[].path`, with a SHA-256 checksum for the exact bytes. Each notebook includes the section and page lists. Its page `inkData` values are empty because ink is stored separately.
- One ISF entry per page, indexed by `pageId`, `path` and `sha256` in the notebook descriptor's `inks` list. An empty page can have a zero-length ink entry.
- `assets/<sha256>.bin`: original image/PDF bytes, with filename and content type in the manifest.

Readers should follow manifest paths rather than assume notebook filenames. Import validates paths, document structure, ISF payloads, page/section references and asset checksums before storing assets. It does not decode every image or PDF during backup restoration. Format 2 requires an explicit, nonempty section list, unique section IDs, valid memberships and pages grouped in section order; malformed structures are rejected. Restore remaps notebook, section, page, text and image IDs, preserves asset content hashes, and appends “(restored copy)” to the notebook title.

Current size limits are 2 GiB for both the archive file and its aggregate uncompressed entries, 50,000 ZIP entries, 64 MiB per notebook JSON, ISF entry or manifest, 512 MiB per asset, 10,000 notebooks and 20,000 pages and sections per notebook. Section titles are nonblank and at most 10,000 characters; IDs are nonblank and at most 200 characters. Unknown format versions are rejected. [BackupService.cs](../src/Moye/Services/BackupService.cs) contains the complete validation rules. Tool presets, preferences and in-memory undo history are not part of a notebook backup.
