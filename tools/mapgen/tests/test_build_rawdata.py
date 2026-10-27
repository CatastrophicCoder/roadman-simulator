"""Builds the real map's test area from the downloaded raw data (skipped without it).

Everything is written under tmp_path: the map config is copied into a temporary repo and
the raw folder is linked into it read-only. Run only these with `uv run pytest -m rawdata`.
"""

import shutil
from pathlib import Path

import pytest

from conftest import REPO_ROOT, SOMERO_YAML
from mapgen import paths
from mapgen.build import run_build
from mapgen.cli import EXIT_OK, main
from mapgen.config import load_map_config
from pack_checks import check_pack

pytestmark = pytest.mark.rawdata

MAP_ID = SOMERO_YAML.parent.name
RAW = paths.raw_dir(REPO_ROOT, MAP_ID)


def _require_raw_data() -> None:
    needed = [RAW / "download-manifest.json", RAW / "osm" / f"{MAP_ID}.osm"]
    if not all(p.is_file() for p in needed) or not list((RAW / "mml" / "dem_2m").glob("*.tif")):
        pytest.skip(f"raw data for {MAP_ID} not downloaded (run `mapgen download {MAP_ID}`)")


def _tmp_repo(tmp_path: Path) -> Path:
    """A repo with a copy of the map's config folder and a link to its raw data."""
    root = tmp_path / "repo"
    shutil.copytree(SOMERO_YAML.parent, paths.map_dir(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    raw.parent.mkdir(parents=True)
    raw.symlink_to(RAW, target_is_directory=True)
    return root


def test_raw_test_area_build(tmp_path: Path) -> None:
    _require_raw_data()
    root = _tmp_repo(tmp_path)
    config = load_map_config(paths.map_config_path(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    run_build(config, "test", raw, tmp_path / "pack", tmp_path / "work", log=lambda _: None)
    summary = check_pack(tmp_path / "pack", tmp_path / "work")
    assert summary.tile_count == 4
    assert summary.building_count > 0


def test_raw_cli_build(tmp_path: Path) -> None:
    _require_raw_data()
    root = _tmp_repo(tmp_path)
    args = ["--repo-root", str(root), "build", MAP_ID, "--area", "test"]
    assert main(args) == EXIT_OK
    assert (paths.pack_dir(root, MAP_ID) / "manifest.json").is_file()
    assert (paths.work_dir(root, MAP_ID, "test") / "01-load" / "report.json").is_file()
