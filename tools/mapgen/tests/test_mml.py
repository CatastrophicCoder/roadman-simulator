import json
import urllib.parse
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import pytest

from conftest import FAKE_KEY, FIXED_NOW, Call, FakeHttp, TmpRepo, http_error
from mapgen.config import MapConfig
from mapgen.download import DownloadReport, run_download
from mapgen.manifest import manifest_path
from mapgen.sources.mml import (
    DEFAULT_BASE_URL,
    ensure_allowed_process,
    quote_result_path,
    result_filename,
)

BASE = DEFAULT_BASE_URL
FIXTURE_BOX_3067 = [308504, 6725445, 310809, 6727787]


class FakeMml:
    """A scripted MML OGC API Processes server."""

    def __init__(
        self,
        statuses: list[dict[str, Any]] | None = None,
        results: Any = None,
        file_host: str = "https://avoin-paikkatieto.maanmittauslaitos.fi",
    ) -> None:
        self.statuses: Iterator[dict[str, Any]] = iter(
            statuses or [{"status": "accepted"}, {"status": "running"}, {"status": "successful"}]
        )
        self.results = results or {
            "results": [
                {
                    "path": f"{file_host}/tiedostopalvelu/ogcproc/v1/jobs/j1/files/L4211B.tif",
                    "format": "TIFF",
                    "mapSheetNumber": "L4211B",
                },
                {
                    "path": f"{file_host}/tiedostopalvelu/ogcproc/v1/jobs/j1/files/L4212A.tif",
                    "format": "TIFF",
                    "mapSheetNumber": "L4212A",
                },
                {"zipPath": f"{file_host}/tiedostopalvelu/dl/v1/uncompressed/j1"},
            ]
        }
        self.jobs = 0

    def __call__(self, call: Call) -> Any:
        parts = urllib.parse.urlsplit(call.url)
        path = parts.path.split("/ogcproc/v1/")[-1]
        if call.method == "POST" and path.endswith("/execution"):
            self.jobs += 1
            return {"jobID": f"j{self.jobs}", "status": "accepted"}
        if call.method == "GET" and path.startswith("processes/"):
            return {"inputs": {"mapSheetInput": {"schema": {"type": "array"}}}}
        if call.method == "GET" and path.endswith("/results"):
            return self.results
        if call.method == "GET" and path.startswith("jobs/"):
            return next(self.statuses)
        if call.method == "DOWNLOAD":
            return b"raster-bytes:" + path.encode()
        raise AssertionError(f"unexpected call {call}")


def _run(
    config: MapConfig,
    repo: TmpRepo,
    http: FakeHttp,
    *,
    only: list[str],
    env: dict[str, str] | None = None,
    force: bool = False,
    log: list[str] | None = None,
    sleeps: list[float] | None = None,
) -> DownloadReport:
    sink = log if log is not None else []
    slept = sleeps if sleeps is not None else []
    return run_download(
        config,
        repo_root=repo.root,
        raw_dir=repo.raw_dir(config.id),
        only=only,
        force=force,
        http=http,
        env={"MML_API_KEY": FAKE_KEY} if env is None else env,
        log=sink.append,
        now=lambda: FIXED_NOW,
        sleep=slept.append,
    )


def _assert_no_key(report: DownloadReport, raw_dir: Path, log: list[str]) -> None:
    assert FAKE_KEY not in "\n".join(log)
    assert all(FAKE_KEY not in message for message in report.failed.values())
    if manifest_path(raw_dir).exists():
        assert FAKE_KEY not in manifest_path(raw_dir).read_text()


