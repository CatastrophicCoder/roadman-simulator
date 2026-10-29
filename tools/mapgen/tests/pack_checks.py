"""The pipeline's own pack checks (`mapgen.build.pack_checks`), as used by the tests.

Every build runs these checks on the staged pack before the swap; the tests call the
same functions instead of keeping their own copy.
"""

from mapgen.build.pack_checks import (
    BRIDGE_NODE,
    DECK_RISE,
    FLOAT32_SLACK,
    MAX_STEP_M,
    MM,
    OVERLAP_M2,
    ROAD_SURFACES,
    TOLERANCE,
    UM,
    Chain,
    PackCheckError,
    PackSummary,
    RoadSummary,
    TriangleSampler,
    bridge_chains,
    check_connectivity_report,
    check_pack,
    check_roads,
    footprint_samples,
    local_points,
    read_glb_meshes,
)

__all__ = [
    "BRIDGE_NODE",
    "DECK_RISE",
    "FLOAT32_SLACK",
    "MAX_STEP_M",
    "MM",
    "OVERLAP_M2",
    "ROAD_SURFACES",
    "TOLERANCE",
    "UM",
    "Chain",
    "PackCheckError",
    "PackSummary",
    "RoadSummary",
    "TriangleSampler",
    "bridge_chains",
    "check_connectivity_report",
    "check_pack",
    "check_roads",
    "footprint_samples",
    "local_points",
    "read_glb_meshes",
]
