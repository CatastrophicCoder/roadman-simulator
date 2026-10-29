"""Map data snapshots: `snapshot create`, `publish`, `fetch` and the map.yaml block writer.

No test touches the network or starts `gh`: HTTP goes to FakeHttp, and `publish` runs
only against FakeGh, a stub runner that answers like `gh` and records every command.
The leak check runs the real `.github/scripts/check-no-secret.sh` only where a test says
so; elsewhere a stub runner passes it.
"""

import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from collections.abc import Sequence
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pytest
import yaml

from conftest import FIXED_NOW, FIXTURE_YAML, REPO_ROOT, Call, FakeHttp, TmpRepo, http_error
from mapgen import snapshot as snap
from mapgen.cli import EXIT_FAILED, EXIT_OK, EXIT_USAGE, main
from mapgen.config import MapConfig, load_map_config
from mapgen.manifest import MANIFEST_NAME, load_entries
from mapgen.snapshot import (
    SnapshotError,
    create_snapshot,
    fetch_snapshot,
    set_snapshot_block,
)
from mapgen.sources.base import sha256_of
from mapgen.sources.osm import METADATA_ATTRIBUTES

SOMERO_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"
LEAK_SCRIPT = REPO_ROOT / ".github" / "scripts" / "check-no-secret.sh"
DATE = "2026-10-27"  # FIXED_NOW
TAG = f"data-testtown-{DATE}"
ASSET = f"mapdata-testtown-{DATE}.zip"
OSM_TEXT = """<?xml version="1.0" encoding="UTF-8"?>
<osm version="0.6" generator="Overpass API">
<note>The data included in this document is from www.openstreetmap.org.</note>
<meta osm_base="2026-10-27T14:32:56Z"/>
  <node id="1" lat="60.63" lon="23.52" version="3" timestamp="2020-01-01T00:00:00Z" \
changeset="11" uid="22" user="someone"/>
  <way id="5" version="2" timestamp="2021-01-01T00:00:00Z" changeset="12" uid="23" user="x">
    <nd ref="1"/>
    <tag k="building" v="yes"/>
  </way>
</osm>
"""
DEM_ATTRIBUTION = (
    "Contains data from the National Land Survey of Finland Elevation model 2 m, 10/2026"
)
ORTHO_ATTRIBUTION = "Contains data from the National Land Survey of Finland Orthophoto, 10/2026"
SNAPSHOT_FILES = {"osm/testtown.osm", "mml/dem_2m/a.tif", "mml/dem_2m/b.tif"}


def _ok(args: Sequence[str]) -> "subprocess.CompletedProcess[str]":
    return subprocess.CompletedProcess(list(args), 0, "", "")


