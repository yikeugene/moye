"""Verify synthetic Office import smoke artifacts without opening Office or Moye.

Run after Moye.UiPreview --office-import-smoke finishes successfully:
    python tools/verify-office-fixtures.py RUN_DIRECTORY --poppler-bin POPPLER_BIN

Reads ../source/expected.json and writes verification.json in RUN_DIRECTORY.
Requires Pillow and Poppler pdfinfo; uses pdftotext when available, otherwise
pypdf. These checks supplement visual review; they do not prove arbitrary
Office documents render identically in every converter.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image


def executable(name: str, directory: Path | None) -> str | None:
    if directory:
        for suffix in (".exe", ""):
            candidate = directory / (name + suffix)
            if candidate.is_file():
                return str(candidate)
    return shutil.which(name)


def command(arguments: list[str]) -> str:
    result = subprocess.run(arguments, capture_output=True, encoding="utf-8",
                            errors="replace", timeout=30, check=False)
    if result.returncode:
        raise RuntimeError(f"{Path(arguments[0]).name} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def existing(path: Path) -> Path:
    require(path.is_file() and path.stat().st_size > 0, f"Missing or empty artifact: {path.name}")
    return path


def pdf_check(path: Path, fixture: dict, pdfinfo: str, pdftotext: str | None) -> dict:
    existing(path)
    metadata = command([pdfinfo, str(path)])
    count = re.search(r"^Pages:\s+(\d+)\s*$", metadata, re.MULTILINE)
    require(count is not None, f"No page count reported for {path.name}")
    pages = int(count.group(1))
    require(pages == fixture["pages"], f"{path.name}: expected {fixture['pages']} pages, got {pages}")
    if pdftotext:
        content = command([pdftotext, "-layout", "-enc", "UTF-8", str(path), "-"])
    else:
        from pypdf import PdfReader
        content = "\n".join(page.extract_text() or "" for page in PdfReader(str(path)).pages)
    normalized = " ".join(content.split())
    missing = [marker for marker in fixture["textMarkers"] if marker not in normalized]
    require(not missing, f"{path.name}: missing searchable source text: {missing}")
    return {"file": path.name, "pages": pages, "textMarkers": fixture["textMarkers"],
            "textExtractor": "pdftotext" if pdftotext else "pypdf"}


def image_check(path: Path, fixture: dict, colors: list[list[int]]) -> dict:
    existing(path)
    with Image.open(path) as image:
        image.load()
        width, height = image.size
        require(abs(width - fixture["pageWidthDip"]) < 2 and abs(height - fixture["pageHeightDip"]) < 2,
                f"{path.name}: unexpected rendered size {width}x{height}")
        rgb = image.convert("RGB")
        palette = rgb.getcolors(maxcolors=width * height)
        require(palette is not None, f"Cannot count colors in {path.name}")
        # A generous color tolerance permits Office image compression and edge
        # antialiasing. A 100-pixel minimum rejects isolated text/edge matches.
        counts = [sum(count for count, actual in palette
                      if all(abs(actual[channel] - color[channel]) <= 18 for channel in range(3)))
                  for color in colors]
        require(all(count >= 100 for count in counts),
                f"{path.name}: missing embedded image color patch, pixel counts {counts}")
    return {"file": path.name, "width": width, "height": height,
            "patchPixels": counts, "channelTolerance": 18, "minimumPatchPixels": 100}


def verify(run: Path, poppler_bin: Path | None) -> dict:
    expected_path = existing(run.parent / "source" / "expected.json")
    expected = json.loads(expected_path.read_text(encoding="utf-8-sig"))
    native = json.loads(existing(run / "report.json").read_text(encoding="utf-8-sig"))
    pdfinfo = executable("pdfinfo", poppler_bin)
    require(pdfinfo is not None, "pdfinfo was not found; provide --poppler-bin or add Poppler to PATH")
    pdftotext = executable("pdftotext", poppler_bin)
    report = {"status": "passed", "runDirectory": str(run), "textExtractor": "pdftotext" if pdftotext else "pypdf",
              "pdfinfo": pdfinfo, "files": [], "errors": []}
    existing(run / "moye.db")
    for fixture in expected["files"]:
        name = fixture["name"]
        item = {"name": name, "status": "passed", "pdfs": [], "images": []}
        report["files"].append(item)
        try:
            require(Path(name).name == name, "Fixture names must be simple filenames")
            source = existing(expected_path.parent / name)
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            require(digest == fixture["sha256"], f"{name}: source bytes changed")
            item["sourceSha256"] = digest
            records = [entry for entry in native if entry.get("name") == name]
            require(len(records) == 1, f"{name}: missing or duplicate native smoke report")
            entry = records[0]
            require(entry.get("sourceSha256") == digest and entry.get("pages") == fixture["pages"],
                    f"{name}: native report belongs to different source bytes or has incorrect page count")
            for stage in ("conversion", "saveReopen", "backupRestore", "annotatedPdfRoundTrip"):
                require(entry.get(stage) == "passed", f"{name}: native {stage} did not pass")
            existing(run / (name + ".moye"))
            for suffix in ("-converted.pdf", "-annotated.pdf"):
                item["pdfs"].append(pdf_check(run / (name + suffix), fixture, pdfinfo, pdftotext))
            for suffix in [f"-page-{page}.png" for page in range(1, fixture["pages"] + 1)] + ["-annotated.png"]:
                item["images"].append(image_check(run / (name + suffix), fixture, expected["embeddedImageRgb"]))
        except Exception as error:
            item["status"] = "failed"
            item["error"] = str(error)
            report["errors"].append(f"{name}: {error}")
    if report["errors"]:
        report["status"] = "failed"
    return report


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_directory", type=Path)
    parser.add_argument("--poppler-bin", type=Path)
    args = parser.parse_args()
    run = args.run_directory.resolve()
    if not run.is_dir():
        parser.error(f"Run directory does not exist: {run}")
    try:
        report = verify(run, args.poppler_bin.resolve() if args.poppler_bin else None)
    except Exception as error:
        report = {"status": "failed", "runDirectory": str(run), "errors": [str(error)]}
    destination = run / "verification.json"
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for fixture in report.get("files", []):
        print(f"{fixture['name']}: {fixture['status']}; {len(fixture['pdfs'])} PDFs, {len(fixture['images'])} rendered images")
    for error in report["errors"]:
        print(f"ERROR: {error}")
    print(f"Verification {report['status']}; text extractor: {report.get('textExtractor', 'unavailable')}; report: {destination}")
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
