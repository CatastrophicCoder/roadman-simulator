"""The raw download manifest: what was downloaded, when, and under which licence."""

import json
from collections.abc import Iterable
from datetime import datetime
from pathlib import Path
from typing import Any

from mapgen import __version__
from mapgen.sources.base import DownloadedFile, isoformat_utc

MANIFEST_NAME = "download-manifest.json"
SCHEMA_VERSION = 1


def manifest_path(raw_dir: Path) -> Path:
    return raw_dir / MANIFEST_NAME


def load_entries(raw_dir: Path) -> dict[str, dict[str, Any]]:
    """Existing file entries keyed by relative path (empty if there is no valid manifest)."""
    path = manifest_path(raw_dir)
    if not path.is_file():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except ValueError:
        return {}
    files = data.get("files") if isinstance(data, dict) else None
    if not isinstance(files, list):
        return {}
    return {
        entry["path"]: entry
        for entry in files
        if isinstance(entry, dict) and isinstance(entry.get("path"), str)
    }


def merge_entries(
    previous: dict[str, dict[str, Any]],
    refreshed_sources: Iterable[str],
    files: Iterable[DownloadedFile],
    raw_dir: Path,
) -> list[dict[str, Any]]:
    """Replace the entries of the sources that ran; keep the rest if their files still exist."""
    refreshed = set(refreshed_sources)
    merged = {
        rel: entry
        for rel, entry in previous.items()
        if entry.get("source") not in refreshed and (raw_dir / rel).is_file()
    }
    for item in files:
        entry = item.to_manifest_entry(raw_dir)
        merged[entry["path"]] = entry
    return [merged[key] for key in sorted(merged)]


def write_manifest(
    raw_dir: Path,
    map_id: str,
    entries: list[dict[str, Any]],
    generated_at: datetime,
) -> Path:
    path = manifest_path(raw_dir)
    raw_dir.mkdir(parents=True, exist_ok=True)
    document = {
        "schema_version": SCHEMA_VERSION,
        "map_id": map_id,
        "generated_at": isoformat_utc(generated_at),
        "tool": {"name": "mapgen", "version": __version__},
        "files": entries,
    }
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    tmp.replace(path)
    return path