def _data() -> dict[str, Any]:
    """The testtown config with the orthophoto marked debug_only."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["sources"]["mml_orthophoto"]["debug_only"] = True
    return data


def _entry(raw: Path, rel: str, source: str, licence: str, attribution: str) -> dict[str, Any]:
    path = raw / rel
    return {
        "path": rel,
        "source": source,
        "dataset": {"osm": "OpenStreetMap"}.get(source, "Elevation model 2 m"),
        "request": {"service": "https://example.invalid/?api-key=***"},
        "retrieved_at": "2026-10-20T08:00:00Z",
        "size": path.stat().st_size,
        "sha256": sha256_of(path),
        "licence": licence,
        "attribution": attribution,
        "origin": "downloaded",
    }


def _write_manifest(raw: Path, entries: list[dict[str, Any]]) -> None:
    document = {"schema_version": 1, "map_id": "testtown", "files": entries}
    (raw / MANIFEST_NAME).write_text(json.dumps(document, indent=2), encoding="utf-8")


def make_raw(raw: Path) -> Path:
    """A fake raw folder: OSM with metadata, two DEM files, a debug-only orthophoto, and
    files no manifest lists."""
    files = {
        "osm/testtown.osm": OSM_TEXT.encode("utf-8"),
        "mml/dem_2m/a.tif": b"dem-a" * 100,
        "mml/dem_2m/b.tif": b"dem-b" * 100,
        "mml/orthophoto/o.jp2": b"ortho",
        "mml/dem_2m/a.tif.ovr": b"overview",  # extra file in a source folder
        "map-box.gpkg": b"qgis",
    }
    for rel, content in files.items():
        (raw / rel).parent.mkdir(parents=True, exist_ok=True)
        (raw / rel).write_bytes(content)
    _write_manifest(
        raw,
        [
            _entry(raw, "mml/dem_2m/a.tif", "mml_dem_2m", "CC BY 4.0", DEM_ATTRIBUTION),
            _entry(raw, "mml/dem_2m/b.tif", "mml_dem_2m", "CC BY 4.0", DEM_ATTRIBUTION),
            _entry(raw, "mml/orthophoto/o.jp2", "mml_orthophoto", "CC BY 4.0", ORTHO_ATTRIBUTION),
            _entry(raw, "osm/testtown.osm", "osm", "ODbL 1.0", "© OpenStreetMap contributors"),
        ],
    )
    return raw


@pytest.fixture
def repo(tmp_repo: TmpRepo) -> TmpRepo:
    """A tmp repo with the testtown map, a fake raw folder and the real leak check script."""
    tmp_repo.write_map(_data())
    make_raw(tmp_repo.raw_dir("testtown"))
    script = tmp_repo.root / ".github" / "scripts" / LEAK_SCRIPT.name
    script.parent.mkdir(parents=True)
    shutil.copyfile(LEAK_SCRIPT, script)
    return tmp_repo


def _config(repo: TmpRepo) -> MapConfig:
    return load_map_config(repo.root / "maps" / "testtown" / "map.yaml")


def _create(repo: TmpRepo, out: Path | None = None, **kwargs: Any) -> snap.SnapshotArchive:
    kwargs.setdefault("now", lambda: FIXED_NOW)
    return create_snapshot(
        _config(repo),
        repo_root=repo.root,
        raw_dir=repo.raw_dir("testtown"),
        out_dir=out or repo.root / "out",
        runner=_ok,
        **kwargs,
    )


def _members(archive: Path) -> dict[str, bytes]:
    with zipfile.ZipFile(archive) as opened:
        return {name: opened.read(name) for name in opened.namelist()}


def _tree(folder: Path) -> dict[str, bytes]:
    if not folder.exists():
        return {}
    return {
        p.relative_to(folder).as_posix(): p.read_bytes()
        for p in sorted(folder.rglob("*"))
        if p.is_file()
    }


def _cli(repo: TmpRepo, *args: str) -> int:
    return main(["--repo-root", str(repo.root), "snapshot", *args])


# create


def test_create_holds_exactly_the_non_debug_source_files_and_metadata(repo: TmpRepo) -> None:
    result = _create(repo)
    assert result.path == repo.root / "out" / ASSET
    assert (result.tag, result.asset) == (TAG, ASSET)
    names = set(_members(result.path))
    assert names == SNAPSHOT_FILES | {MANIFEST_NAME, "SHA256SUMS", "NOTICE.md"}
    assert [name for name, _ in result.members] == sorted(names)
    document = json.loads(_members(result.path)[MANIFEST_NAME])
    assert document["schema_version"] == 1 and document["map_id"] == "testtown"
    assert {e["path"] for e in document["files"]} == SNAPSHOT_FILES
    assert set(document) == {"schema_version", "map_id", "generated_at", "tool", "files"}


def test_create_via_cli_prints_the_archive_and_runs_the_leak_check(
    repo: TmpRepo, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("MML_API_KEY", raising=False)
    assert _cli(repo, "create", "testtown") == EXIT_OK
    out = capsys.readouterr().out
    archives = list((repo.root / "tools" / "mapgen" / "build" / "snapshots").glob("*.zip"))
    assert len(archives) == 1
    assert f"archive: {archives[0]}" in out
    assert f"sha256: {sha256_of(archives[0])}" in out
    assert "mml/dem_2m/a.tif" in out and "SHA256SUMS" in out


def test_sums_match_and_creates_are_reproducible(repo: TmpRepo) -> None:
    first = _create(repo, repo.root / "one")
    later = datetime(2026, 10, 27, 23, 59, tzinfo=UTC)  # same UTC day, other time
    second = _create(repo, repo.root / "two", now=lambda: later)
    assert first.path.read_bytes() == second.path.read_bytes()
    assert first.sha256 == second.sha256

    members = _members(first.path)
    listed = {}
    for line in members["SHA256SUMS"].decode().splitlines():
        checksum, name = line.split("  ", 1)
        listed[name] = checksum
    assert set(listed) == set(members) - {"SHA256SUMS"}
    for name, checksum in listed.items():
        assert snap._sha256_bytes(members[name]) == checksum
    with zipfile.ZipFile(first.path) as opened:
        for info in opened.infolist():
            assert info.date_time == snap.ZIP_DATE_TIME
            assert info.compress_type == zipfile.ZIP_DEFLATED


def test_notice_has_the_attributions_licences_and_changes(repo: TmpRepo) -> None:
    notice = _members(_create(repo).path)["NOTICE.md"].decode()
    assert "© OpenStreetMap contributors" in notice
    assert "https://opendatacommons.org/licenses/odbl/1-0/" in notice
    assert "derivative database of the OpenStreetMap database" in notice
    assert "offered under the ODbL 1.0" in notice
    assert DEM_ATTRIBUTION in notice
    assert "https://creativecommons.org/licenses/by/4.0/" in notice
    assert "retrieved 2026-10-20" in notice
    assert "contributor metadata removed" in notice
    assert "as downloaded and identified by their `sha256`" in notice
    assert ORTHO_ATTRIBUTION not in notice


def test_osm_metadata_is_stripped_in_the_archive_only(repo: TmpRepo) -> None:
    local = repo.raw_dir("testtown") / "osm" / "testtown.osm"
    local_sha = sha256_of(local)
    members = _members(_create(repo).path)
    root = ET.fromstring(members["osm/testtown.osm"])
    assert root.attrib["version"] == "0.6"  # the API version on the root stays
    assert root.find("note") is not None and root.find("meta") is not None
    elements = [e for e in root.iter() if e is not root]
    assert {"node", "way", "nd", "tag"} <= {e.tag for e in elements}
    for element in elements:
        assert not set(METADATA_ATTRIBUTES) & set(element.attrib)

    (entry,) = [
        e for e in json.loads(members[MANIFEST_NAME])["files"] if e["path"] == "osm/testtown.osm"
    ]
    assert entry["source_sha256"] == local_sha
    assert entry["source_size"] == local.stat().st_size
    assert entry["sha256"] == snap._sha256_bytes(members["osm/testtown.osm"])
    assert entry["size"] == len(members["osm/testtown.osm"])
    assert entry["modifications"] == "contributor metadata removed"
    assert sha256_of(local) == local_sha  # the local file is unchanged


def test_an_earlier_source_sha256_is_kept(repo: TmpRepo) -> None:
    raw = repo.raw_dir("testtown")
    document = json.loads((raw / MANIFEST_NAME).read_text())
    for entry in document["files"]:
        if entry["source"] == "osm":
            entry.update(source_sha256="ab" * 32, source_size=12345)
    (raw / MANIFEST_NAME).write_text(json.dumps(document))
    members = _members(_create(repo).path)
    (entry,) = [e for e in json.loads(members[MANIFEST_NAME])["files"] if e["source"] == "osm"]
    assert (entry["source_sha256"], entry["source_size"]) == ("ab" * 32, 12345)


def _edit_manifest(raw: Path, edit: Any) -> None:
    document = json.loads((raw / MANIFEST_NAME).read_text())
    document["files"] = edit(document["files"])
    (raw / MANIFEST_NAME).write_text(json.dumps(document))


@pytest.mark.parametrize(
    ("case", "message"),
    [
        ("no_entries", "source osm has no files"),
        ("missing_file", "source mml_dem_2m: mml/dem_2m/b.tif is in the manifest but missing"),
        ("checksum", "source mml_dem_2m: mml/dem_2m/a.tif differs from its manifest checksum"),
    ],
)
def test_create_fails_naming_the_source_and_file(
    repo: TmpRepo, capsys: pytest.CaptureFixture[str], case: str, message: str
) -> None:
    raw = repo.raw_dir("testtown")
    if case == "no_entries":
        _edit_manifest(raw, lambda files: [e for e in files if e["source"] != "osm"])
    elif case == "missing_file":
        (raw / "mml" / "dem_2m" / "b.tif").unlink()
    else:
        (raw / "mml" / "dem_2m" / "a.tif").write_bytes(b"changed")
    assert _cli(repo, "create", "testtown", "--out", str(repo.root / "out")) == EXIT_FAILED
    assert message in capsys.readouterr().err
    assert not (repo.root / "out" / ASSET).exists()


def test_create_fails_for_a_licence_without_a_url(repo: TmpRepo) -> None:
    def relicence(files: list[dict[str, Any]]) -> list[dict[str, Any]]:
        for entry in files:
            if entry["source"] == "mml_dem_2m":
                entry["licence"] = "Some Licence 9"
        return files

    _edit_manifest(repo.raw_dir("testtown"), relicence)
    with pytest.raises(SnapshotError, match="no URL known for licence 'Some Licence 9'"):
        _create(repo)


def test_create_with_a_planted_key_fails_and_leaves_no_archive(
    repo: TmpRepo, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    """Runs the real check-no-secret.sh on the archive."""
    monkeypatch.delenv("MML_API_KEY", raising=False)
    raw = repo.raw_dir("testtown")
    (raw / "mml" / "dem_2m" / "a.tif").write_bytes(b"https://x.invalid/?api-key=abc")
    _edit_manifest(
        raw,
        lambda files: [
            {**e, "sha256": sha256_of(raw / e["path"]), "size": (raw / e["path"]).stat().st_size}
            for e in files
        ],
    )
    out = repo.root / "out"
    assert _cli(repo, "create", "testtown", "--out", str(out)) == EXIT_FAILED
    err = capsys.readouterr().err
    assert "leak check failed" in err and "abc" not in err
    assert list(out.iterdir()) == []


# fetch


@pytest.fixture
def archive(repo: TmpRepo) -> snap.SnapshotArchive:
    """A snapshot of the fake raw folder (made in its own folder, outside raw/)."""
    return _create(repo, repo.root / "made")


def _pin(repo: TmpRepo, sha256: str, asset: str = ASSET) -> MapConfig:
    data = _data()
    data["snapshot"] = {
        "repo": "owner/name",
        "release_tag": TAG,
        "asset": asset,
        "sha256": sha256,
    }
    return repo.load(data)


def _serving(content: bytes) -> FakeHttp:
    return FakeHttp(lambda call: content)


def _fetch(repo: TmpRepo, config: MapConfig, http: FakeHttp, **kwargs: Any) -> snap.FetchReport:
    return fetch_snapshot(
        config,
        raw_dir=repo.raw_dir("testtown"),
        http=http,
        now=lambda: FIXED_NOW,
        log=lambda _: None,
        **kwargs,
    )


def _empty_raw(repo: TmpRepo) -> Path:
    raw = repo.raw_dir("testtown")
    shutil.rmtree(raw)
    return raw


def test_fetch_into_an_empty_raw_folder(repo: TmpRepo, archive: snap.SnapshotArchive) -> None:
    raw = _empty_raw(repo)
    config = _pin(repo, archive.sha256)
    http = _serving(archive.path.read_bytes())
    report = _fetch(repo, config, http)

    assert [c.url for c in http.calls] == [
        f"https://github.com/owner/name/releases/download/{TAG}/{ASSET}"
    ]
    assert http.calls[0].method == "DOWNLOAD" and http.calls[0].body is None
    members = _members(archive.path)
    for rel in SNAPSHOT_FILES:
        assert (raw / rel).read_bytes() == members[rel]
    assert sorted(report.added) == sorted(SNAPSHOT_FILES)
    assert (raw / "snapshot" / "NOTICE.md").read_bytes() == members["NOTICE.md"]
    assert (raw / "snapshot" / "SHA256SUMS").read_bytes() == members["SHA256SUMS"]
    assert not (raw / "NOTICE.md").exists() and not (raw / "SHA256SUMS").exists()
    entries = load_entries(raw)
    assert set(entries) == SNAPSHOT_FILES
    bundled = {e["path"]: e for e in json.loads(members[MANIFEST_NAME])["files"]}
    assert entries == bundled
    # A second fetch finds identical files and changes nothing.
    again = _fetch(repo, config, http)
    assert sorted(again.unchanged) == sorted(SNAPSHOT_FILES) and not again.replaced


def test_fetch_with_a_mismatching_checksum_changes_nothing(
    repo: TmpRepo,
    archive: snap.SnapshotArchive,
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    pinned = "f" * 64
    _pin(repo, pinned)
    monkeypatch.setattr(snap, "UrllibClient", lambda: _serving(archive.path.read_bytes()))
    before = _tree(repo.raw_dir("testtown"))
    assert _cli(repo, "fetch", "testtown", "--force") == EXIT_FAILED
    err = capsys.readouterr().err
    assert pinned in err and archive.sha256 in err
    assert _tree(repo.raw_dir("testtown")) == before


def test_fetch_without_a_snapshot_block_is_a_usage_error(
    repo: TmpRepo, capsys: pytest.CaptureFixture[str]
) -> None:
    assert _cli(repo, "fetch", "testtown") == EXIT_USAGE
    err = capsys.readouterr().err
    assert "map.yaml has no snapshot block" in err
    assert "`mapgen snapshot publish`" in err and "`mapgen download`" in err


def test_fetch_reports_a_missing_asset(
    repo: TmpRepo, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    _pin(repo, "0" * 64)

    def missing(call: Call) -> Any:
        raise http_error(404, call.url)

    monkeypatch.setattr(snap, "UrllibClient", lambda: FakeHttp(missing))
    assert _cli(repo, "fetch", "testtown") == EXIT_FAILED
    assert f"snapshot asset {ASSET} not found on release {TAG}" in capsys.readouterr().err


def test_fetch_passes_without_mml_api_key(
    repo: TmpRepo, archive: snap.SnapshotArchive, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("MML_API_KEY", raising=False)
    _empty_raw(repo)
    _pin(repo, archive.sha256)
    monkeypatch.setattr(snap, "UrllibClient", lambda: _serving(archive.path.read_bytes()))
    assert _cli(repo, "fetch", "testtown") == EXIT_OK
    assert set(load_entries(repo.raw_dir("testtown"))) == SNAPSHOT_FILES


def _hostile_zip(dest: Path, case: str) -> bytes:
    """A zip that lists its members in SHA256SUMS, plus one hostile change."""
    good = {
        "osm/testtown.osm": b"<osm/>",
        MANIFEST_NAME: b"{}",
        "NOTICE.md": b"notice",
    }
    hostile = {
        "dotdot": ("../evil.txt", b"evil", True),
        "absolute": ("/tmp/evil.txt", b"evil", True),
        "unlisted": ("mml/dem_2m/extra.tif", b"evil", False),
        "symlink": ("osm/link", b"/etc/passwd", True),
    }
    name, content, listed = hostile[case]
    listed_files = dict(good)
    if listed:
        listed_files[name] = content
    sums = "".join(
        f"{snap._sha256_bytes(data)}  {path}\n" for path, data in sorted(listed_files.items())
    )
    with zipfile.ZipFile(dest, "w") as out:
        for path, data in good.items():
            out.writestr(path, data)
        out.writestr("SHA256SUMS", sums)
        info = zipfile.ZipInfo(name)
        if case == "symlink":
            info.external_attr = 0o120777 << 16
        out.writestr(info, content)
    return dest.read_bytes()


@pytest.mark.parametrize("case", ["dotdot", "absolute", "unlisted", "symlink"])
def test_fetch_refuses_a_hostile_archive(repo: TmpRepo, tmp_path: Path, case: str) -> None:
    raw = _empty_raw(repo)
    content = _hostile_zip(tmp_path / "hostile.zip", case)
    config = _pin(repo, snap._sha256_bytes(content))
    with pytest.raises(SnapshotError):
        _fetch(repo, config, _serving(content))
    assert not raw.exists()
    assert not (tmp_path / "evil.txt").exists()


def test_fetch_protects_differing_and_extra_local_files_unless_forced(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    raw = repo.raw_dir("testtown")
    (raw / "mml" / "dem_2m" / "a.tif").write_bytes(b"locally changed")
    (raw / "mml" / "dem_2m" / "extra.tif").write_bytes(b"extra")
    config = _pin(repo, archive.sha256)
    http = _serving(archive.path.read_bytes())
    before = _tree(raw)

    with pytest.raises(SnapshotError) as failed:
        _fetch(repo, config, http)
    message = str(failed.value)
    assert "differs from the snapshot: mml/dem_2m/a.tif" in message
    assert "differs from the snapshot: osm/testtown.osm" in message  # local still has metadata
    assert "not in the snapshot: mml/dem_2m/extra.tif" in message
    assert "not in the snapshot: mml/dem_2m/a.tif.ovr" in message
    assert "b.tif" not in message  # identical
    assert "orthophoto" not in message and "map-box" not in message
    assert _tree(raw) == before

    report = _fetch(repo, config, http, force=True)
    members = _members(archive.path)
    assert (raw / "mml" / "dem_2m" / "a.tif").read_bytes() == members["mml/dem_2m/a.tif"]
    assert (raw / "osm" / "testtown.osm").read_bytes() == members["osm/testtown.osm"]
    assert not (raw / "mml" / "dem_2m" / "extra.tif").exists()
    assert not (raw / "mml" / "dem_2m" / "a.tif.ovr").exists()
    assert sorted(report.deleted) == ["mml/dem_2m/a.tif.ovr", "mml/dem_2m/extra.tif"]
    assert report.unchanged == ["mml/dem_2m/b.tif"]
    assert (raw / "mml" / "orthophoto" / "o.jp2").read_bytes() == b"ortho"
    assert (raw / "map-box.gpkg").read_bytes() == b"qgis"
    entries = load_entries(raw)
    assert set(entries) == SNAPSHOT_FILES | {"mml/orthophoto/o.jp2"}
    assert entries["mml/orthophoto/o.jp2"]["attribution"] == ORTHO_ATTRIBUTION


# The committed Somero fixture: snapshot, fetch, release build


def test_fixture_snapshot_builds_the_same_credits(tmp_path: Path) -> None:
    def repo_with(name: str) -> Path:
        root = tmp_path / name
        shutil.copytree(REPO_ROOT / "maps" / "somero", root / "maps" / "somero")
        (root / "tools" / "mapgen").mkdir(parents=True)
        return root

    direct = repo_with("direct")
    raw = direct / "tools" / "mapgen" / "data" / "raw" / "somero"
    shutil.copytree(SOMERO_FIXTURE, raw)
    direct_config = load_map_config(direct / "maps" / "somero" / "map.yaml")
    made = create_snapshot(
        direct_config,
        repo_root=REPO_ROOT,
        raw_dir=raw,
        out_dir=tmp_path / "made",
        runner=_ok,
        now=lambda: FIXED_NOW,
    )
    assert not any("orthophoto" in name for name, _ in made.members)

    fetched = repo_with("fetched")
    map_yaml = fetched / "maps" / "somero" / "map.yaml"
    block = {
        "repo": "owner/name",
        "release_tag": made.tag,
        "asset": made.asset,
        "sha256": made.sha256,
    }
    map_yaml.write_text(set_snapshot_block(map_yaml.read_text(encoding="utf-8"), block))
    fetched_raw = fetched / "tools" / "mapgen" / "data" / "raw" / "somero"
    fetch_snapshot(
        load_map_config(map_yaml),
        raw_dir=fetched_raw,
        http=_serving(made.path.read_bytes()),
        log=lambda _: None,
    )
    assert not (fetched_raw / "mml" / "orthophoto").exists()

    def credits(root: Path) -> list[dict[str, Any]]:
        args = ["--repo-root", str(root), "build", "somero", "--area", "test", "--release"]
        assert main(args) == EXIT_OK
        pack = root / "game" / "maps" / "somero" / "manifest.json"
        return json.loads(pack.read_text(encoding="utf-8"))["credits"]

    from_fixture = credits(direct)
    assert from_fixture and credits(fetched) == from_fixture


# The map.yaml block writer (pure, on text)

BLOCK = {
    "repo": "owner/name",
    "release_tag": "data-testtown-2026-10-29",
    "asset": "mapdata-testtown-2026-10-29.zip",
    "sha256": "1234567890" * 6 + "1234",  # digits only: must stay a string
}
YAML_TEXT = """# Top comment.
id: testtown  # inline comment

