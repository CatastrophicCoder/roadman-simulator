"""Somero's committed boundary (maps/somero/map.yaml) passes the narrow-feature rule
(ADR 0015 amendment of 2026-11-04). Reads only map.yaml, so it runs without raw data."""

from conftest import SOMERO_YAML
from mapgen.build.world_edge.narrow import narrow_features
from mapgen.build.world_edge.ring import Ring
from mapgen.config import load_map_config


def test_somero_boundary_has_no_spike_or_notch_under_the_minimum_width() -> None:
    config = load_map_config(SOMERO_YAML)
    coords = config.boundary_local()
    assert coords is not None, "Somero has a custom boundary"
    ring = Ring.from_coords(coords)
    width = config.world_edge.min_feature_width_m
    assert width == 3.0
    assert narrow_features(ring, width) == []
