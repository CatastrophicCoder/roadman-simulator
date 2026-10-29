from typing import Any

import pytest

from conftest import FIXTURE_YAML, SOMERO_YAML, TmpRepo
from mapgen.config import ConfigError, load_map_config


def test_real_somero_config_loads() -> None:
    """Smoke test: the live maps/somero/map.yaml validates. Other tests use the fixture."""
    config = load_map_config(SOMERO_YAML)
    assert config.id == "somero"
    x, y = config.bbox_wgs84.centre_in(config.crs)
    assert (config.origin.x, config.origin.y) == (round(x), round(y))


def test_fixture_config_loads() -> None:
    config = load_map_config(FIXTURE_YAML)
    assert config.id == "testtown"
    assert config.crs == "EPSG:3067"
    assert config.tile_size_m == 256
    assert config.test_area.size_m == 300
    assert set(config.enabled_sources()) == {"osm", "mml_dem_2m", "mml_orthophoto"}
    assert set(config.sources) == {
        "osm",
        "mml_dem_2m",
        "mml_orthophoto",
        "mml_buildings_3d",
        "mml_topographic",
    }
    x, y = config.bbox_wgs84.centre_in(config.crs)
    assert (config.origin.x, config.origin.y) == (round(x), round(y)) == (309657, 6726616)


@pytest.mark.parametrize(
    ("key", "value", "message"),
    [
        ("lat_min", 60.65, "lat_min must be less than lat_max"),
        ("lon_max", 23.40, "lon_min must be less than lon_max"),
        ("lat_max", 91.0, "less than or equal to 90"),
        ("lon_min", -181.0, "greater than or equal to -180"),
    ],
)
def test_bad_bbox_is_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], key: str, value: float, message: str
) -> None:
    fixture_data["bbox_wgs84"][key] = value
    with pytest.raises(ConfigError, match=message):
        tmp_repo.load(fixture_data)


def test_origin_outside_bbox_is_rejected(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["origin"] = {"x": 300000, "y": 6726616}
    with pytest.raises(ConfigError, match="outside the projected bbox"):
        tmp_repo.load(fixture_data)


def test_test_area_must_fit_inside_bbox(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["test_area"] = {"size_m": 5000}
    with pytest.raises(ConfigError, match="test_area does not fit"):
        tmp_repo.load(fixture_data)


@pytest.mark.parametrize("section", [None, "bbox_wgs84", "origin", "test_area"])
def test_unknown_key_is_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], section: str | None
) -> None:
    target = fixture_data if section is None else fixture_data[section]
    target["surprise"] = 1
    with pytest.raises(ConfigError, match="Extra inputs are not permitted"):
        tmp_repo.load(fixture_data)


