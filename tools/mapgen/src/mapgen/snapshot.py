"""Pinned raw data snapshots: `mapgen snapshot create`, `publish` and `fetch` (ADR 0011).

A snapshot is one zip archive laid out like `data/raw/<map-id>/`: the files of every
enabled, non-`debug_only` source exactly as the download manifest lists them (the OSM file
without contributor metadata), the filtered `download-manifest.json`, `SHA256SUMS` and a
generated `NOTICE.md`. `create` reads only the local raw folder and never contacts the
network. `publish` uploads an archive as the single asset of a new GitHub release with
`gh`, under the next tag of its date (an existing data release is never changed),
and writes the `snapshot:` block into map.yaml; only the user runs it. `fetch`
downloads the pinned archive with plain HTTP, treats it as untrusted, and never replaces
local files of the snapshot's sources without `force`.

External commands (the leak check script, `gh`) go through an injectable `Runner`, and
HTTP through `HttpClient`, so tests never start `gh` or touch the network.
"""

import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import zipfile
from collections.abc import Callable, Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import IO, Any

import yaml

from mapgen import __version__, manifest, paths
from mapgen.config import (
    REPO_PATTERN,
    ConfigError,
    MapConfig,
    SnapshotConfig,
    snapshot_tag_pattern,
)
from mapgen.http import HttpClient, HttpError, UrllibClient
from mapgen.notice import LICENCE_URLS, NoticeError, licence_url
from mapgen.sources.base import isoformat_utc, parse_isoformat, sha256_of, utc_now
from mapgen.sources.mml import list_files
from mapgen.sources.osm import METADATA_ATTRIBUTES, strip_metadata

SUMS_NAME = "SHA256SUMS"
NOTICE_NAME = "NOTICE.md"
METADATA_FILES = (manifest.MANIFEST_NAME, NOTICE_NAME, SUMS_NAME)
SNAPSHOT_SUBDIR = "snapshot"  # data/raw/<map-id>/snapshot/: NOTICE.md and SHA256SUMS of a fetch
OSM_MODIFICATION = "contributor metadata removed"
LEAK_CHECK_SCRIPT = Path(".github") / "scripts" / "check-no-secret.sh"
ZIP_DATE_TIME = (1980, 1, 1, 0, 0, 0)  # every member gets this timestamp (reproducible zips)
MAX_METADATA_BYTES = 16 * 1024 * 1024  # SHA256SUMS, manifest and NOTICE of an archive
_CHUNK = 1024 * 1024
_SUMS_LINE = re.compile(r"^([0-9a-f]{64})  (\S(?:.*\S)?)$")
_ASSET_DATE = r"(\d{4}-\d{2}-\d{2})"

Runner = Callable[[Sequence[str]], "subprocess.CompletedProcess[str]"]


class SnapshotError(Exception):
    """A snapshot could not be created, published or fetched. Shown to the user as is."""


def run_command(args: Sequence[str]) -> "subprocess.CompletedProcess[str]":
    """The real runner: captures output, never raises on a non-zero exit."""
    return subprocess.run(list(args), capture_output=True, text=True, check=False)


def snapshot_tag(map_id: str, moment: datetime) -> str:
    return f"data-{map_id}-{_utc_date(moment)}"


def snapshot_asset(map_id: str, moment: datetime) -> str:
    return f"mapdata-{map_id}-{_utc_date(moment)}.zip"


def asset_for_tag(map_id: str, tag: str) -> str:
    """The asset of a snapshot tag: data-<id>-<date>[.<n>] -> mapdata-<id>-<date>[.<n>].zip."""
    if not re.fullmatch(snapshot_tag_pattern(map_id), tag):
        raise SnapshotError(f"{tag!r} is not a snapshot tag of map {map_id}")
    return f"mapdata-{tag.removeprefix('data-')}.zip"


def next_tag(map_id: str, date_text: str, taken: Iterable[str]) -> str:
    """The plain tag data-<id>-<date> when no tag of that date exists, else the highest
    existing number plus one (the plain tag counts as 1), so a gap is never filled. Only
    existing tags are seen: deleting the highest-numbered one frees its number again."""
    base = f"data-{map_id}-{date_text}"
    numbers = [
        int(match.group(1) or 1)
        for tag in taken
        if (match := re.fullmatch(rf"{re.escape(base)}(?:\.([0-9]+))?", tag))
    ]
    if not numbers:
        return base
    return f"{base}.{max(max(numbers) + 1, 2)}"


def _utc_date(moment: datetime) -> str:
    return isoformat_utc(moment)[:10]


def safe_member_path(name: str) -> PurePosixPath:
    """`name` as a relative POSIX path, or SnapshotError if it could leave its folder."""
    if not name or "\\" in name or "\0" in name or name.startswith("/"):
        raise SnapshotError(f"unsafe path {name!r} in the snapshot")
    path = PurePosixPath(name)
    if any(part in ("", ".", "..") for part in name.split("/")) or ":" in path.parts[0]:
        raise SnapshotError(f"unsafe path {name!r} in the snapshot")
    return path


