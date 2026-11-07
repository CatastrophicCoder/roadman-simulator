from pathlib import Path
from typing import Any

import pytest

from conftest import FAKE_KEY, REPO_ROOT, TmpRepo
from mapgen import cli, paths
from mapgen.cli import EXIT_FAILED, EXIT_OK, EXIT_USAGE, build_parser, main


def test_find_repo_root_from_nested_dir() -> None:
    assert paths.find_repo_root(REPO_ROOT / "tools" / "mapgen" / "src") == REPO_ROOT


def test_raw_dir_layout(tmp_path: Path) -> None:
    assert paths.raw_dir(tmp_path, "testtown") == tmp_path / "tools/mapgen/data/raw/testtown"
    assert paths.map_config_path(tmp_path, "testtown") == tmp_path / "maps/testtown/map.yaml"


def test_download_arguments() -> None:
    args = build_parser().parse_args(
        ["download", "testtown", "--only", "osm, mml_dem_2m", "--force"]
    )
    assert args.map_id == "testtown"
    assert args.only == ["osm", "mml_dem_2m"]
    assert args.force is True
    assert args.retries == 0
    assert build_parser().parse_args(["download", "testtown", "--retries", "3"]).retries == 3


@pytest.mark.parametrize("value", ["-1", "two"])
def test_download_retries_must_be_a_non_negative_number(value: str) -> None:
    with pytest.raises(SystemExit):
        build_parser().parse_args(["download", "testtown", "--retries", value])


def test_origin_command_prints_rounded_centre(
    capsys: pytest.CaptureFixture[str], tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["origin"] = {"x": 0, "y": 0}  # stale origin must not stop the helper
    tmp_repo.write_map(fixture_data)
    assert main(["--repo-root", str(tmp_repo.root), "origin", "testtown"]) == EXIT_OK
    assert "origin: {x: 309657, y: 6726616}" in capsys.readouterr().out


def test_unknown_map_is_a_usage_error(
    capsys: pytest.CaptureFixture[str], tmp_repo: TmpRepo
) -> None:
    assert main(["--repo-root", str(tmp_repo.root), "download", "nowhere"]) == EXIT_USAGE
    assert "map config not found" in capsys.readouterr().err


def test_unknown_only_source_is_a_usage_error(
    capsys: pytest.CaptureFixture[str], tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    tmp_repo.write_map(fixture_data)
    code = main(["--repo-root", str(tmp_repo.root), "download", "testtown", "--only", "nope"])
    assert code == EXIT_USAGE
    assert "unknown source" in capsys.readouterr().err


@pytest.mark.parametrize("map_id", ["../etc", "Somero", "a/b", "x y", "testtown\n"])
def test_invalid_map_id_is_rejected_before_paths(
    capsys: pytest.CaptureFixture[str], tmp_repo: TmpRepo, map_id: str
) -> None:
    assert main(["--repo-root", str(tmp_repo.root), "download", map_id]) == EXIT_USAGE
    assert "invalid map id" in capsys.readouterr().err


def test_unexpected_error_is_redacted(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    tmp_repo: TmpRepo,
    fixture_data: dict[str, Any],
) -> None:
    tmp_repo.write_map(fixture_data)
    monkeypatch.setenv("MML_API_KEY", FAKE_KEY)

    def boom(args: Any, repo_root: Path) -> int:
        raise RuntimeError(f"bad url https://h/x?api-key={FAKE_KEY} and {FAKE_KEY}")

    monkeypatch.setattr(cli, "cmd_download", boom)
    code = main(["--repo-root", str(tmp_repo.root), "download", "testtown"])
    assert code == EXIT_FAILED
    err = capsys.readouterr().err
    assert "unexpected RuntimeError" in err
    assert FAKE_KEY not in err


def test_build_raw_defaults_to_none_and_takes_a_folder() -> None:
    assert build_parser().parse_args(["build", "testtown"]).raw is None
    args = build_parser().parse_args(["build", "testtown", "--raw", "some/raw"])
    assert args.raw == Path("some/raw")


def _capture_run_build(monkeypatch: pytest.MonkeyPatch) -> dict[str, Any]:
    """Replace `run_build` with a stub that records its arguments and fails the build."""
    import mapgen.build

    seen: dict[str, Any] = {}

    def fake_run_build(config: Any, area: str, **kwargs: Any) -> Any:
        seen.update(kwargs, area=area)
        raise mapgen.build.BuildError("stub")

    monkeypatch.setattr(mapgen.build, "run_build", fake_run_build)
    return seen


def test_build_reads_the_default_raw_folder(
    monkeypatch: pytest.MonkeyPatch, tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    tmp_repo.write_map(fixture_data)
    seen = _capture_run_build(monkeypatch)
    main(["--repo-root", str(tmp_repo.root), "build", "testtown", "--area", "test"])
    assert seen["raw_dir"] == tmp_repo.raw_dir("testtown").resolve()
    assert seen["work_dir"] == paths.work_dir(tmp_repo.root, "testtown", "test")


def test_build_passes_the_given_raw_folder(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    tmp_repo: TmpRepo,
    fixture_data: dict[str, Any],
) -> None:
    tmp_repo.write_map(fixture_data)
    other = tmp_path / "elsewhere" / "raw"
    other.mkdir(parents=True)
    seen = _capture_run_build(monkeypatch)
    monkeypatch.chdir(tmp_path)
    args = ["--repo-root", str(tmp_repo.root), "build", "testtown", "--area", "test"]
    main([*args, "--raw", "elsewhere/raw"])  # relative to the working directory
    assert seen["raw_dir"] == other.resolve()
    assert seen["work_dir"] == paths.work_dir(tmp_repo.root, "testtown", "test")


def test_build_with_a_missing_raw_folder_fails_before_building(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    tmp_repo: TmpRepo,
    fixture_data: dict[str, Any],
) -> None:
    tmp_repo.write_map(fixture_data)
    seen = _capture_run_build(monkeypatch)
    args = ["--repo-root", str(tmp_repo.root), "build", "testtown", "--area", "test"]
    assert main([*args, "--raw", str(tmp_path / "missing")]) == EXIT_FAILED
    assert seen == {}
    assert "raw data folder not found" in capsys.readouterr().err
