"""Release support: `debug_only` sources, `download --strict/--release`, `build --release`.

No test touches the network: HTTP goes to FakeHttp (Overpass answers with a tiny XML
document, MML with the scripted `FakeMml` server).
"""

import json
import shutil
from datetime import datetime
from pathlib import Path
from typing import Any, ClassVar

import pytest
import yaml

from conftest import (
    FAKE_KEY,
    FIXED_NOW,
    FIXTURE_YAML,
    REPO_ROOT,
    SOMERO_YAML,
    Call,
    FakeHttp,
    TmpRepo,
)
from mapgen.build import run_build
from mapgen.cli import EXIT_FAILED, EXIT_OK, EXIT_USAGE, main
from mapgen.config import ConfigError, MapConfig, load_map_config
from mapgen.download import DownloadReport, run_download, select_sources
from mapgen.manifest import manifest_path
from mapgen.sources import registry
from mapgen.sources.base import DownloadContext, DownloadedFile, SourceAdapter
from test_mml import FakeMml
from test_orthophoto import ORTHO_ATTRIBUTION, _raw_with_ortho

OSM_XML = b"<osm/>"
SOMERO_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"


def _http() -> FakeHttp:
    """Overpass answers with OSM_XML; every MML request goes to a FakeMml server."""
    mml = FakeMml()

    def handler(call: Call) -> Any:
        return OSM_XML if "maanmittauslaitos" not in call.url else mml(call)

    return FakeHttp(handler)


def _data(ortho_debug_only: bool = True) -> dict[str, Any]:
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["sources"]["mml_orthophoto"]["debug_only"] = ortho_debug_only
    return data


def _run(
    config: MapConfig, repo: TmpRepo, http: FakeHttp, log: list[str], **kwargs: Any
) -> DownloadReport:
    kwargs.setdefault("env", {"MML_API_KEY": FAKE_KEY})
    return run_download(
        config,
        repo_root=repo.root,
        raw_dir=repo.raw_dir(config.id),
        http=http,
        log=log.append,
        now=lambda: FIXED_NOW,
        sleep=lambda _: None,
        **kwargs,
    )


def _place(repo: TmpRepo, rel: str, content: bytes = b"manual") -> Path:
    path = repo.raw_dir("testtown") / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)
    return path


# map.yaml: the debug_only flag


def test_debug_only_defaults_to_false(fixture_config: MapConfig) -> None:
    assert not any(src.debug_only for src in fixture_config.sources.values())
    assert fixture_config.debug_only_sources() == []
    assert fixture_config.for_release() == fixture_config


def test_debug_only_must_be_a_boolean(tmp_repo: TmpRepo) -> None:
    data = _data()
    data["sources"]["mml_orthophoto"]["debug_only"] = "sometimes"
    with pytest.raises(ConfigError, match="debug_only"):
        tmp_repo.load(data)


