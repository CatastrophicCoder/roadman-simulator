"""TM35 map sheets.

The expected names follow the documented TM35 scheme (see mapgen/tm35.py). L4211B and
L4212A are verified against sheets MML returned; the other references are derived.
"""

import pytest

from mapgen import tm35
from mapgen.geo import BBox, wgs84_to_map

HELSINKI = wgs84_to_map(24.9384, 60.1699, "EPSG:3067")  # (385611, 6672118)


@pytest.mark.parametrize(
    ("scale", "expected"),
    [
        (200_000, "L4"),
        (100_000, "L41"),
        (50_000, "L413"),
        (25_000, "L4133"),
        (10_000, "L4133B"),
        (5_000, "L4133B3"),
    ],
)
def test_helsinki_centre(scale: int, expected: str) -> None:
    assert tm35.sheet_at(*HELSINKI, scale) == expected


def test_l4_bounds() -> None:
    # Column 4 ends on the TM35FIN central meridian (E 500 000).
    assert tm35.sheet_bounds("L4") == BBox(308_000, 6_666_000, 500_000, 6_762_000)


def test_row_k_column_2_is_the_grid_corner() -> None:
    assert tm35.sheet_bounds("K2") == BBox(-76_000, 6_570_000, 116_000, 6_666_000)


def test_rows_skip_o_and_cover_finland() -> None:
    assert tm35.ROWS == "KLMNPQRSTUVWX"
    top = tm35.sheet_bounds("X4").max_y
    assert top == 6_570_000 + 13 * 96_000
    # Northernmost Finland (about 70.09 N, 27.9 E) is inside the grid.
    assert tm35.sheet_at(*wgs84_to_map(27.9, 70.09, "EPSG:3067"), 200_000)[0] == "X"


@pytest.mark.parametrize(
    ("name", "expected"),
    [
        ("L41", BBox(308_000, 6_666_000, 404_000, 6_714_000)),
        ("L42", BBox(308_000, 6_714_000, 404_000, 6_762_000)),
        ("L43", BBox(404_000, 6_666_000, 500_000, 6_714_000)),
        ("L44", BBox(404_000, 6_714_000, 500_000, 6_762_000)),
        ("L4133A", BBox(380_000, 6_666_000, 386_000, 6_672_000)),
        ("L4133B", BBox(380_000, 6_672_000, 386_000, 6_678_000)),
        ("L4133H", BBox(398_000, 6_672_000, 404_000, 6_678_000)),
        ("L4133B3", BBox(383_000, 6_672_000, 386_000, 6_675_000)),
    ],
)
def test_subdivision_order(name: str, expected: BBox) -> None:
    # Digits and letters run column by column from the south-west corner.
    assert tm35.sheet_bounds(name) == expected


@pytest.mark.parametrize("scale", tm35.SCALES)
def test_sheet_size_and_round_trip(scale: int) -> None:
    name = tm35.sheet_at(*HELSINKI, scale)
    bounds = tm35.sheet_bounds(name)
    assert (bounds.width, bounds.height) == tm35.sheet_size(scale)
    assert bounds.contains_point(*HELSINKI)
    assert tm35.sheet_at(bounds.min_x, bounds.min_y, scale) == name


def test_child_sheets_nest_in_parent() -> None:
    parent = tm35.sheet_bounds("L4133")
    children = tm35.sheets_covering(parent, 10_000)
    assert children == [f"L4133{letter}" for letter in "ABCDEFGH"]
    assert all(parent.contains(tm35.sheet_bounds(c)) for c in children)


@pytest.mark.parametrize(
    ("name", "expected"),
    [
        # Verified: sheets and extents returned by MML for the Somero download.
        ("L4211B", BBox(308_000, 6_720_000, 314_000, 6_726_000)),
        ("L4212A", BBox(308_000, 6_726_000, 314_000, 6_732_000)),
    ],
)
def test_verified_mml_sheets(name: str, expected: BBox) -> None:
    assert tm35.sheet_bounds(name) == expected
    assert tm35.sheet_at(expected.min_x + 1, expected.min_y + 1, 10_000) == name


def test_fixture_bbox_sheets() -> None:
    box = BBox(308_504, 6_725_445, 310_809, 6_727_787)
    assert tm35.sheets_covering(box, 10_000) == ["L4211B", "L4212A"]
    assert tm35.sheets_covering(box, 5_000) == ["L4211B2", "L4212A1"]
    assert tm35.sheets_covering(box, 25_000) == ["L4211", "L4212"]


def test_edge_on_sheet_boundary_does_not_add_neighbour() -> None:
    box = tm35.sheet_bounds("L4133B")
    assert tm35.sheets_covering(box, 10_000) == ["L4133B"]


@pytest.mark.parametrize("name", ["O4", "L7", "L1", "L41A", "L4135", "L4133I", "L4133B5", "l4"])
def test_invalid_names(name: str) -> None:
    with pytest.raises(ValueError):
        tm35.sheet_bounds(name)


def test_point_outside_grid() -> None:
    with pytest.raises(ValueError, match="outside"):
        tm35.sheet_at(0, 6_000_000, 10_000)


def test_unsupported_scale() -> None:
    with pytest.raises(ValueError, match="unsupported"):
        tm35.sheet_size(20_000)
