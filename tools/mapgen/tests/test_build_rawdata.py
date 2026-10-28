"""Builds the real map's test area from the downloaded raw data (skipped without it).

Everything is written under tmp_path: the map config is copied into a temporary repo and
the raw folder is linked into it read-only. Run only these with `uv run pytest -m rawdata`.
"""

import shutil
from pathlib import Path
from typing import Any

import pytest
import rasterio
from rasterio.errors import NotGeoreferencedWarning

from conftest import REPO_ROOT, SOMERO_YAML
from mapgen import paths
from mapgen.build import run_build
from mapgen.cli import EXIT_OK, main
from mapgen.config import load_map_config
from pack_checks import check_pack

pytestmark = [
    pytest.mark.rawdata,
    pytest.mark.filterwarnings(
        f"ignore::{NotGeoreferencedWarning.__module__}.NotGeoreferencedWarning"
    ),
]

MAP_ID = SOMERO_YAML.parent.name
RAW = paths.raw_dir(REPO_ROOT, MAP_ID)


def _require_raw_data() -> None:
    needed = [RAW / "download-manifest.json", RAW / "osm" / f"{MAP_ID}.osm"]
    folders = {"dem_2m": "*.tif", "lidar_05p": "*.laz"}
    has_mml = all(list((RAW / "mml" / name).glob(pattern)) for name, pattern in folders.items())
    if not all(p.is_file() for p in needed) or not has_mml:
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


def test_raw_full_build_gets_lidar_heights_for_95_percent(tmp_path: Path) -> None:
    """Spec docs/specs/building-heights.md, acceptance criteria 2 and 11 on the whole map."""
    import json
    import math

    _require_raw_data()
    root = _tmp_repo(tmp_path)
    config = load_map_config(paths.map_config_path(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    pack, work = tmp_path / "pack", tmp_path / "work"
    run_build(config, "full", raw, pack, work, log=lambda _: None)
    buildings = json.loads((pack / "buildings.json").read_text(encoding="utf-8"))["buildings"]
    assert buildings
    for building in buildings:
        assert math.isfinite(building["height_m"]) and building["height_m"] > 0, building["id"]
    lidar = [b for b in buildings if b["height_source"] == "mml_lidar"]
    assert len(lidar) >= 0.95 * len(buildings), f"{len(lidar)} of {len(buildings)}"
    assert all(b["roof_z_p90"] >= b["roof_z_median"] for b in lidar)
    report = json.loads((work / "03-buildings" / "report.json").read_text(encoding="utf-8"))
    assert report["height_source_counts"]["mml_lidar"] == len(lidar)
    assert set(report["lidar"]["rejected_counts"]) == {
        "few_points",
        "tree_cover",
        "too_low",
        "too_high",
    }
    manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
    credits = manifest["credits"]
    assert "Laser scanning data 0,5 p" in {c["dataset"] for c in credits}
    textures = _check_debug_textures(config, pack, manifest)
    print(
        f"\n{MAP_ID} full: {len(lidar)}/{len(buildings)} mml_lidar, "
        f"sources {report['height_source_counts']}, lidar {report['lidar']}, "
        f"debug textures {textures}"
    )


def _check_debug_textures(config: Any, pack: Path, manifest: dict[str, Any]) -> str:
    """With the orthophoto downloaded, every tile has a texture of its size (else none)."""
    from mapgen.build.orthophoto import texture_path, texture_size
    from mapgen.geo import BBox
    from mapgen.sources.registry import enabled_sources_providing

    imagery = enabled_sources_providing(config, "imagery")
    if not imagery or not list((RAW / "mml" / "orthophoto").glob("*.jp2")):
        assert not (pack / "debug").exists()
        return "none"
    m_per_px = imagery[0].options.debug_texture_m_per_px
    assert (pack / "debug" / ".gdignore").is_file()
    assert "Orthophoto" in {c["dataset"] for c in manifest["credits"]}
    total = 0
    for tile in manifest["tiles"]:
        path = pack / texture_path(tile["id"])
        with rasterio.open(path) as src:
            assert (src.width, src.height) == texture_size(BBox(*tile["bounds_local"]), m_per_px)
        total += path.stat().st_size
    return f"{len(manifest['tiles'])} files, {total / 1e6:.1f} MB at {m_per_px} m/px"