def test_for_release_disables_only_debug_only_sources(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    assert config.debug_only_sources() == ["mml_orthophoto"]
    release = config.for_release()
    assert list(release.enabled_sources()) == ["osm", "mml_dem_2m"]
    assert config.sources["mml_orthophoto"].enabled  # the original is unchanged


def test_somero_marks_only_the_orthophoto_debug_only() -> None:
    config = load_map_config(SOMERO_YAML)
    assert config.debug_only_sources() == ["mml_orthophoto"]


# download --release


def test_release_skips_debug_only_sources_and_logs_it(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    log: list[str] = []
    assert select_sources(config, None, release=True, log=log.append) == ["osm", "mml_dem_2m"]
    assert log == ["[mml_orthophoto] skipped (debug only)"]
    assert select_sources(config, None) == ["osm", "mml_dem_2m", "mml_orthophoto"]


def test_release_with_only_naming_a_debug_only_source_is_a_usage_error(
    tmp_repo: TmpRepo,
) -> None:
    config = tmp_repo.load(_data())
    with pytest.raises(ConfigError, match="mml_orthophoto are debug_only"):
        select_sources(config, ["osm", "mml_orthophoto"], release=True)
    assert select_sources(config, ["mml_orthophoto"]) == ["mml_orthophoto"]  # without --release


def test_cli_release_with_only_debug_only_source_exits_with_usage_error(
    capsys: pytest.CaptureFixture[str], tmp_repo: TmpRepo
) -> None:
    tmp_repo.write_map(_data())
    args = ["--repo-root", str(tmp_repo.root), "download", "testtown"]
    assert main([*args, "--only", "mml_orthophoto", "--release"]) == EXIT_USAGE
    assert "debug_only" in capsys.readouterr().err


def test_release_strict_succeeds_without_the_orthophoto(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    http = _http()
    log: list[str] = []
    report = _run(config, tmp_repo, http, log, release=True, strict=True)
    assert report.ok, report.failed
    assert {f.source for f in report.files} == {"osm", "mml_dem_2m"}
    assert "[mml_orthophoto] skipped (debug only)" in log
    executions = [c.url for c in http.calls if c.method == "POST"]
    assert len(executions) == 1 and "korkeusmalli_2m" in executions[0]  # the DEM job only
    assert not (tmp_repo.raw_dir("testtown") / "mml" / "orthophoto").exists()


# download --strict


def test_strict_fails_an_mml_source_without_key_even_with_files(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    _place(tmp_repo, "mml/dem_2m/own.tif")
    http = FakeHttp(lambda call: pytest.fail("no request without a key"))
    log: list[str] = []
    report = _run(config, tmp_repo, http, log, only=["mml_dem_2m"], env={}, strict=True)
    assert report.failed == {"mml_dem_2m": "MML_API_KEY is not set (required with --strict)"}
    assert report.files == []
    assert not any("Manual step" in line for line in log)  # no fallback to manual steps
    assert not manifest_path(tmp_repo.raw_dir("testtown")).exists()


def test_strict_reports_every_failing_source(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    report = _run(config, tmp_repo, _http(), [], env={}, strict=True)
    assert set(report.failed) == {"mml_dem_2m", "mml_orthophoto"}  # osm still downloads
    assert [f.source for f in report.files] == ["osm"]


def test_without_strict_a_keyless_mml_source_still_succeeds(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    http = FakeHttp(lambda call: pytest.fail("no request without a key"))
    report = _run(config, tmp_repo, http, [], only=["mml_dem_2m"], env={})
    assert report.ok and report.files == []  # unchanged: manual steps, no files, success


class _EmptyAdapter(SourceAdapter):
    """A source that downloads nothing (e.g. a service answering with an empty result)."""

    name: ClassVar[str] = "test_empty"
    licence: ClassVar[str] = "CC BY 4.0"

    def attribution(self, retrieved_at: datetime) -> str:
        return "empty"

    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        return []


def test_strict_fails_a_source_that_returned_no_files(
    monkeypatch: pytest.MonkeyPatch, tmp_repo: TmpRepo
) -> None:
    monkeypatch.setitem(registry.ADAPTERS, _EmptyAdapter.name, _EmptyAdapter)
    data = _data()
    data["sources"]["nothing"] = {"adapter": "test_empty", "enabled": True, "options": {}}
    config = tmp_repo.load(data)
    log: list[str] = []
    strict = _run(config, tmp_repo, _http(), log, only=["nothing"], strict=True)
    assert strict.failed == {"nothing": "returned no files (required with --strict)"}
    assert "[nothing] FAILED: returned no files (required with --strict)" in log
    assert _run(config, tmp_repo, _http(), [], only=["nothing"]).ok  # without --strict


def test_strict_with_key_downloads_and_succeeds(tmp_repo: TmpRepo) -> None:
    config = tmp_repo.load(_data())
    report = _run(config, tmp_repo, _http(), [], only=["osm", "mml_dem_2m"], strict=True)
    assert report.ok
    assert {f.origin for f in report.files} == {"downloaded"}


def test_cli_strict_prints_reasons_and_never_the_key(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    tmp_repo: TmpRepo,
) -> None:
    tmp_repo.write_map(_data())
    monkeypatch.delenv("MML_API_KEY", raising=False)
    args = ["--repo-root", str(tmp_repo.root), "download", "testtown"]
    code = main([*args, "--only", "mml_dem_2m", "--strict"])
    assert code == EXIT_FAILED
    err = capsys.readouterr().err
    assert "failed sources: mml_dem_2m" in err
    assert "mml_dem_2m: MML_API_KEY is not set (required with --strict)" in err


def test_cli_strict_failure_with_key_set_is_redacted(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    tmp_repo: TmpRepo,
) -> None:
    import mapgen.download as download

    tmp_repo.write_map(_data())
    monkeypatch.setenv("MML_API_KEY", FAKE_KEY)

    def fake_run(*_: Any, **kwargs: Any) -> DownloadReport:
        assert kwargs["strict"] and kwargs["release"]
        url = f"https://avoin-paikkatieto.maanmittauslaitos.fi/x?api-key={FAKE_KEY}"
        return DownloadReport(failed={"mml_dem_2m": f"HTTP 500 for {url} ({FAKE_KEY})"})

    monkeypatch.setattr(download, "run_download", fake_run)
    code = main(
        ["--repo-root", str(tmp_repo.root), "download", "testtown", "--release", "--strict"]
    )
    assert code == EXIT_FAILED
    captured = capsys.readouterr()
    assert FAKE_KEY not in captured.out + captured.err
    assert "mml_dem_2m: HTTP 500" in captured.err


# build --release


def _ortho_repo(root: Path, debug_only: bool) -> tuple[MapConfig, Path]:
    config = TmpRepo(root).load(_data(ortho_debug_only=debug_only))
    return config, _raw_with_ortho(root)


def _build(root: Path, config: MapConfig, raw: Path, **kwargs: Any) -> tuple[Path, list[str]]:
    pack = root / "game" / "maps" / "testtown"
    log: list[str] = []
    run_build(
        config, "test", raw, pack, root / "work", now=lambda: FIXED_NOW, log=log.append, **kwargs
    )
    return pack, log


def _credit_sources(pack: Path) -> set[str]:
    manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
    return {c["source"] for c in manifest["credits"]}


def test_release_build_skips_debug_only_imagery_even_with_files(tmp_path: Path) -> None:
    config, raw = _ortho_repo(tmp_path, debug_only=True)
    assert (raw / "mml" / "orthophoto" / "synthetic.tif").is_file()
    pack, log = _build(tmp_path, config, raw, release=True)
    assert not (pack / "debug").exists()
    assert _credit_sources(pack) == {"osm", "mml_dem_2m"}
    assert "[mml_orthophoto] skipped (debug only)" in log


def test_default_build_still_writes_debug_textures_for_debug_only_sources(
    tmp_path: Path,
) -> None:
    config, raw = _ortho_repo(tmp_path, debug_only=True)
    pack, log = _build(tmp_path, config, raw)  # release defaults to False
    assert (pack / "debug" / "orthophoto" / "tile_x0_y0.jpg").is_file()
    assert "mml_orthophoto" in _credit_sources(pack)
    assert not any("skipped (debug only)" in line for line in log)


def test_release_build_without_any_orthophoto_files(tmp_path: Path) -> None:
    config = TmpRepo(tmp_path).load(_data())
    raw = _raw_with_ortho(tmp_path, bounds=None)
    shutil.rmtree(raw / "mml" / "orthophoto", ignore_errors=True)
    pack, _ = _build(tmp_path, config, raw, release=True)
    assert not (pack / "debug").exists()
    assert (pack / "manifest.json").is_file()


# The committed Somero fixture through the CLI: build --release, then notice


@pytest.fixture(scope="module")
def somero_release(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """A repo-like folder with maps/somero/ and the fixture as raw data, built with
    `mapgen build somero --area test --release`."""
    root = tmp_path_factory.mktemp("somero-release")
    shutil.copytree(REPO_ROOT / "maps" / "somero", root / "maps" / "somero")
    raw = root / "tools" / "mapgen" / "data" / "raw" / "somero"
    shutil.copytree(SOMERO_FIXTURE, raw)
    assert list((raw / "mml" / "orthophoto").glob("*.jp2"))  # files exist, still skipped
    args = ["--repo-root", str(root), "build", "somero", "--area", "test", "--release"]
    assert main(args) == EXIT_OK
    return root


def test_somero_release_build_has_no_orthophoto(somero_release: Path) -> None:
    pack = somero_release / "game" / "maps" / "somero"
    assert not (pack / "debug").exists()
    assert _credit_sources(pack) == {"osm", "mml_dem_2m", "mml_lidar"}
    assert (pack / "camera_presets.json").is_file()


def test_somero_release_notice_round_trip(somero_release: Path, tmp_path: Path) -> None:
    notice = tmp_path / "build" / "NOTICE.txt"
    root = ["--repo-root", str(somero_release)]
    assert main([*root, "notice", "somero", "--out", str(notice)]) == EXIT_OK
    text = notice.read_text(encoding="utf-8")
    assert ORTHO_ATTRIBUTION not in text
    assert (
        "Contains data from the National Land Survey of Finland Elevation model 2 m, 10/2026"
        in text
    )
    assert "© OpenStreetMap contributors" in text
    assert main([*root, "notice", "--check", str(notice), "somero"]) == EXIT_OK


# The committed fixture's NOTICE.md


def test_fixture_notice_states_the_modifications_and_matches_the_cut_script() -> None:
    import importlib.util

    spec = importlib.util.spec_from_file_location(
        "cut_test_area", Path(__file__).resolve().parents[1] / "scripts" / "cut_test_area.py"
    )
    assert spec is not None and spec.loader is not None
    script = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(script)

    text = (SOMERO_FIXTURE / "NOTICE.md").read_text(encoding="utf-8")
    assert "National Land Survey of Finland (MML) data listed above was modified" in text
    assert "cut to the test area" in text and "re-encoded" in text
    assert "derivative database" in text and "ODbL 1.0" in text
    entries = json.loads((SOMERO_FIXTURE / "download-manifest.json").read_text())["files"]
    assert text == script._notice("somero", entries)  # regenerating gives the same file