def test_unknown_source_option_is_rejected(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"]["mml_orthophoto"]["options"]["yaer"] = 2024
    with pytest.raises(ConfigError, match="mml_orthophoto"):
        tmp_repo.load(fixture_data)


def test_unknown_adapter_is_rejected(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"]["osm"]["adapter"] = "nope"
    with pytest.raises(ConfigError, match="unknown adapter 'nope'"):
        tmp_repo.load(fixture_data)


def test_missing_sources_is_rejected(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    del fixture_data["sources"]
    with pytest.raises(ConfigError, match="sources"):
        tmp_repo.load(fixture_data)


def test_empty_sources_is_rejected(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["sources"] = {}
    with pytest.raises(ConfigError, match="sources"):
        tmp_repo.load(fixture_data)


def test_source_without_adapter_is_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    del fixture_data["sources"]["osm"]["adapter"]
    with pytest.raises(ConfigError, match="adapter"):
        tmp_repo.load(fixture_data)


def test_id_must_match_folder(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    with pytest.raises(ConfigError, match="does not match the map folder name 'other'"):
        tmp_repo.load(fixture_data, folder="other")


def test_id_must_be_a_slug(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["id"] = "Test Town"
    with pytest.raises(ConfigError, match="id"):
        tmp_repo.load(fixture_data, folder="Test Town")


@pytest.mark.parametrize("crs", ["EPSG:999999", "not a crs", "EPSG:4326"])
def test_crs_must_be_a_projected_crs(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], crs: str
) -> None:
    fixture_data["crs"] = crs
    with pytest.raises(ConfigError, match="crs"):
        tmp_repo.load(fixture_data)


@pytest.mark.parametrize("size", [0, -256])
def test_tile_size_must_be_positive(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], size: int
) -> None:
    fixture_data["tile_size_m"] = size
    with pytest.raises(ConfigError, match="tile_size_m"):
        tmp_repo.load(fixture_data)


def test_missing_file_is_a_config_error(tmp_repo: TmpRepo) -> None:
    with pytest.raises(ConfigError, match="not found"):
        load_map_config(tmp_repo.root / "maps" / "nowhere" / "map.yaml")


def test_laser_scanning_process_is_rejected_in_config(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["sources"]["mml_dem_2m"]["options"]["process_id"] = "laserkeilausaineisto_5p"
    with pytest.raises(ConfigError, match="laser scanning"):
        tmp_repo.load(fixture_data)


# The optional `snapshot` block (pinned raw data for release builds)

VALID_SNAPSHOT = {
    "repo": "CatastrophicCoder/roadman-simulator",
    "release_tag": "data-testtown-2026-10-29",
    "asset": "mapdata-testtown-2026-10-29.zip",
    "sha256": "0" * 64,
}


def test_snapshot_block_is_optional(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    assert "snapshot" not in fixture_data
    assert tmp_repo.load(fixture_data).snapshot is None


def test_valid_snapshot_block_loads(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["snapshot"] = dict(VALID_SNAPSHOT)
    snapshot = tmp_repo.load(fixture_data).snapshot
    assert snapshot is not None
    assert snapshot.asset_url() == (
        "https://github.com/CatastrophicCoder/roadman-simulator/releases/download/"
        "data-testtown-2026-10-29/mapdata-testtown-2026-10-29.zip"
    )


@pytest.mark.parametrize(
    ("key", "value", "message"),
    [
        ("release_tag", "data-somero-2026-10-29", "must be data-testtown-YYYY-MM-DD"),
        ("release_tag", "data-testtown-2026-02-30", "no valid date"),
        ("release_tag", "data-testtown-2026-13-01", "no valid date"),
        ("release_tag", "data-testtown", "must end in -YYYY-MM-DD"),
        ("release_tag", "v-data-testtown-2026-10-29", "must be data-testtown-YYYY-MM-DD"),
        ("sha256", "g" * 64, "sha256"),
        ("sha256", "0" * 63, "sha256"),
        ("sha256", "A" * 64, "sha256"),
        ("asset", "dir/mapdata.zip", "plain file name"),
        ("asset", "dir\\mapdata.zip", "plain file name"),
        ("asset", "..mapdata.zip", "plain file name"),
        ("asset", "mapdata.tar.gz", ".zip file name"),
        ("repo", "no-slash", "repo"),
        ("repo", "a/b/c", "repo"),
        ("repo", "owner/na me", "repo"),
        ("repo", "owner/.", "repo"),
        ("repo", "owner/..", "repo"),
        ("repo", "owner/.hidden", "repo"),
    ],
)
def test_bad_snapshot_block_is_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], key: str, value: str, message: str
) -> None:
    fixture_data["snapshot"] = {**VALID_SNAPSHOT, key: value}
    with pytest.raises(ConfigError, match=message):
        tmp_repo.load(fixture_data)


def test_snapshot_block_rejects_extra_keys(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    fixture_data["snapshot"] = {**VALID_SNAPSHOT, "url": "https://example.invalid/x.zip"}
    with pytest.raises(ConfigError, match="Extra inputs are not permitted"):
        tmp_repo.load(fixture_data)
