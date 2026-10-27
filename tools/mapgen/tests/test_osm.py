import urllib.parse
from pathlib import Path

import pytest

from conftest import FIXED_NOW, Call, FakeHttp, TmpRepo, http_error
from mapgen.config import MapConfig
from mapgen.download import run_download
from mapgen.sources.osm import DEFAULT_OVERPASS_URL, build_query, overpass_runtime_error

OSM_XML = b'<?xml version="1.0"?><osm version="0.6"><node id="1" lat="60.63" lon="23.52"/></osm>'


def test_query_text() -> None:
    assert build_query(60.62, 23.50, 60.64, 23.54, 180) == (
        "[out:xml][timeout:180];(node(60.62,23.5,60.64,23.54);way(60.62,23.5,60.64,23.54););"
        "(._;>;);(._;rel(bn);rel(bw););out meta;"
    )


def _run(config: MapConfig, repo: TmpRepo, http: FakeHttp, force: bool = False):
    return run_download(
        config,
        repo_root=repo.root,
        raw_dir=repo.raw_dir(config.id),
        only=["osm"],
        force=force,
        http=http,
        env={},
        log=lambda _: None,
        now=lambda: FIXED_NOW,
    )


def test_download_posts_query_and_writes_file(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    http = FakeHttp(lambda call: OSM_XML)
    report = _run(fixture_config, tmp_repo, http)

    assert report.ok
    (call,) = http.calls
    assert call.method == "DOWNLOAD"
    assert call.url == DEFAULT_OVERPASS_URL
    assert call.headers == {"Content-Type": "application/x-www-form-urlencoded"}
    assert call.timeout is not None and call.timeout > 180
    form = urllib.parse.parse_qs(call.body.decode())
    assert form["data"] == [build_query(60.62, 23.5, 60.64, 23.54, 180)]

    dest = tmp_repo.raw_dir("testtown") / "osm" / "testtown.osm"
    assert dest.read_bytes() == OSM_XML
    (entry,) = report.files
    assert entry.licence == "ODbL 1.0"
    assert entry.attribution == "© OpenStreetMap contributors"
    assert entry.origin == "downloaded"


def test_existing_file_is_skipped_without_force(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML))
    http = FakeHttp(lambda call: pytest.fail("should not download"))
    report = _run(fixture_config, tmp_repo, http)
    assert http.calls == []
    assert report.files[0].origin == "downloaded"  # kept from the previous manifest

    forced = FakeHttp(lambda call: OSM_XML)
    _run(fixture_config, tmp_repo, forced, force=True)
    assert len(forced.calls) == 1


@pytest.mark.parametrize("status", [429, 504])
def test_busy_server_fails_once_without_retry(
    fixture_config: MapConfig, tmp_repo: TmpRepo, status: int
) -> None:
    def handler(call: Call) -> bytes:
        raise http_error(status, call.url)

    http = FakeHttp(handler)
    report = _run(fixture_config, tmp_repo, http)
    assert len(http.calls) == 1
    assert "Overpass busy" in report.failed["osm"]
    assert "try again later" in report.failed["osm"]


def test_runtime_error_remark_is_detected(tmp_path: Path) -> None:
    path = tmp_path / "x.osm"
    path.write_text(
        '<osm><remark> runtime error: Query timed out in "query" at line 1 </remark></osm>'
    )
    assert overpass_runtime_error(path) is not None
    path.write_bytes(OSM_XML)
    assert overpass_runtime_error(path) is None


def test_failed_force_run_keeps_previous_file(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    _run(fixture_config, tmp_repo, FakeHttp(lambda call: OSM_XML))
    dest = tmp_repo.raw_dir("testtown") / "osm" / "testtown.osm"

    remark = b'<osm><remark> runtime error: Query timed out in "query" at line 1 </remark></osm>'
    report = _run(fixture_config, tmp_repo, FakeHttp(lambda call: remark), force=True)
    assert "returned an error" in report.failed["osm"]
    assert dest.read_bytes() == OSM_XML

    def busy(call: Call) -> bytes:
        raise http_error(504, call.url)

    report = _run(fixture_config, tmp_repo, FakeHttp(busy), force=True)
    assert "Overpass busy" in report.failed["osm"]
    assert dest.read_bytes() == OSM_XML
    assert sorted(p.name for p in dest.parent.iterdir()) == ["testtown.osm"]