sources:
  osm:
    adapter: osm_overpass  # keep me
"""


def test_block_writer_appends_and_keeps_comments() -> None:
    text = set_snapshot_block(YAML_TEXT, BLOCK)
    assert text.startswith(YAML_TEXT.rstrip("\n"))
    assert "# Top comment." in text and "# keep me" in text and "# inline comment" in text
    assert yaml.safe_load(text)["snapshot"] == BLOCK
    assert isinstance(yaml.safe_load(text)["snapshot"]["sha256"], str)


def test_block_writer_replaces_only_the_block() -> None:
    old = YAML_TEXT.replace(
        "\nsources:",
        "\n# Snapshot comment stays.\nsnapshot:\n  repo: old/repo\n\n  release_tag: x\n"
        "  # a comment inside the block\n  asset: y.zip\n  sha256: z\n# After comment.\n"
        "\nsources:",
    )
    text = set_snapshot_block(old, BLOCK)
    assert "# Snapshot comment stays." in text and "# After comment." in text
    assert "old/repo" not in text and "a comment inside the block" not in text
    assert yaml.safe_load(text)["snapshot"] == BLOCK
    assert yaml.safe_load(text)["sources"] == yaml.safe_load(old)["sources"]
    before_lines = old.split("snapshot:")[0]
    assert text.startswith(before_lines)
    assert text.endswith(old.split("# After comment.")[1])
    assert set_snapshot_block(text, BLOCK) == text  # idempotent


# publish (FakeGh only: never the real gh)

REPO = "owner/name"


class FakeGh:
    """A stub `Runner` that answers `gh` commands from in-memory releases and git tags.

    `fail` names a listing ("releases" or "refs") that exits non-zero. `listed_size`, if
    given, is the asset size that `release view --json assets` reports after a create.
    Every command is recorded; `release create` records the uploaded file's name, size
    and SHA-256. The listing calls must have exactly the arguments `existing_tags` uses.
    """

    def __init__(
        self,
        releases: Sequence[str] = (),
        tags: Sequence[str] = (),
        fail: str = "",
        listed_size: int | None = None,
    ) -> None:
        self.releases = {tag: {} for tag in releases}  # tag -> {asset name: size}
        self.tags = set(tags) | set(releases)
        self.fail = fail
        self.listed_size = listed_size
        self.calls: list[list[str]] = []
        self.uploads: list[tuple[str, int, str]] = []

    def __call__(self, args: Sequence[str]) -> "subprocess.CompletedProcess[str]":
        args = list(args)
        self.calls.append(args)
        if args[0] == "bash":  # the leak check
            return _done(args)
        assert args[0] == "gh", args
        gh = args[1:]
        if gh in (["--version"], ["auth", "status"]):
            return _done(args)
        if gh[0] == "api":
            path = gh[2]
            if path == f"repos/{REPO}/releases":
                assert gh == ["api", "--paginate", path, "--jq", ".[].tag_name"], gh
                if self.fail == "releases":
                    return _done(args, 1, err="HTTP 502")
                return _done(args, out="".join(f"{t}\n" for t in sorted(self.releases)))
            prefix = path.removeprefix(f"repos/{REPO}/git/matching-refs/tags/")
            assert prefix != path, gh
            assert gh == ["api", "--paginate", path, "--jq", ".[].ref"], gh
            if self.fail == "refs":
                return _done(args, 1, err="HTTP 403")
            refs = sorted(f"refs/tags/{t}" for t in self.tags if t.startswith(prefix))
            return _done(args, out="".join(f"{r}\n" for r in refs))
        if gh[:2] == ["release", "view"]:
            tag = gh[2]
            if tag not in self.releases:
                return _done(args, 1, err="release not found")
            assets = [{"name": n, "size": z} for n, z in self.releases[tag].items()]
            return _done(args, out=json.dumps({"assets": assets}))
        if gh[:2] == ["release", "create"]:
            tag, file = gh[2], Path(gh[3])
            assert tag not in self.releases and tag not in self.tags, "would reuse a tag"
            size = file.stat().st_size if self.listed_size is None else self.listed_size
            self.releases[tag] = {file.name: size}
            self.tags.add(tag)
            self.uploads.append((file.name, file.stat().st_size, sha256_of(file)))
            return _done(args)
        raise AssertionError(f"unexpected gh command: {args}")


def _done(
    args: Sequence[str], code: int = 0, out: str = "", err: str = ""
) -> "subprocess.CompletedProcess[str]":
    return subprocess.CompletedProcess(list(args), code, out, err)


def _publish(repo: TmpRepo, gh: FakeGh, archive: snap.SnapshotArchive) -> snap.PublishResult:
    config = _config(repo)
    return snap.publish_snapshot(
        config,
        repo_root=repo.root,
        map_yaml=repo.root / "maps" / "testtown" / "map.yaml",
        archive=archive.path,
        repo=REPO,
        runner=gh,
        now=lambda: FIXED_NOW,
        log=lambda _msg: None,
    )


@pytest.mark.parametrize(
    ("existing", "expected"),
    [
        ((), TAG),
        ((TAG,), f"{TAG}.2"),
        ((TAG, f"{TAG}.2"), f"{TAG}.3"),
        (
            (
                TAG,
                f"{TAG}.2",
                f"{TAG}.3",
                f"{TAG}.4",
                f"{TAG}.5",
                f"{TAG}.6",
                f"{TAG}.7",
                f"{TAG}.8",
                f"{TAG}.9",
            ),
            f"{TAG}.10",
        ),
        # other dates and maps don't count
        (("data-testtown-2026-10-26", "data-othertown-2026-10-27"), TAG),
        # a gap is never filled: the next number follows the highest one
        ((TAG, f"{TAG}.3"), f"{TAG}.4"),
        # nor is the plain tag reused once a number of that date exists
        ((f"{TAG}.2",), f"{TAG}.3"),
    ],
)
def test_publish_picks_the_next_tag_and_names_the_asset_after_it(
    repo: TmpRepo, archive: snap.SnapshotArchive, existing: tuple[str, ...], expected: str
) -> None:
    gh = FakeGh(releases=existing)
    before = {tag: dict(assets) for tag, assets in gh.releases.items()}
    result = _publish(repo, gh, archive)

    asset = f"mapdata-{expected.removeprefix('data-')}.zip"
    assert (result.tag, result.asset, result.sha256) == (expected, asset, archive.sha256)
    assert gh.uploads == [(asset, archive.size, archive.sha256)]  # the archive's bytes
    assert archive.path.name == ASSET and archive.path.is_file()  # create's name unchanged
    assert {t: a for t, a in gh.releases.items() if t != expected} == before
    pinned = _config(repo).snapshot
    assert pinned is not None
    assert (pinned.repo, pinned.release_tag, pinned.asset, pinned.sha256) == (
        REPO,
        expected,
        asset,
        archive.sha256,
    )


def test_a_git_tag_without_a_release_is_not_reused(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    gh = FakeGh(releases=[TAG], tags=[f"{TAG}.2"])
    assert _publish(repo, gh, archive).tag == f"{TAG}.3"


def test_publish_never_changes_an_existing_release(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    gh = FakeGh(releases=[TAG, f"{TAG}.2"])
    _publish(repo, gh, archive)
    mutating = {"delete", "edit", "upload", "delete-asset"}
    for call in gh.calls:
        if call[0] != "gh":
            continue
        assert not mutating & set(call), call
        if call[1] == "api":
            assert "--method" not in call and "-X" not in call, call
    created = [c for c in gh.calls if c[1:3] == ["release", "create"]]
    assert [c[3] for c in created] == [f"{TAG}.3"]


@pytest.mark.parametrize("fail", ["releases", "refs"])
def test_a_gh_listing_error_aborts_without_publishing(
    repo: TmpRepo, archive: snap.SnapshotArchive, fail: str
) -> None:
    map_yaml = repo.root / "maps" / "testtown" / "map.yaml"
    before = map_yaml.read_text(encoding="utf-8")
    gh = FakeGh(fail=fail)
    with pytest.raises(SnapshotError, match="could not list"):
        _publish(repo, gh, archive)
    assert not any(c[1:3] == ["release", "create"] for c in gh.calls if c[0] == "gh")
    assert not any(c[0] == "bash" for c in gh.calls)  # stopped before the leak check
    assert gh.uploads == [] and map_yaml.read_text(encoding="utf-8") == before


def test_an_existing_release_found_by_the_pre_check_aborts(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    """The listings miss the tag (e.g. a release made meanwhile); `release view` finds it."""
    map_yaml = repo.root / "maps" / "testtown" / "map.yaml"
    before = map_yaml.read_text(encoding="utf-8")
    gh = FakeGh()
    gh.releases[TAG] = {ASSET: 1}  # not in the listings' answers below
    real = gh.__call__

    def hide_from_listings(args: Sequence[str]) -> "subprocess.CompletedProcess[str]":
        if list(args[1:2]) == ["api"]:
            gh.calls.append(list(args))
            return _done(args)
        return real(args)

    with pytest.raises(SnapshotError, match=f"release {TAG} already exists"):
        snap.publish_snapshot(
            _config(repo),
            repo_root=repo.root,
            map_yaml=map_yaml,
            archive=archive.path,
            repo=REPO,
            runner=hide_from_listings,
            now=lambda: FIXED_NOW,
            log=lambda _msg: None,
        )
    assert ["gh", "release", "view", TAG, "--repo", REPO] in gh.calls
    assert not any(c[1:3] == ["release", "create"] for c in gh.calls if c[0] == "gh")
    assert gh.uploads == [] and gh.releases == {TAG: {ASSET: 1}}
    assert map_yaml.read_text(encoding="utf-8") == before


def test_a_differing_copy_of_the_archive_aborts_before_uploading(
    repo: TmpRepo, archive: snap.SnapshotArchive, monkeypatch: pytest.MonkeyPatch
) -> None:
    map_yaml = repo.root / "maps" / "testtown" / "map.yaml"
    before = map_yaml.read_text(encoding="utf-8")
    real_copy = shutil.copyfile

    def corrupt_copy(src: Path, dst: Path) -> Path:
        real_copy(src, dst)
        with open(dst, "ab") as handle:
            handle.write(b"x")
        return dst

    monkeypatch.setattr(snap.shutil, "copyfile", corrupt_copy)
    gh = FakeGh(releases=[TAG])  # the .2 tag needs a renamed copy
    with pytest.raises(SnapshotError, match="differs from .*nothing was uploaded"):
        _publish(repo, gh, archive)
    assert not any(c[1:3] == ["release", "create"] for c in gh.calls if c[0] == "gh")
    assert gh.uploads == [] and map_yaml.read_text(encoding="utf-8") == before


def test_a_wrong_asset_after_create_names_the_kept_release(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    map_yaml = repo.root / "maps" / "testtown" / "map.yaml"
    before = map_yaml.read_text(encoding="utf-8")
    gh = FakeGh(listed_size=archive.size - 1)
    with pytest.raises(SnapshotError) as raised:
        _publish(repo, gh, archive)
    message = str(raised.value)
    assert f"release {TAG} was created in {REPO} and left as is" in message
    assert f"{ASSET} isn't listed" in message and "was not updated" in message
    assert TAG in gh.releases  # left as is: no delete or edit
    assert not any({"delete", "edit"} & set(c) for c in gh.calls)
    assert map_yaml.read_text(encoding="utf-8") == before


def test_a_map_yaml_write_error_after_create_names_the_kept_release(
    repo: TmpRepo, archive: snap.SnapshotArchive, monkeypatch: pytest.MonkeyPatch
) -> None:
    map_yaml = repo.root / "maps" / "testtown" / "map.yaml"
    before = map_yaml.read_text(encoding="utf-8")
    real_write = Path.write_text

    def failing_write(self: Path, *args: Any, **kwargs: Any) -> int:
        if self == map_yaml:
            raise PermissionError(13, "Permission denied", str(self))
        return real_write(self, *args, **kwargs)

    monkeypatch.setattr(Path, "write_text", failing_write)
    gh = FakeGh()
    with pytest.raises(SnapshotError) as raised:
        _publish(repo, gh, archive)
    message = str(raised.value)
    assert f"release {TAG} was created in {REPO} and left as is" in message
    assert "Permission denied" in message and "was not updated" in message
    assert gh.uploads == [(ASSET, archive.size, archive.sha256)]
    assert map_yaml.read_text(encoding="utf-8") == before


def test_next_tag_and_asset_names() -> None:
    assert snap.next_tag("x", DATE, []) == f"data-x-{DATE}"
    assert snap.next_tag("x", DATE, [f"data-x-{DATE}", f"data-x-{DATE}.3"]) == f"data-x-{DATE}.4"
    assert snap.next_tag("x", DATE, [f"data-x-{DATE}.10", f"data-x-{DATE}.9"]) == (
        f"data-x-{DATE}.11"
    )
    # `.1` and `.02` aren't valid tags but still count by their number
    assert snap.next_tag("x", DATE, [f"data-x-{DATE}.1"]) == f"data-x-{DATE}.2"
    assert snap.next_tag("x", DATE, [f"data-x-{DATE}", f"data-x-{DATE}.02"]) == f"data-x-{DATE}.3"
    # a prefix match from the tag listing isn't a tag of this date
    assert snap.next_tag("x", DATE, [f"data-x-{DATE}.2x"]) == f"data-x-{DATE}"
    assert snap.asset_for_tag("x", f"data-x-{DATE}.12") == f"mapdata-x-{DATE}.12.zip"
    for bad in (f"data-x-{DATE}.1", f"data-x-{DATE}.01", f"data-y-{DATE}"):
        with pytest.raises(SnapshotError):
            snap.asset_for_tag("x", bad)


def test_fetch_accepts_a_suffixed_tag(repo: TmpRepo, archive: snap.SnapshotArchive) -> None:
    data = _data()
    data["snapshot"] = {
        "repo": REPO,
        "release_tag": f"{TAG}.2",
        "asset": f"mapdata-testtown-{DATE}.2.zip",
        "sha256": archive.sha256,
    }
    config = repo.load(data)
    _empty_raw(repo)
    http = _serving(archive.path.read_bytes())
    _fetch(repo, config, http)
    assert http.calls[0].url.endswith(f"/releases/download/{TAG}.2/mapdata-testtown-{DATE}.2.zip")


# workflows


def test_release_workflow_fetches_snapshots_without_the_key() -> None:
    text = (REPO_ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
    assert "MML_API_KEY" not in text
    assert "mapgen download" not in text
    assert "mapgen snapshot fetch" in text
    assert "check-no-secret.sh dist" in text


def test_ci_workflow_ignores_data_tags_but_runs_on_branches() -> None:
    data = yaml.safe_load((REPO_ROOT / ".github" / "workflows" / "ci.yml").read_text())
    on = data.get("on", data.get(True))  # YAML 1.1 reads a bare `on` key as True
    assert on["push"]["branches"] == ["**"]
    assert on["push"]["tags-ignore"] == ["data-*"]


# Review fixes: stripping only when needed, leak check before replacing, strict fetch checks


def test_osm_without_metadata_is_archived_byte_for_byte(repo: TmpRepo) -> None:
    raw = repo.raw_dir("testtown")
    osm = raw / "osm" / "testtown.osm"
    osm.write_text(OSM_TEXT.replace(' version="3"', "").replace(' version="2"', ""))
    for name in ("timestamp", "changeset", "uid", "user"):
        osm.write_text(re.sub(rf' {name}="[^"]*"', "", osm.read_text()))
    assert snap.strip_metadata(osm, repo.root / "unused.osm") == 0
    assert not (repo.root / "unused.osm").exists()
    _edit_manifest(
        raw,
        lambda files: [
            {**e, "sha256": sha256_of(raw / e["path"]), "size": (raw / e["path"]).stat().st_size}
            for e in files
        ],
    )
    members = _members(_create(repo).path)
    assert members["osm/testtown.osm"] == osm.read_bytes()
    (entry,) = [e for e in json.loads(members[MANIFEST_NAME])["files"] if e["source"] == "osm"]
    assert not {"modifications", "source_sha256", "source_size"} & set(entry)
    assert "contributor metadata removed" not in members["NOTICE.md"].decode()


def test_strip_metadata_counts_the_removed_attributes(tmp_path: Path) -> None:
    source = tmp_path / "in.osm"
    source.write_text(OSM_TEXT)
    assert snap.strip_metadata(source, tmp_path / "out.osm") == 10  # 5 on the node, 5 on the way


def test_a_failed_leak_check_keeps_the_existing_archive(
    repo: TmpRepo, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Runs the real check-no-secret.sh on the second archive."""
    monkeypatch.delenv("MML_API_KEY", raising=False)
    out = repo.root / "out"
    good = _create(repo, out)
    good_bytes = good.path.read_bytes()
    raw = repo.raw_dir("testtown")
    (raw / "mml" / "dem_2m" / "a.tif").write_bytes(b"?api-key=abc")
    _edit_manifest(
        raw,
        lambda files: [
            {**e, "sha256": sha256_of(raw / e["path"]), "size": (raw / e["path"]).stat().st_size}
            for e in files
        ],
    )
    with pytest.raises(SnapshotError, match="leak check failed"):
        create_snapshot(
            _config(repo),
            repo_root=repo.root,
            raw_dir=raw,
            out_dir=out,
            now=lambda: FIXED_NOW,
        )
    assert sorted(p.name for p in out.iterdir()) == [ASSET]
    assert good.path.read_bytes() == good_bytes


