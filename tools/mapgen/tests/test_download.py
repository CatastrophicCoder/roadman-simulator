import hashlib
import json
from typing import Any

import pytest

from conftest import FAKE_KEY, FIXED_NOW, Call, FakeHttp, TmpRepo, http_error
from mapgen.config import ConfigError, MapConfig
from mapgen.download import run_download, select_sources
from mapgen.manifest import manifest_path

OSM_XML = b"<osm/>"


def _run(config: MapConfig, repo: TmpRepo, http: FakeHttp, **kwargs: Any):
    kwargs.setdefault("env", {})
    return run_download(
        config,
        repo_root=repo.root,
        raw_dir=repo.raw_dir(config.id),
        http=http,
        log=lambda _: None,
        now=lambda: FIXED_NOW,
        **kwargs,
    )


def test_select_sources(fixture_config: MapConfig) -> None:
    assert select_sources(fixture_config, None) == ["osm", "mml_dem_2m", "mml_orthophoto"]
    assert select_sources(fixture_config, ["mml_dem_2m", "osm"]) == ["mml_dem_2m", "osm"]
    with pytest.raises(ConfigError, match="unknown source"):
        select_sources(fixture_config, ["nope"])
    with pytest.raises(ConfigError, match="disabled"):
        select_sources(fixture_config, ["mml_topographic"])


def test_manifest_contents(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML), only=["osm"])
    document = json.loads(manifest_path(tmp_repo.raw_dir("testtown")).read_text())
    assert document["schema_version"] == 1
    assert document["map_id"] == "testtown"
    assert document["generated_at"] == "2026-10-27T12:00:00Z"
    assert document["tool"]["name"] == "mapgen"
    (entry,) = document["files"]
    assert set(entry) == {
        "path",
        "source",
        "dataset",
        "request",
        "retrieved_at",
        "size",
        "sha256",
        "licence",
        "attribution",
        "origin",
    }
    assert entry["path"] == "osm/testtown.osm"
    assert entry["sha256"] == hashlib.sha256(OSM_XML).hexdigest()
    assert entry["size"] == len(OSM_XML)


def test_partial_run_keeps_other_entries(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    raw = tmp_repo.raw_dir("testtown")
    manual = raw / "mml" / "dem_2m" / "a.tif"
    manual.parent.mkdir(parents=True)
    manual.write_bytes(b"dem")
    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML))  # all enabled sources
    first = {e["path"] for e in json.loads(manifest_path(raw).read_text())["files"]}
    assert first == {"osm/testtown.osm", "mml/dem_2m/a.tif"}

    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML), only=["osm"], force=True)
    second = {e["path"] for e in json.loads(manifest_path(raw).read_text())["files"]}
    assert second == first


def test_failed_source_keeps_previous_entries_and_others_continue(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    raw = tmp_repo.raw_dir("testtown")
    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML), only=["osm"])

    def busy(call: Call) -> Any:
        raise http_error(429, call.url)

    report = _run(fixture_config, tmp_repo, FakeHttp(busy), force=True)
    assert set(report.failed) == {"osm"}
    paths = {e["path"] for e in json.loads(manifest_path(raw).read_text())["files"]}
    assert paths == {"osm/testtown.osm"}


def test_key_never_in_manifest_after_mml_download(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    from test_mml import FakeMml

    report = _run(
        fixture_config,
        tmp_repo,
        FakeHttp(FakeMml()),
        only=["mml_dem_2m"],
        env={"MML_API_KEY": FAKE_KEY},
        sleep=lambda _: None,
    )
    assert report.ok
    assert FAKE_KEY not in manifest_path(tmp_repo.raw_dir("testtown")).read_text()
