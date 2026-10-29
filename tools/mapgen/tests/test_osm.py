import urllib.parse
from datetime import timedelta
from email.utils import format_datetime
from pathlib import Path

import pytest

from conftest import FIXED_NOW, Call, FakeHttp, TmpRepo, http_error
from mapgen.config import MapConfig
from mapgen.download import run_download
from mapgen.http import HttpError
from mapgen.sources.osm import (
    DEFAULT_OVERPASS_URL,
    build_query,
    overpass_runtime_error,
    retry_after_s,
)

OSM_XML = b'<?xml version="1.0"?><osm version="0.6"><node id="1" lat="60.63" lon="23.52"/></osm>'


def test_query_text() -> None:
    assert build_query(60.62, 23.50, 60.64, 23.54, 180) == (
        "[out:xml][timeout:180];(node(60.62,23.5,60.64,23.54);way(60.62,23.5,60.64,23.54););"
        "(._;>;);(._;rel(bn);rel(bw););out meta;"
    )


def _run(
    config: MapConfig,
    repo: TmpRepo,
    http: FakeHttp,
    force: bool = False,
    retries: int = 0,
    sleeps: list[float] | None = None,
    logs: list[str] | None = None,
):
    def no_sleep(seconds: float) -> None:
        if sleeps is None:
            pytest.fail("unexpected sleep")
        sleeps.append(seconds)

    return run_download(
        config,
        repo_root=repo.root,
        raw_dir=repo.raw_dir(config.id),
        only=["osm"],
        force=force,
        retries=retries,
        http=http,
        env={},
        log=(lambda _: None) if logs is None else logs.append,
        now=lambda: FIXED_NOW,
        sleep=no_sleep,
    )


def _busy_then_ok(statuses: list[int], retry_after: str | None = None) -> FakeHttp:
    """Fails with each status in turn, then returns OSM_XML."""
    remaining = list(statuses)

    def handler(call: Call) -> bytes:
        if remaining:
            raise HttpError(call.url, remaining.pop(0), "busy", "<html>busy</html>", retry_after)
        return OSM_XML

    return FakeHttp(handler)


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


@pytest.mark.parametrize("status", [429, 502, 503, 504])
def test_busy_server_fails_once_without_retry(
    fixture_config: MapConfig, tmp_repo: TmpRepo, status: int
) -> None:
    def handler(call: Call) -> bytes:
        raise http_error(status, call.url)

    http = FakeHttp(handler)
    report = _run(fixture_config, tmp_repo, http)  # default: retries=0, any sleep fails
    assert len(http.calls) == 1
    assert f"Overpass busy (HTTP {status})" in report.failed["osm"]
    assert "--retries" in report.failed["osm"]


@pytest.mark.parametrize("status", [429, 502, 503, 504])
def test_busy_server_is_retried_with_backoff(
    fixture_config: MapConfig, tmp_repo: TmpRepo, status: int
) -> None:
    http = _busy_then_ok([status] * 3)
    sleeps: list[float] = []
    logs: list[str] = []
    report = _run(fixture_config, tmp_repo, http, retries=3, sleeps=sleeps, logs=logs)

    assert report.ok
    assert len(http.calls) == 4
    assert sleeps == [30, 60, 120]
    retry_logs = [line for line in logs if "retry" in line]
    assert retry_logs == [
        f"[osm] Overpass busy (HTTP {status}), retry {n} of 3 in {s} s"
        for n, s in [(1, 30), (2, 60), (3, 120)]
    ]
    query = urllib.parse.parse_qs(http.calls[0].body.decode())["data"][0]
    for line in logs:
        assert query not in line and "out meta" not in line and "<html>" not in line
    dest = tmp_repo.raw_dir("testtown") / "osm" / "testtown.osm"
    assert dest.read_bytes() == OSM_XML


def test_busy_server_gives_up_after_all_retries(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    http = _busy_then_ok([502] * 10)
    sleeps: list[float] = []
    report = _run(fixture_config, tmp_repo, http, retries=3, sleeps=sleeps)
    assert len(http.calls) == 4
    assert sleeps == [30, 60, 120]
    assert "Overpass busy (HTTP 502) after 4 attempts" in report.failed["osm"]
    assert "<html>" not in report.failed["osm"]
    assert not (tmp_repo.raw_dir("testtown") / "osm" / "testtown.osm").exists()


def test_other_errors_are_not_retried(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    http = _busy_then_ok([500])
    report = _run(fixture_config, tmp_repo, http, retries=3, sleeps=[])
    assert len(http.calls) == 1
    assert "Overpass request failed" in report.failed["osm"]


@pytest.mark.parametrize(
    ("header", "expected"),
    [
        ("5", 5),
        (" 120 ", 120),
        (format_datetime(FIXED_NOW + timedelta(seconds=90), usegmt=True), 90),
        (format_datetime(FIXED_NOW - timedelta(seconds=90), usegmt=True), 0),
    ],
)
def test_retry_after_replaces_backoff(
    fixture_config: MapConfig, tmp_repo: TmpRepo, header: str, expected: float
) -> None:
    http = _busy_then_ok([429], retry_after=header)
    sleeps: list[float] = []
    logs: list[str] = []
    report = _run(fixture_config, tmp_repo, http, retries=3, sleeps=sleeps, logs=logs)
    assert report.ok
    assert sleeps == [expected]
    assert any("(Retry-After)" in line for line in logs)


def test_unreadable_retry_after_falls_back_to_backoff(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    http = _busy_then_ok([503], retry_after="soon")
    sleeps: list[float] = []
    logs: list[str] = []
    assert _run(fixture_config, tmp_repo, http, retries=3, sleeps=sleeps, logs=logs).ok
    assert sleeps == [30]
    assert not any("Retry-After" in line for line in logs)


def test_too_long_retry_after_stops_retrying(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    http = _busy_then_ok([429], retry_after="3600")
    report = _run(fixture_config, tmp_repo, http, retries=3, sleeps=[])
    assert len(http.calls) == 1
    assert "Retry-After" in report.failed["osm"]


def test_retry_after_parsing() -> None:
    assert retry_after_s(None, FIXED_NOW) is None
    assert retry_after_s("", FIXED_NOW) is None
    assert retry_after_s("-5", FIXED_NOW) is None
    assert retry_after_s("Wed, 21 Oct 2015 07:28:00", FIXED_NOW) == 0


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