def _pin_data(repo: TmpRepo, sha256: str, data: dict[str, Any]) -> MapConfig:
    data["snapshot"] = {"repo": "owner/name", "release_tag": TAG, "asset": ASSET, "sha256": sha256}
    return repo.load(data)


def test_fetch_fails_when_the_snapshot_lacks_an_enabled_source(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    raw = _empty_raw(repo)
    data = _data()
    data["sources"]["mml_orthophoto"]["debug_only"] = False  # now the snapshot misses it
    config = _pin_data(repo, archive.sha256, data)
    with pytest.raises(SnapshotError, match="no files of source.s. mml_orthophoto"):
        _fetch(repo, config, _serving(archive.path.read_bytes()))
    assert not raw.exists()


def test_fetch_refuses_files_of_sources_the_map_does_not_snapshot(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    raw = _empty_raw(repo)
    data = _data()
    data["sources"]["mml_dem_2m"]["enabled"] = False
    config = _pin_data(repo, archive.sha256, data)
    with pytest.raises(SnapshotError, match="belongs to source 'mml_dem_2m'"):
        _fetch(repo, config, _serving(archive.path.read_bytes()))
    assert not raw.exists()


def test_fetch_refuses_paths_the_local_manifest_gives_another_source(
    repo: TmpRepo, archive: snap.SnapshotArchive
) -> None:
    raw = repo.raw_dir("testtown")

    def reassign(files: list[dict[str, Any]]) -> list[dict[str, Any]]:
        for entry in files:
            if entry["path"] == "mml/dem_2m/a.tif":
                entry["source"] = "mml_orthophoto"
        return files

    _edit_manifest(raw, reassign)
    before = _tree(raw)
    config = _pin(repo, archive.sha256)
    with pytest.raises(SnapshotError, match="another source.*mml/dem_2m/a.tif"):
        _fetch(repo, config, _serving(archive.path.read_bytes()), force=True)
    assert _tree(raw) == before


def test_an_interrupted_forced_fetch_keeps_a_true_manifest_and_a_rerun_completes(
    repo: TmpRepo, archive: snap.SnapshotArchive, monkeypatch: pytest.MonkeyPatch
) -> None:
    raw = repo.raw_dir("testtown")
    (raw / "mml" / "dem_2m" / "a.tif").write_bytes(b"locally changed")
    old_osm = (raw / "osm" / "testtown.osm").read_bytes()
    config = _pin(repo, archive.sha256)
    http = _serving(archive.path.read_bytes())
    real_place = snap._place
    calls: list[Path] = []

    def failing_place(source: Path, dest: Path) -> None:
        calls.append(dest)
        if len(calls) == 2:
            raise OSError("disk full")
        real_place(source, dest)

    monkeypatch.setattr(snap, "_place", failing_place)
    with pytest.raises(OSError, match="disk full"):
        _fetch(repo, config, http, force=True)
    # The first differing file was replaced, the second not; no extra file was deleted.
    members = _members(archive.path)
    assert (raw / "mml" / "dem_2m" / "a.tif").read_bytes() == members["mml/dem_2m/a.tif"]
    assert (raw / "osm" / "testtown.osm").read_bytes() == old_osm
    assert (raw / "mml" / "dem_2m" / "a.tif.ovr").exists()
    entries = load_entries(raw)
    for rel, entry in entries.items():
        assert entry["sha256"] == sha256_of(raw / rel), rel
    assert "osm/testtown.osm" in entries and "mml/orthophoto/o.jp2" in entries

    monkeypatch.setattr(snap, "_place", real_place)
    _fetch(repo, config, http, force=True)
    assert (raw / "osm" / "testtown.osm").read_bytes() == members["osm/testtown.osm"]
    assert not (raw / "mml" / "dem_2m" / "a.tif.ovr").exists()
    assert set(load_entries(raw)) == SNAPSHOT_FILES | {"mml/orthophoto/o.jp2"}


def _zip_with_manifest(dest: Path, entries: list[Any], files: dict[str, bytes]) -> bytes:
    """A checksum-consistent zip of `files`, `entries` as its manifest, NOTICE and sums."""
    members = dict(files)
    members[MANIFEST_NAME] = json.dumps(
        {"schema_version": 1, "map_id": "testtown", "files": entries}
    ).encode()
    members["NOTICE.md"] = b"notice"
    sums = "".join(f"{snap._sha256_bytes(d)}  {n}\n" for n, d in sorted(members.items()))
    with zipfile.ZipFile(dest, "w") as out:
        for name, data in members.items():
            out.writestr(name, data)
        out.writestr("SHA256SUMS", sums)
    return dest.read_bytes()


def _good_entry(rel: str, data: bytes, source: str) -> dict[str, Any]:
    return {
        "path": rel,
        "source": source,
        "dataset": "x",
        "retrieved_at": "2026-10-20T08:00:00Z",
        "size": len(data),
        "sha256": snap._sha256_bytes(data),
        "licence": "CC BY 4.0",
        "attribution": "x",
    }


@pytest.mark.parametrize(
    ("case", "message"),
    [
        ("no_size", "no valid 'size'"),
        ("size_text", "no valid 'size'"),
        ("no_licence", "no valid 'licence'"),
        ("bad_date", "invalid retrieved_at"),
        ("not_object", "is not an object"),
        ("duplicate", "lists osm/testtown.osm twice"),
    ],
)
def test_fetch_refuses_a_malformed_bundled_manifest(
    repo: TmpRepo, tmp_path: Path, case: str, message: str
) -> None:
    raw = _empty_raw(repo)
    osm, dem = b"<osm/>", b"dem"
    entries: list[Any] = [
        _good_entry("osm/testtown.osm", osm, "osm"),
        _good_entry("mml/dem_2m/a.tif", dem, "mml_dem_2m"),
    ]
    if case == "no_size":
        del entries[0]["size"]
    elif case == "size_text":
        entries[0]["size"] = "6"
    elif case == "no_licence":
        del entries[0]["licence"]
    elif case == "bad_date":
        entries[0]["retrieved_at"] = "yesterday"
    elif case == "not_object":
        entries.append("osm/testtown.osm")
    else:
        entries.append(dict(entries[0]))
    content = _zip_with_manifest(
        tmp_path / "bad.zip", entries, {"osm/testtown.osm": osm, "mml/dem_2m/a.tif": dem}
    )
    config = _pin(repo, snap._sha256_bytes(content))
    with pytest.raises(SnapshotError, match=message):
        _fetch(repo, config, _serving(content))
    assert not raw.exists()


def test_fetch_refuses_members_differing_only_in_case(repo: TmpRepo, tmp_path: Path) -> None:
    raw = _empty_raw(repo)
    osm, dem = b"<osm/>", b"dem"
    files = {"osm/testtown.osm": osm, "mml/dem_2m/a.tif": dem, "mml/dem_2m/A.tif": dem}
    entries = [
        _good_entry(rel, data, "osm" if "osm" in rel else "mml_dem_2m")
        for rel, data in files.items()
    ]
    content = _zip_with_manifest(tmp_path / "case.zip", entries, files)
    config = _pin(repo, snap._sha256_bytes(content))
    with pytest.raises(SnapshotError, match="differ only in case"):
        _fetch(repo, config, _serving(content))
    assert not raw.exists()