def _is_data_path(path: PurePosixPath) -> bool:
    """Source files live below a folder, never at the top or in `snapshot/`."""
    return len(path.parts) > 1 and path.parts[0] != SNAPSHOT_SUBDIR


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _json_bytes(document: Any) -> bytes:
    return (json.dumps(document, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def sums_text(checksums: Mapping[str, str]) -> str:
    """`sha256sum -c` lines, sorted by path."""
    return "".join(f"{checksums[path]}  {path}\n" for path in sorted(checksums))


def parse_sums(text: str) -> dict[str, str]:
    sums: dict[str, str] = {}
    for number, line in enumerate(text.splitlines(), start=1):
        match = _SUMS_LINE.match(line)
        if not match:
            raise SnapshotError(f"{SUMS_NAME} line {number} is not '<sha256>  <path>'")
        checksum, name = match.groups()
        if name in sums:
            raise SnapshotError(f"{SUMS_NAME} lists {name} twice")
        safe_member_path(name)
        sums[name] = checksum
    return sums


# NOTICE.md


def render_notice(map_id: str, entries: Sequence[Mapping[str, Any]]) -> str:
    """The snapshot's NOTICE.md, generated only from its manifest entries.

    Raises SnapshotError for a licence without a URL in `notice.LICENCE_URLS`.
    """
    groups: dict[tuple[str, str], list[Mapping[str, Any]]] = {}
    for entry in sorted(entries, key=lambda e: str(e["path"])):
        key = (str(entry["licence"]), str(entry["attribution"]))
        groups.setdefault(key, []).append(entry)
    lines = [
        f"# Map data snapshot for `{map_id}`",
        "",
        f"Raw data of the map `{map_id}`, laid out like `tools/mapgen/data/raw/{map_id}/`. "
        f"Release builds fetch it with `mapgen snapshot fetch {map_id}` and build the map "
        "pack from it.",
        "",
        "## Attribution and licences",
        "",
    ]
    for (licence, attribution), group in groups.items():
        try:
            url = licence_url(licence)
        except NoticeError as exc:
            raise SnapshotError(str(exc)) from None
        lines += [attribution, "", f"- Licence: {licence}, {url}"]
        for entry in group:
            lines.append(f"- `{entry['path']}`, retrieved {str(entry['retrieved_at'])[:10]}")
        if licence == "ODbL 1.0":
            dataset = str(group[0]["dataset"])
            lines.append(
                f"- The {dataset} extract is a derivative database of the {dataset} database. "
                f"It is offered under the ODbL 1.0, {url}."
            )
        lines.append("")
    lines += ["## Changes", ""]
    modified = [e for e in entries if e.get("modifications")]
    for entry in sorted(modified, key=lambda e: str(e["path"])):
        detail = ""
        if entry["modifications"] == OSM_MODIFICATION:
            detail = f" (the attributes {', '.join(METADATA_ATTRIBUTES)})"
        lines.append(
            f"- `{entry['path']}`: {entry['modifications']}{detail}. The original file is "
            "identified by `source_size` and `source_sha256` in `download-manifest.json`."
        )
    unchanged = [e for e in entries if not e.get("modifications")]
    if unchanged:
        lines.append(
            "- All other files are as downloaded and identified by their `sha256` in "
            "`download-manifest.json`."
        )
    return "\n".join(lines) + "\n"


# create


@dataclass(frozen=True)
class SnapshotArchive:
    path: Path
    size: int
    sha256: str
    tag: str
    asset: str
    members: tuple[tuple[str, int], ...]  # (path, uncompressed size), in archive order


def snapshot_sources(config: MapConfig) -> list[str]:
    """The sources a snapshot holds: enabled and not `debug_only`, in map.yaml order."""
    return list(config.for_release().enabled_sources())


def _included_entries(
    config: MapConfig, raw_dir: Path, entries: Mapping[str, Mapping[str, Any]]
) -> list[dict[str, Any]]:
    """The manifest entries of the snapshot's sources, each checked against its file."""
    included: list[dict[str, Any]] = []
    for source in snapshot_sources(config):
        own = [dict(e) for rel, e in sorted(entries.items()) if e.get("source") == source]
        if not own:
            raise SnapshotError(
                f"source {source} has no files in {manifest.MANIFEST_NAME}; run "
                f"`mapgen download {config.id} --only {source}` first"
            )
        for entry in own:
            rel = str(entry["path"])
            if not _is_data_path(safe_member_path(rel)):
                raise SnapshotError(f"source {source}: {rel} can't be in a snapshot")
            path = raw_dir / rel
            if not path.is_file():
                raise SnapshotError(f"source {source}: {rel} is in the manifest but missing")
            if sha256_of(path) != entry.get("sha256"):
                raise SnapshotError(
                    f"source {source}: {rel} differs from its manifest checksum; run "
                    f"`mapgen download {config.id} --only {source}` to register it again"
                )
            included.append(entry)
    return included


def _snapshot_entry(entry: dict[str, Any], original: Path, archived: Path) -> dict[str, Any]:
    """The manifest entry of a file the snapshot changed (keeps an earlier `source_*`)."""
    updated = dict(entry)
    updated["size"] = archived.stat().st_size
    updated["sha256"] = sha256_of(archived)
    updated["source_size"] = entry.get("source_size", original.stat().st_size)
    updated["source_sha256"] = entry.get("source_sha256", entry["sha256"])
    updated["modifications"] = OSM_MODIFICATION
    return updated


def _zip_info(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=ZIP_DATE_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # Unix, on every platform
    info.external_attr = 0o100644 << 16  # a regular file, rw-r--r--
    return info


def _copy_stream(source: IO[bytes], dest: IO[bytes]) -> None:
    while chunk := source.read(_CHUNK):
        dest.write(chunk)


def write_zip(dest: Path, members: Mapping[str, Path | bytes]) -> None:
    """A reproducible zip: sorted members, fixed timestamps and attributes, deflated."""
    with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(members):
            content = members[name]
            if isinstance(content, bytes):
                archive.writestr(_zip_info(name), content)
            else:
                with content.open("rb") as source, archive.open(_zip_info(name), "w") as out:
                    _copy_stream(source, out)


def leak_check(archive: Path, repo_root: Path, runner: Runner) -> None:
    """Run `.github/scripts/check-no-secret.sh` on `archive`; SnapshotError if it fails.

    The script itself reads `MML_API_KEY` from the environment (literal mode) and prints
    only file names, never a match.
    """
    script = repo_root / LEAK_CHECK_SCRIPT
    if not script.is_file():
        raise SnapshotError(f"leak check script not found: {script}")
    try:
        result = runner(["bash", str(script), str(archive)])
    except OSError as exc:
        raise SnapshotError(f"could not run the leak check: {exc}") from None
    if result.returncode != 0:
        output = (result.stderr or result.stdout or "").strip()
        raise SnapshotError(f"leak check failed for {archive.name}: {output}")


def create_snapshot(
    config: MapConfig,
    *,
    repo_root: Path,
    raw_dir: Path,
    out_dir: Path,
    runner: Runner = run_command,
    now: Callable[[], datetime] = utc_now,
) -> SnapshotArchive:
    """Zip the snapshot's sources from `raw_dir` into `out_dir/mapdata-<id>-<date>.zip`.

    Reads only `raw_dir`; the same raw data gives the same bytes, whatever the time of day
    (the manifest's `generated_at` is the latest `retrieved_at` of its entries).
    """
    if not manifest.manifest_path(raw_dir).is_file():
        raise SnapshotError(
            f"{manifest.manifest_path(raw_dir)} not found; run `mapgen download {config.id}`"
        )
    included = _included_entries(config, raw_dir, manifest.load_entries(raw_dir))
    moment = now()
    tag, asset = snapshot_tag(config.id, moment), snapshot_asset(config.id, moment)

    with tempfile.TemporaryDirectory(prefix="mapgen-snapshot-") as tmp:
        staging = Path(tmp)
        members: dict[str, Path | bytes] = {}
        entries: list[dict[str, Any]] = []
        for entry in included:
            rel = str(entry["path"])
            local = raw_dir / rel
            stripped = staging / rel
            if local.suffix.lower() == ".osm" and strip_metadata(local, stripped):
                entry = _snapshot_entry(entry, local, stripped)
                members[rel] = stripped
            else:  # archived byte for byte (an entry from an earlier snapshot stays as is)
                members[rel] = local
            entries.append(entry)

        entries.sort(key=lambda e: str(e["path"]))
        generated = max(parse_isoformat(str(e["retrieved_at"])) for e in entries)
        document = {
            "schema_version": manifest.SCHEMA_VERSION,
            "map_id": config.id,
            "generated_at": isoformat_utc(generated),
            "tool": {"name": "mapgen", "version": __version__},
            "files": entries,
        }
        members[manifest.MANIFEST_NAME] = _json_bytes(document)
        members[NOTICE_NAME] = render_notice(config.id, entries).encode("utf-8")
        checksums = {str(e["path"]): str(e["sha256"]) for e in entries}
        for name in (manifest.MANIFEST_NAME, NOTICE_NAME):
            content = members[name]
            assert isinstance(content, bytes)
            checksums[name] = _sha256_bytes(content)
        members[SUMS_NAME] = sums_text(checksums).encode("utf-8")

        out_dir.mkdir(parents=True, exist_ok=True)
        dest = out_dir / asset
        # Ends in .zip so the leak check unpacks it; replaces `dest` only once it passed.
        part = out_dir / f".{dest.stem}.part.zip"
        try:
            write_zip(part, members)
            leak_check(part, repo_root, runner)
            part.replace(dest)
        finally:
            part.unlink(missing_ok=True)

    with zipfile.ZipFile(dest) as archive:
        listed = tuple((i.filename, i.file_size) for i in archive.infolist())
    return SnapshotArchive(dest, dest.stat().st_size, sha256_of(dest), tag, asset, listed)


# fetch


@dataclass
class FetchReport:
    url: str
    added: list[str] = field(default_factory=list)
    replaced: list[str] = field(default_factory=list)
    deleted: list[str] = field(default_factory=list)
    unchanged: list[str] = field(default_factory=list)
    manifest_path: Path | None = None


NO_SNAPSHOT_MESSAGE = (
    "map.yaml has no snapshot block; run `mapgen snapshot publish` or `mapgen download`"
)


def _read_member(archive: zipfile.ZipFile, name: str) -> bytes:
    info = archive.getinfo(name)
    if info.file_size > MAX_METADATA_BYTES:
        raise SnapshotError(f"{name} in the snapshot is too large ({info.file_size} bytes)")
    with archive.open(info) as handle:
        data = handle.read(MAX_METADATA_BYTES + 1)
    if len(data) > MAX_METADATA_BYTES:
        raise SnapshotError(f"{name} in the snapshot is too large")
    return data


def _check_members(archive: zipfile.ZipFile) -> dict[str, str]:
    """Refuse unsafe or unlisted members; returns the SHA256SUMS mapping."""
    names = [info.filename for info in archive.infolist()]
    if len(set(names)) != len(names):
        raise SnapshotError("the snapshot lists a member twice")
    if len({name.casefold() for name in names}) != len(names):
        # Would overwrite each other on a case-insensitive file system (macOS, Windows).
        raise SnapshotError("the snapshot has members whose names differ only in case")
    for info in archive.infolist():
        safe_member_path(info.filename)
        mode = (info.external_attr >> 16) & 0o170000
        if mode == 0o120000:
            raise SnapshotError(f"the snapshot member {info.filename} is a symlink")
        if info.is_dir():
            raise SnapshotError(f"the snapshot member {info.filename} is a folder entry")
    if SUMS_NAME not in names:
        raise SnapshotError(f"the snapshot has no {SUMS_NAME}")
    try:
        sums = parse_sums(_read_member(archive, SUMS_NAME).decode("utf-8"))
    except UnicodeDecodeError:
        raise SnapshotError(f"{SUMS_NAME} is not UTF-8 text") from None
    allowed = set(sums) | {SUMS_NAME}
    unlisted = sorted(set(names) - allowed)
    if unlisted:
        raise SnapshotError(f"the snapshot has members not in {SUMS_NAME}: {', '.join(unlisted)}")
    missing = sorted(allowed - set(names))
    if missing:
        raise SnapshotError(f"{SUMS_NAME} lists files missing from the snapshot: {missing}")
    for name in (manifest.MANIFEST_NAME, NOTICE_NAME):
        if name not in sums:
            raise SnapshotError(f"the snapshot has no {name}")
    return sums


# Required keys of a bundled manifest entry and their JSON types.
ENTRY_FIELDS: dict[str, type | tuple[type, ...]] = {
    "path": str,
    "source": str,
    "sha256": str,
    "size": int,
    "retrieved_at": str,
    "licence": str,
    "attribution": str,
}


def _check_entry_fields(entry: Any, index: int) -> None:
    where = f"the snapshot's {manifest.MANIFEST_NAME}, file {index + 1}"
    if not isinstance(entry, dict):
        raise SnapshotError(f"{where} is not an object")
    for key, kind in ENTRY_FIELDS.items():
        value = entry.get(key)
        if not isinstance(value, kind) or isinstance(value, bool):
            raise SnapshotError(f"{where} has no valid {key!r}")
    if not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]) or entry["size"] < 0:
        raise SnapshotError(f"{where} has an invalid sha256 or size")
    try:
        parse_isoformat(entry["retrieved_at"])
    except ValueError:
        raise SnapshotError(f"{where} has an invalid retrieved_at") from None


def _bundled_entries(
    config: MapConfig, archive: zipfile.ZipFile, sums: Mapping[str, str]
) -> list[dict[str, Any]]:
    """The bundled manifest's entries, checked against SHA256SUMS and the map config."""
    try:
        document = json.loads(_read_member(archive, manifest.MANIFEST_NAME))
    except ValueError:
        raise SnapshotError(f"the snapshot's {manifest.MANIFEST_NAME} is not JSON") from None
    if not isinstance(document, dict) or document.get("schema_version") != 1:
        raise SnapshotError(f"the snapshot's {manifest.MANIFEST_NAME} is not schema version 1")
    if document.get("map_id") != config.id:
        raise SnapshotError(f"the snapshot is for map {document.get('map_id')!r}, not {config.id}")
    files = document.get("files")
    if not isinstance(files, list) or not files:
        raise SnapshotError(f"the snapshot's {manifest.MANIFEST_NAME} lists no files")
    expected_sources = snapshot_sources(config)
    entries: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, entry in enumerate(files):
        _check_entry_fields(entry, index)
        rel = entry["path"]
        if rel in seen:
            raise SnapshotError(f"the snapshot's {manifest.MANIFEST_NAME} lists {rel} twice")
        seen.add(rel)
        if not _is_data_path(safe_member_path(rel)):
            raise SnapshotError(f"the snapshot's manifest lists {rel}, which isn't a data path")
        source = entry["source"]
        if source not in expected_sources:
            raise SnapshotError(
                f"the snapshot's {rel} belongs to source {source!r}, which isn't an enabled, "
                "non-debug source in map.yaml"
            )
        if sums.get(rel) != entry.get("sha256"):
            raise SnapshotError(f"{rel}: the bundled manifest and {SUMS_NAME} disagree")
        if archive.getinfo(rel).file_size != entry.get("size"):
            raise SnapshotError(f"{rel}: its size differs from the bundled manifest")
        entries.append(entry)
    missing_sources = [
        name for name in expected_sources if name not in {e["source"] for e in entries}
    ]
    if missing_sources:
        raise SnapshotError(
            f"the snapshot has no files of source(s) {', '.join(missing_sources)}, which "
            "map.yaml enables; publish a new snapshot"
        )
    data_paths = {e["path"] for e in entries}
    extra = sorted(set(sums) - data_paths - {manifest.MANIFEST_NAME, NOTICE_NAME})
    if extra:
        raise SnapshotError(f"{SUMS_NAME} lists files the manifest doesn't: {', '.join(extra)}")
    return entries


def _unpack(archive: zipfile.ZipFile, sums: Mapping[str, str], dest: Path) -> None:
    """Write every member below `dest` (paths already checked) and verify its checksum."""
    for name, expected in sums.items():
        target = dest.joinpath(*PurePosixPath(name).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        with archive.open(name) as source, target.open("wb") as out:
            while chunk := source.read(_CHUNK):
                digest.update(chunk)
                out.write(chunk)
        if digest.hexdigest() != expected:
            raise SnapshotError(f"{name} doesn't match its checksum in {SUMS_NAME}")
    (dest / SUMS_NAME).write_bytes(_read_member(archive, SUMS_NAME))


def _source_folders(entries: Iterable[Mapping[str, Any]]) -> set[PurePosixPath]:
    """The folders that hold the snapshot's source files (where the loaders look)."""
    return {PurePosixPath(str(e["path"])).parent for e in entries}


def _local_changes(
    raw_dir: Path,
    entries: Sequence[Mapping[str, Any]],
    previous: Mapping[str, Mapping[str, Any]],
) -> tuple[list[str], list[str], list[str], list[str]]:
    """(missing, differing, identical, extra) local paths for the snapshot's files.

    Extra files are files in the snapshot sources' folders that the snapshot doesn't have,
    except files of other sources in the local manifest.
    """
    snapshot_paths = {str(e["path"]) for e in entries}
    sources = {e["source"] for e in entries}
    others = {rel for rel, e in previous.items() if e.get("source") not in sources}
    missing, differing, identical = [], [], []
    for entry in entries:
        rel = str(entry["path"])
        local = raw_dir / rel
        if not local.exists() and not local.is_symlink():
            missing.append(rel)
        elif local.is_file() and not local.is_symlink() and sha256_of(local) == entry["sha256"]:
            identical.append(rel)
        else:
            differing.append(rel)
    extra: set[str] = set()
    for folder in _source_folders(entries):
        for path in list_files(raw_dir.joinpath(*folder.parts)):
            rel = path.relative_to(raw_dir).as_posix()
            if rel not in snapshot_paths and rel not in others:
                extra.add(rel)
    return missing, differing, identical, sorted(extra)


def _place(source: Path, dest: Path) -> None:
    """Move `source` to `dest` through a `.part` file (the loaders skip `.part` files)."""
    if dest.is_dir() and not dest.is_symlink():
        raise SnapshotError(f"{dest} is a folder; remove it first")
    dest.parent.mkdir(parents=True, exist_ok=True)
    part = dest.with_name(dest.name + ".part")
    try:
        shutil.copyfile(source, part)
        part.replace(dest)
    finally:
        part.unlink(missing_ok=True)


def fetch_snapshot(
    config: MapConfig,
    *,
    raw_dir: Path,
    force: bool = False,
    http: HttpClient | None = None,
    now: Callable[[], datetime] = utc_now,
    log: Callable[[str], None] = print,
) -> FetchReport:
    """Download the snapshot pinned in map.yaml and place it in `raw_dir`.

    Raises ConfigError without a snapshot block and SnapshotError for every other failure.
    Nothing in `raw_dir` changes until the archive has passed every check.
    """
    snapshot = config.snapshot
    if snapshot is None:
        raise ConfigError(NO_SNAPSHOT_MESSAGE)
    http = http or UrllibClient()
    url = snapshot.asset_url()
    report = FetchReport(url)

    with tempfile.TemporaryDirectory(prefix="mapgen-fetch-") as tmp:
        work = Path(tmp)
        downloaded = work / "archive.zip"
        log(f"downloading {url}")
        try:
            http.download(url, downloaded)
        except HttpError as exc:
            if exc.status == 404:
                raise SnapshotError(
                    f"snapshot asset {snapshot.asset} not found on release {snapshot.release_tag}"
                ) from None
            raise SnapshotError(f"snapshot download failed: {exc}") from None
        actual = sha256_of(downloaded)
        if actual != snapshot.sha256:
            raise SnapshotError(
                f"snapshot {snapshot.asset} has SHA-256 {actual}, but map.yaml expects "
                f"{snapshot.sha256}; nothing was changed"
            )

        unpacked = work / "unpacked"
        try:
            with zipfile.ZipFile(downloaded) as archive:
                sums = _check_members(archive)
                entries = _bundled_entries(config, archive, sums)
                _unpack(archive, sums, unpacked)
        except zipfile.BadZipFile as exc:
            raise SnapshotError(f"snapshot {snapshot.asset} is not a valid zip: {exc}") from None

        previous = manifest.load_entries(raw_dir)
        sources = {str(e["source"]) for e in entries}
        taken = sorted(
            str(e["path"])
            for e in entries
            if str(e["path"]) in previous and previous[str(e["path"])].get("source") not in sources
        )
        if taken:
            raise SnapshotError(
                "the local manifest assigns these snapshot files to another source; nothing "
                f"was changed: {', '.join(taken)}"
            )
        missing, differing, identical, extra = _local_changes(raw_dir, entries, previous)
        if (differing or extra) and not force:
            lines = [f"  differs from the snapshot: {rel}" for rel in differing]
            lines += [f"  not in the snapshot: {rel}" for rel in extra]
            raise SnapshotError(
                "local data of the snapshot's sources would change; nothing was changed. "
                "Rerun with --force to replace and delete these files:\n" + "\n".join(lines)
            )

        # Place every file first and delete extras only after that, so a failure leaves
        # no source with fewer files than before. The manifest is written in any case and
        # describes what is on disk; rerunning with --force completes an interrupted fetch.
        placed = set(identical)
        completed = False
        try:
            for rel in missing + differing:
                _place(unpacked / rel, raw_dir / rel)
                placed.add(rel)
            for rel in extra:
                (raw_dir / rel).unlink()
            notice_dir = raw_dir / SNAPSHOT_SUBDIR
            for name in (NOTICE_NAME, SUMS_NAME):
                _place(unpacked / name, notice_dir / name)
            completed = True
        finally:
            report.manifest_path = _write_fetched_manifest(
                config, raw_dir, previous, entries, placed, completed, now()
            )

    report.added, report.replaced = sorted(missing), sorted(differing)
    report.unchanged, report.deleted = sorted(identical), extra
    return report


def _write_fetched_manifest(
    config: MapConfig,
    raw_dir: Path,
    previous: Mapping[str, Mapping[str, Any]],
    entries: Sequence[Mapping[str, Any]],
    placed: set[str],
    completed: bool,
    moment: datetime,
) -> Path:
    """Merge the snapshot entries of the files now on disk into the local manifest.

    The entries of the snapshot's sources are replaced (`manifest.merge_entries`). After an
    interrupted fetch, a file that wasn't replaced yet keeps its previous entry.
    """
    sources = {str(e["source"]) for e in entries}
    kept = manifest.merge_entries(previous, sources, [], raw_dir)
    fetched = [dict(e) for e in entries if str(e["path"]) in placed]
    if not completed:
        kept += [
            dict(e)
            for rel, e in previous.items()
            if e.get("source") in sources and rel not in placed and (raw_dir / rel).is_file()
        ]
    merged = sorted([*kept, *fetched], key=lambda e: str(e["path"]))
    return manifest.write_manifest(raw_dir, config.id, merged, moment)


# publish


@dataclass(frozen=True)
class PublishResult:
    repo: str
    tag: str
    asset: str
    sha256: str
    map_yaml: Path


def set_snapshot_block(yaml_text: str, block: Mapping[str, str]) -> str:
    """`yaml_text` with its top-level `snapshot:` block replaced by (or appended with) `block`.

    Only the block's own lines change: comments and every other line stay as they are.
    Values are written as double-quoted strings, so a digits-only checksum stays a string.
    """
    new_block = ["snapshot:", *(f"  {key}: {json.dumps(str(v))}" for key, v in block.items())]
    lines = yaml_text.splitlines()
    start = next((i for i, line in enumerate(lines) if re.match(r"^snapshot:(\s|$)", line)), None)
    if start is None:
        while lines and not lines[-1].strip():
            lines.pop()
        comment = "# Pinned raw data for release builds; written by `mapgen snapshot publish`."
        lines += ["", comment, *new_block]
    else:
        end = start
        for i in range(start + 1, len(lines)):
            if not lines[i].strip():
                continue
            if lines[i][0] in " \t":
                end = i
            else:
                break
        lines[start : end + 1] = new_block
    text = "\n".join(lines) + "\n"
    parsed = yaml.safe_load(text)
    if not isinstance(parsed, dict) or parsed.get("snapshot") != {
        k: str(v) for k, v in block.items()
    }:
        raise ValueError("the snapshot block could not be written into map.yaml")
    return text


def _gh(runner: Runner, args: Sequence[str]) -> "subprocess.CompletedProcess[str]":
    try:
        return runner(["gh", *args])
    except FileNotFoundError:
        raise SnapshotError(
            "gh (GitHub CLI) is not installed; see https://cli.github.com/"
        ) from None
    except OSError as exc:
        raise SnapshotError(f"could not run gh: {exc}") from None


def _gh_lines(runner: Runner, args: Sequence[str], what: str) -> list[str]:
    """The non-empty output lines of a read-only `gh` command; fails on any error."""
    result = _gh(runner, args)
    if result.returncode != 0:
        detail = (result.stderr or "").strip()
        raise SnapshotError(f"could not list the {what} ({detail}); nothing was published")
    return [line.strip() for line in (result.stdout or "").splitlines() if line.strip()]


def existing_tags(runner: Runner, repo: str, prefix: str) -> set[str]:
    """Tag names starting with `prefix` that a release or a git tag in `repo` already uses.

    Lists every release (drafts included) and every git tag with the prefix. Any `gh` error
    raises, so a publish never guesses that a tag is free.
    """
    releases = _gh_lines(
        runner,
        ["api", "--paginate", f"repos/{repo}/releases", "--jq", ".[].tag_name"],
        f"releases of {repo}",
    )
    refs = _gh_lines(
        runner,
        ["api", "--paginate", f"repos/{repo}/git/matching-refs/tags/{prefix}", "--jq", ".[].ref"],
        f"tags of {repo}",
    )
    tags = set(releases) | {ref.removeprefix("refs/tags/") for ref in refs}
    return {tag for tag in tags if tag.startswith(prefix)}


def default_archive(repo_root: Path, map_id: str, moment: datetime) -> Path:
    return paths.snapshots_dir(repo_root) / snapshot_asset(map_id, moment)


def _archive_notice(config: MapConfig, archive: Path) -> str:
    """The NOTICE.md of an archive made by `create` for this map (checked lightly)."""
    try:
        with zipfile.ZipFile(archive) as opened:
            names = set(opened.namelist())
            if not set(METADATA_FILES) <= names:
                raise SnapshotError(f"{archive} is not a snapshot (no {', '.join(METADATA_FILES)})")
            document = json.loads(_read_member(opened, manifest.MANIFEST_NAME))
            notice = _read_member(opened, NOTICE_NAME).decode("utf-8")
    except (zipfile.BadZipFile, ValueError) as exc:
        raise SnapshotError(f"{archive} is not a valid snapshot: {exc}") from None
    if not isinstance(document, dict) or document.get("map_id") != config.id:
        raise SnapshotError(f"{archive} is not a snapshot of map {config.id}")
    return notice


def publish_snapshot(
    config: MapConfig,
    *,
    repo_root: Path,
    map_yaml: Path,
    archive: Path | None = None,
    repo: str | None = None,
    runner: Runner = run_command,
    now: Callable[[], datetime] = utc_now,
    log: Callable[[str], None] = print,
) -> PublishResult:
    """Upload a snapshot as the single asset of a new data release, then pin it in map.yaml.

    The tag's date is the archive's creation date (from its name). The tag is the first
    free one of data-<id>-<date>, data-<id>-<date>.2, .3, ... among the repository's
    releases and git tags, and the asset is named after it (mapdata-<id>-<date>[.<n>].zip,
    the archive's bytes unchanged). An existing release is never changed. Fails before
    uploading if `gh` is missing or not logged in, or the existing tags can't be listed.
    """
    path = archive or default_archive(repo_root, config.id, now())
    if not path.is_file():
        raise SnapshotError(f"{path} not found; run `mapgen snapshot create {config.id}` first")
    match = re.fullmatch(rf"mapdata-{re.escape(config.id)}-{_ASSET_DATE}\.zip", path.name)
    if not match:
        raise SnapshotError(f"{path.name} is not named mapdata-{config.id}-YYYY-MM-DD.zip")
    date_text = match.group(1)
    notice = _archive_notice(config, path)
    checksum = sha256_of(path)

    if _gh(runner, ["--version"]).returncode != 0:
        raise SnapshotError("gh (GitHub CLI) does not run; check its installation")
    if _gh(runner, ["auth", "status"]).returncode != 0:
        raise SnapshotError("gh is not logged in; run `gh auth login` first")
    if repo is None and config.snapshot is not None:
        repo = config.snapshot.repo
    if repo is None:
        viewed = _gh(runner, ["repo", "view", "--json", "nameWithOwner"])
        try:
            repo = str(json.loads(viewed.stdout)["nameWithOwner"]) if viewed.returncode == 0 else ""
        except (ValueError, KeyError, TypeError):
            repo = ""
    if not repo or not re.fullmatch(REPO_PATTERN, repo):
        raise SnapshotError(f"no valid repository ({repo!r}); pass --repo OWNER/NAME")

    taken = existing_tags(runner, repo, f"data-{config.id}-{date_text}")
    tag = next_tag(config.id, date_text, taken)
    asset = asset_for_tag(config.id, tag)
    if taken:
        log(f"existing data tags of {date_text}: {', '.join(sorted(taken))}; using {tag}")

    block = {"repo": repo, "release_tag": tag, "asset": asset, "sha256": checksum}
    text = map_yaml.read_text(encoding="utf-8")
    new_text = set_snapshot_block(text, block)
    MapConfig.model_validate(
        {**config.model_dump(), "snapshot": SnapshotConfig.model_validate(block).model_dump()}
    )

    if _gh(runner, ["release", "view", tag, "--repo", repo]).returncode == 0:
        raise SnapshotError(f"release {tag} already exists in {repo}; nothing was uploaded")
    leak_check(path, repo_root, runner)
    with tempfile.TemporaryDirectory(prefix="mapgen-publish-") as tmp:
        upload = path
        if asset != path.name:  # gh names the asset after the file: a copy, same bytes
            upload = Path(tmp) / asset
            shutil.copyfile(path, upload)
            if sha256_of(upload) != checksum:
                raise SnapshotError(f"the copy {upload} differs from {path}; nothing was uploaded")
        _create_release(config, runner, repo, tag, upload, asset, checksum, notice, log)
        try:
            _check_release_asset(runner, repo, tag, asset, upload.stat().st_size)
            map_yaml.write_text(new_text, encoding="utf-8")
        except (SnapshotError, OSError) as exc:
            raise SnapshotError(
                f"release {tag} was created in {repo} and left as is, but {exc}; "
                f"{map_yaml} was not updated. Check the release and decide whether to keep "
                "it (and pin it in map.yaml) or delete it"
            ) from exc
    return PublishResult(repo, tag, asset, checksum, map_yaml)


def _create_release(
    config: MapConfig,
    runner: Runner,
    repo: str,
    tag: str,
    path: Path,
    asset: str,
    checksum: str,
    notice: str,
    log: Callable[[str], None],
) -> None:
    """`gh release create` for a new tag (raises if gh fails)."""
    licences = ", ".join(f"{name}: {url}" for name, url in LICENCE_URLS.items())
    notes = (
        f"Raw map data snapshot of `{config.id}` for release builds "
        f"(`mapgen snapshot fetch {config.id}`). Not a game release.\n\n"
        f"SHA-256 of {asset}: `{checksum}`\n\n{notice}\nLicence texts: {licences}\n"
    )
    log(f"creating release {tag} in {repo}")
    created = _gh(
        runner,
        [
            "release",
            "create",
            tag,
            str(path),
            "--repo",
            repo,
            "--latest=false",
            "--title",
            f"Map data {config.id} {tag.removeprefix(f'data-{config.id}-')}",
            "--notes",
            notes,
        ],
    )
    if created.returncode != 0:
        raise SnapshotError(f"gh release create failed: {(created.stderr or '').strip()}")


def _check_release_asset(runner: Runner, repo: str, tag: str, asset: str, size: int) -> None:
    """Raise unless `asset` is listed on release `tag` (with `size` bytes, if gh gives one)."""
    viewed = _gh(runner, ["release", "view", tag, "--repo", repo, "--json", "assets"])
    try:
        assets = json.loads(viewed.stdout)["assets"] if viewed.returncode == 0 else []
        uploaded = {a["name"]: a.get("size") for a in assets}
    except (ValueError, KeyError, TypeError):
        uploaded = {}
    if asset not in uploaded or uploaded[asset] not in (None, size):
        raise SnapshotError(f"{asset} isn't listed as its asset with {size} bytes")
