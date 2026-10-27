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