def test_dem_request_poll_and_download(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    server = FakeMml()
    http = FakeHttp(server)
    log: list[str] = []
    sleeps: list[float] = []
    report = _run(fixture_config, tmp_repo, http, only=["mml_dem_2m"], log=log, sleeps=sleeps)

    assert report.ok, report.failed
    post = next(c for c in http.calls if c.method == "POST")
    assert post.url.startswith(BASE + "processes/korkeusmalli_2m_bbox/execution?")
    assert urllib.parse.parse_qs(urllib.parse.urlsplit(post.url).query)["api-key"] == [FAKE_KEY]
    assert post.body == {
        "id": "korkeusmalli_2m_bbox",
        "inputs": {"boundingBoxInput": FIXTURE_BOX_3067, "fileFormatInput": "TIFF"},
    }
    assert sleeps == [5.0, 7.5]  # accepted, running, then successful; backoff x1.5

    raw = tmp_repo.raw_dir("testtown")
    target = raw / "mml" / "dem_2m"
    assert sorted(p.name for p in target.iterdir()) == ["L4211B.tif", "L4212A.tif"]
    downloads = [c for c in http.calls if c.method == "DOWNLOAD"]
    assert all("api-key=" in c.url for c in downloads)  # same host as the API

    entry = json.loads(manifest_path(raw).read_text())["files"][0]
    assert entry["path"] == "mml/dem_2m/L4211B.tif"
    assert entry["licence"] == "CC BY 4.0"
    assert entry["attribution"] == (
        "Contains data from the National Land Survey of Finland Elevation model 2 m, 10/2026"
    )
    assert entry["origin"] == "downloaded"
    assert entry["request"]["job_id"] == "j1"
    assert entry["request"]["result"]["mapSheetNumber"] == "L4211B"
    assert "api-key" not in json.dumps(entry)
    _assert_no_key(report, raw, log)


def test_key_not_sent_to_other_hosts(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    http = FakeHttp(FakeMml(file_host="https://files.example.invalid"))
    report = _run(fixture_config, tmp_repo, http, only=["mml_dem_2m"])
    assert report.ok
    downloads = [c for c in http.calls if c.method == "DOWNLOAD"]
    assert downloads and all(FAKE_KEY not in c.url for c in downloads)


def test_download_retries_with_key_on_other_mml_host(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    server = FakeMml(file_host="https://files.maanmittauslaitos.fi")

    def handler(call: Call) -> Any:
        if call.method == "DOWNLOAD" and "api-key" not in call.url:
            raise http_error(401, call.url)
        return server(call)

    http = FakeHttp(handler)
    report = _run(fixture_config, tmp_repo, http, only=["mml_dem_2m"])
    assert report.ok
    downloads = [c.url for c in http.calls if c.method == "DOWNLOAD"]
    assert len(downloads) == 4 and sum("api-key=" in u for u in downloads) == 2


def test_failed_job_reports_message(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    server = FakeMml(
        statuses=[
            {"status": "running"},
            {"status": "failed", "message": f"bad input (api-key={FAKE_KEY}) {FAKE_KEY}"},
        ]
    )
    log: list[str] = []
    report = _run(fixture_config, tmp_repo, FakeHttp(server), only=["mml_dem_2m"], log=log)
    assert "job j1 failed: bad input" in report.failed["mml_dem_2m"]
    _assert_no_key(report, tmp_repo.raw_dir("testtown"), log)


def test_dismissed_and_unknown_status(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    report = _run(
        fixture_config,
        tmp_repo,
        FakeHttp(FakeMml(statuses=[{"status": "dismissed"}])),
        only=["mml_dem_2m"],
    )
    assert "dismissed" in report.failed["mml_dem_2m"]
    report = _run(
        fixture_config,
        tmp_repo,
        FakeHttp(FakeMml(statuses=[{"state": "weird"}])),
        only=["mml_dem_2m"],
    )
    assert "unexpected status response" in report.failed["mml_dem_2m"]


def test_job_timeout(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    server = FakeMml(statuses=[{"status": "running"}] * 1000)
    clock = [0.0]

    def sleep(seconds: float) -> None:
        clock[0] += seconds

    raw = tmp_repo.raw_dir("testtown")
    report = run_download(
        fixture_config,
        repo_root=tmp_repo.root,
        raw_dir=raw,
        only=["mml_dem_2m"],
        http=FakeHttp(server),
        env={"MML_API_KEY": FAKE_KEY},
        log=lambda _: None,
        now=lambda: FIXED_NOW,
        sleep=sleep,
        monotonic=lambda: clock[0],
    )
    assert "did not finish within 3600 s" in report.failed["mml_dem_2m"]
    assert clock[0] <= 3600


def test_http_error_text_has_no_key(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    def handler(call: Call) -> Any:
        raise http_error(500, call.url)

    log: list[str] = []
    report = _run(fixture_config, tmp_repo, FakeHttp(handler), only=["mml_dem_2m"], log=log)
    message = report.failed["mml_dem_2m"]
    assert "HTTP 500" in message and "api-key=***" in message
    _assert_no_key(report, tmp_repo.raw_dir("testtown"), log)


@pytest.mark.parametrize(
    ("response", "message"),
    [
        ({"status": "accepted"}, "no jobID"),
        (["not", "a", "dict"], "unexpected execution response"),
    ],
)
def test_bad_execution_response(
    fixture_config: MapConfig, tmp_repo: TmpRepo, response: Any, message: str
) -> None:
    server = FakeMml()

    def handler(call: Call) -> Any:
        return response if call.method == "POST" else server(call)

    report = _run(fixture_config, tmp_repo, FakeHttp(handler), only=["mml_dem_2m"])
    assert message in report.failed["mml_dem_2m"]


@pytest.mark.parametrize(
    ("results", "message"),
    [
        ({"nope": []}, "no 'results' list"),
        ({"results": [{"format": "TIFF"}]}, "without a 'path'"),
    ],
)
def test_bad_results_response(
    fixture_config: MapConfig, tmp_repo: TmpRepo, results: Any, message: str
) -> None:
    report = _run(fixture_config, tmp_repo, FakeHttp(FakeMml(results=results)), only=["mml_dem_2m"])
    assert message in report.failed["mml_dem_2m"]


def test_orthophoto_uses_map_sheets(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"]["mml_orthophoto"]["options"]["year"] = 2023
    config = tmp_repo.load(fixture_data)
    http = FakeHttp(FakeMml())
    log: list[str] = []
    report = _run(config, tmp_repo, http, only=["mml_orthophoto"], log=log)

    assert report.ok, report.failed
    describe = http.calls[0]
    assert describe.method == "GET" and "/processes/ortoilmakuva_karttalehti?" in describe.url
    assert any("input mapSheetInput" in line for line in log)
    post = next(c for c in http.calls if c.method == "POST")
    assert post.body == {
        "id": "ortoilmakuva_karttalehti",
        "inputs": {
            "mapSheetInput": ["L4211B", "L4212A"],
            "fileFormatInput": "JPEG2000",
            "dataSetInput": "ortokuva",
            "yearInput": 2023,
        },
    }


def test_orthophoto_single_sheet_mode_and_latest_year(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    options = fixture_data["sources"]["mml_orthophoto"]["options"]
    options.update(map_sheet_input="single", sheet_scale=5000)
    config = tmp_repo.load(fixture_data)
    server = FakeMml(statuses=[{"status": "successful"}] * 2)
    http = FakeHttp(server)
    report = _run(config, tmp_repo, http, only=["mml_orthophoto"])

    assert report.ok, report.failed
    posts = [c.body["inputs"] for c in http.calls if c.method == "POST"]
    assert [p["mapSheetInput"] for p in posts] == ["L4211B2", "L4212A1"]
    assert all(p["yearInput"] == 9999 for p in posts)


def test_topographic_inputs(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"]["mml_topographic"]["enabled"] = True
    config = tmp_repo.load(fixture_data)
    http = FakeHttp(FakeMml())
    assert _run(config, tmp_repo, http, only=["mml_topographic"]).ok
    post = next(c for c in http.calls if c.method == "POST")
    assert post.body == {
        "id": "maastotietokanta_bbox",
        "inputs": {
            "boundingBoxInput": FIXTURE_BOX_3067,
            "themeInput": "maastotietokanta_kaikki",
            "fileFormatInput": "GPKG",
        },
    }
    assert (tmp_repo.raw_dir("testtown") / "mml" / "topographic" / "L4211B.tif").exists()


def test_buildings_3d_inputs(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"]["mml_buildings_3d"]["enabled"] = True
    fixture_data["sources"]["mml_buildings_3d"]["options"]["describe_process"] = False
    config = tmp_repo.load(fixture_data)
    http = FakeHttp(FakeMml())
    assert _run(config, tmp_repo, http, only=["mml_buildings_3d"]).ok
    assert http.calls[0].method == "POST"
    assert http.calls[0].body == {
        "id": "3d-rakennukset_karttalehti",
        "inputs": {"mapSheetInput": ["L4211B", "L4212A"]},
    }


def test_without_key_prints_manual_steps_and_registers_files(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    raw = tmp_repo.raw_dir("testtown")
    manual = raw / "mml" / "dem_2m" / "L4211B.tif"
    manual.parent.mkdir(parents=True)
    manual.write_bytes(b"manual raster")
    (manual.parent / ".DS_Store").write_bytes(b"x")

    http = FakeHttp(lambda call: pytest.fail("no request without a key"))
    log: list[str] = []
    report = _run(
        fixture_config, tmp_repo, http, only=["mml_dem_2m", "mml_orthophoto"], env={}, log=log
    )

    assert report.ok
    text = "\n".join(log)
    assert "https://asiointi.maanmittauslaitos.fi/karttapaikka/tiedostopalvelu" in text
    assert "Korkeusmalli 2 m" in text
    assert "308504, 6725445, 310809, 6727787" in text
    assert "tools/mapgen/data/raw/testtown/mml/dem_2m/" in text
    assert "L4211B, L4212A" in text
    assert "tools/mapgen/data/raw/testtown/mml/orthophoto/" in text

    document = json.loads(manifest_path(raw).read_text())
    assert document["map_id"] == "testtown"
    (entry,) = document["files"]
    assert entry["path"] == "mml/dem_2m/L4211B.tif"
    assert entry["origin"] == "manual"
    assert entry["size"] == len(b"manual raster")
    assert entry["licence"] == "CC BY 4.0"


def test_key_set_but_manual_files_present_skips_download(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    manual = tmp_repo.raw_dir("testtown") / "mml" / "dem_2m" / "own.tif"
    manual.parent.mkdir(parents=True)
    manual.write_bytes(b"x")
    http = FakeHttp(lambda call: pytest.fail("should not download"))
    report = _run(fixture_config, tmp_repo, http, only=["mml_dem_2m"])
    assert [f.origin for f in report.files] == ["manual"]

    forced = FakeHttp(FakeMml())
    assert _run(fixture_config, tmp_repo, forced, only=["mml_dem_2m"], force=True).ok
    assert any(c.method == "POST" for c in forced.calls)


def test_laser_scanning_guard() -> None:
    for process_id in (
        "laserkeilausaineisto_5p",
        "laserkeilausaineisto_0_5p",
        " Laserkeilausaineisto",
    ):
        with pytest.raises(ValueError, match="laser scanning"):
            ensure_allowed_process(process_id)
    assert ensure_allowed_process("korkeusmalli_2m_bbox") == "korkeusmalli_2m_bbox"


def test_client_refuses_laser_process(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    from mapgen.sources.base import DownloadContext
    from mapgen.sources.mml import Dem2mOptions, MmlClient

    http = FakeHttp(lambda call: pytest.fail("must not call the API"))
    ctx = DownloadContext(
        map_config=fixture_config,
        source_name="x",
        source_config=fixture_config.sources["mml_dem_2m"],
        options=Dem2mOptions(),
        raw_dir=tmp_repo.raw_dir("testtown"),
        repo_root=tmp_repo.root,
        force=False,
        http=http,
        env={},
    )
    client = MmlClient(ctx, Dem2mOptions(), FAKE_KEY)
    with pytest.raises(ValueError):
        client.execute("laserkeilausaineisto_5p", {})
    with pytest.raises(ValueError):
        client.describe("laserkeilausaineisto_5p")
    assert http.calls == []


def test_no_laser_process_in_source_code() -> None:
    src = Path(__file__).resolve().parents[1] / "src"
    for path in src.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        assert "laserkeilausaineisto_" not in text, path


def test_result_path_is_quoted_before_download(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    host = "https://avoin-paikkatieto.maanmittauslaitos.fi"
    server = FakeMml(
        results={"results": [{"path": f"{host}/files/L4211B 2024 ä.tif\n", "format": "TIFF"}]}
    )
    http = FakeHttp(server)
    report = _run(fixture_config, tmp_repo, http, only=["mml_dem_2m"])
    assert report.ok, report.failed
    (download,) = [c for c in http.calls if c.method == "DOWNLOAD"]
    assert "/files/L4211B%202024%20%C3%A4.tif%0A?" in download.url
    target = tmp_repo.raw_dir("testtown") / "mml" / "dem_2m"
    assert [p.name for p in target.iterdir()] == ["L4211B_2024_.tif"]
    assert quote_result_path("https://h/a%20b?x=1&y=2") == "https://h/a%20b?x=1&y=2"


@pytest.mark.parametrize(
    ("result", "expected"),
    [
        ({"path": "https://h/a/../x y.tif"}, "x_y.tif"),
        ({"path": "https://h/", "mapSheetNumber": "L4211B", "format": "TIFF"}, "L4211B.tiff"),
        ({"path": "https://h/files/..", "format": "../GPKG"}, "result-3.gpkg"),
    ],
)
def test_result_filename_is_safe(result: dict[str, Any], expected: str) -> None:
    assert result_filename(result, 3) == expected


def test_result_fields_in_manifest_are_redacted(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    host = "https://avoin-paikkatieto.maanmittauslaitos.fi"
    server = FakeMml(
        results={
            "results": [
                {
                    "path": f"{host}/files/a.tif?api-key={FAKE_KEY}",
                    "format": "TIFF",
                    "zipPath": f"{host}/files/all.zip?api-key={FAKE_KEY}",
                    "extra": [f"note {FAKE_KEY}"],
                }
            ]
        }
    )
    log: list[str] = []
    report = _run(fixture_config, tmp_repo, FakeHttp(server), only=["mml_dem_2m"], log=log)
    assert report.ok, report.failed
    _assert_no_key(report, tmp_repo.raw_dir("testtown"), log)
    entry = json.loads(manifest_path(tmp_repo.raw_dir("testtown")).read_text())["files"][0]
    assert entry["request"]["result"]["zipPath"].endswith("api-key=***")


def test_process_description_log_is_scrubbed(fixture_config: MapConfig, tmp_repo: TmpRepo) -> None:
    server = FakeMml()

    def handler(call: Call) -> Any:
        if call.method == "GET" and "/processes/" in call.url:
            return f"odd description echoing api-key={FAKE_KEY} {FAKE_KEY}"
        return server(call)

    log: list[str] = []
    report = _run(fixture_config, tmp_repo, FakeHttp(handler), only=["mml_orthophoto"], log=log)
    assert report.ok, report.failed
    assert any("description: odd description" in line for line in log)
    _assert_no_key(report, tmp_repo.raw_dir("testtown"), log)


def test_failed_force_run_keeps_previous_files(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    raw = tmp_repo.raw_dir("testtown")
    target = raw / "mml" / "dem_2m"
    assert _run(fixture_config, tmp_repo, FakeHttp(FakeMml()), only=["mml_dem_2m"]).ok
    before = {p.name: p.read_bytes() for p in target.iterdir()}
    manifest_before = manifest_path(raw).read_text()

    server = FakeMml()

    def second_download_fails(call: Call) -> Any:
        if call.method == "DOWNLOAD" and call.url.split("?")[0].endswith("L4212A.tif"):
            raise http_error(500, call.url)
        return server(call)

    report = _run(
        fixture_config, tmp_repo, FakeHttp(second_download_fails), only=["mml_dem_2m"], force=True
    )
    assert "mml_dem_2m" in report.failed
    assert {p.name: p.read_bytes() for p in target.iterdir()} == before
    assert manifest_path(raw).read_text() == manifest_before
    assert sorted(p.name for p in target.parent.iterdir()) == ["dem_2m"]


def test_successful_force_run_replaces_the_layer_folder(
    fixture_config: MapConfig, tmp_repo: TmpRepo
) -> None:
    raw = tmp_repo.raw_dir("testtown")
    stale = raw / "mml" / "dem_2m" / "stale.tif"
    stale.parent.mkdir(parents=True)
    stale.write_bytes(b"old")
    report = _run(fixture_config, tmp_repo, FakeHttp(FakeMml()), only=["mml_dem_2m"], force=True)
    assert report.ok, report.failed
    assert sorted(p.name for p in stale.parent.iterdir()) == ["L4211B.tif", "L4212A.tif"]
    paths = [e["path"] for e in json.loads(manifest_path(raw).read_text())["files"]]
    assert paths == ["mml/dem_2m/L4211B.tif", "mml/dem_2m/L4212A.tif"]
    assert sorted(p.name for p in stale.parent.parent.iterdir()) == ["dem_2m"]


def test_bbox_outside_tm35_grid_is_a_source_error(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    # A valid map far south of Finland: EPSG:3067 still projects it, but no TM35 sheet exists.
    fixture_data["bbox_wgs84"] = {
        "lat_min": 40.0,
        "lat_max": 40.02,
        "lon_min": 23.50,
        "lon_max": 23.54,
    }
    from mapgen.config import BBoxWGS84

    x, y = BBoxWGS84.model_validate(fixture_data["bbox_wgs84"]).centre_in("EPSG:3067")
    fixture_data["origin"] = {"x": round(x), "y": round(y)}
    config = tmp_repo.load(fixture_data)

    http = FakeHttp(lambda call: pytest.fail("no request expected"))
    for env in ({}, {"MML_API_KEY": FAKE_KEY}):
        report = _run(config, tmp_repo, http, only=["mml_orthophoto"], env=env)
        assert "outside the TM35 sheet grid" in report.failed["mml_orthophoto"]
