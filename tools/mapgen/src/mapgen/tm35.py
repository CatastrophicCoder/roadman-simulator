"""ETRS-TM35FIN map sheet division (the Finnish "TM35 lehtijako", JHS 197).

The 1:200 000 sheets are 192 km x 96 km. Rows are lettered K..X from south to north
(no O), columns are numbered 2..6 from west to east. The grid is anchored at
E = -76 000 m (west edge of column 2) and N = 6 570 000 m (south edge of row K).

Each smaller scale splits the one above it:

- 1:100 000 (96 x 48 km), 1:50 000 (48 x 24 km), 1:25 000 (24 x 12 km): 2 x 2, digits 1..4
- 1:10 000 (6 x 6 km): the 1:25 000 sheet split 4 x 2, letters A..H
- 1:5 000 (3 x 3 km): the 1:10 000 sheet split 2 x 2, digits 1..4

Digits and letters run column by column from the south-west corner: 1 = SW, 2 = NW,
3 = SE, 4 = NE, and A = SW, B = NW, C, D for the second column, and so on to H = NE.
Example: L4 > L41 > L413 > L4133 > L4133B > L4133B1.

Only valid in EPSG:3067 coordinates.
"""

import math
import re
from dataclasses import dataclass

from mapgen.geo import BBox

GRID_E0 = -76_000.0
GRID_N0 = 6_570_000.0
ROWS = "KLMNPQRSTUVWX"
FIRST_COLUMN = 2
COLUMNS = 5
LETTERS = "ABCDEFGH"


@dataclass(frozen=True)
class _Level:
    scale: int
    width: float
    height: float
    cols: int  # subdivisions of the parent sheet, west-east
    rows: int  # subdivisions of the parent sheet, south-north
    symbols: str


_LEVELS: tuple[_Level, ...] = (
    _Level(100_000, 96_000, 48_000, 2, 2, "1234"),
    _Level(50_000, 48_000, 24_000, 2, 2, "1234"),
    _Level(25_000, 24_000, 12_000, 2, 2, "1234"),
    _Level(10_000, 6_000, 6_000, 4, 2, LETTERS),
    _Level(5_000, 3_000, 3_000, 2, 2, "1234"),
)
_BASE_W = 192_000.0
_BASE_H = 96_000.0
SCALES: tuple[int, ...] = (200_000, *(level.scale for level in _LEVELS))

_NAME_RE = re.compile(r"^([K-NP-X])([2-6])([1-4]{0,3})(?:([A-H])([1-4])?)?$")


def sheet_size(scale: int) -> tuple[float, float]:
    """(width, height) in metres of a sheet at `scale`."""
    if scale == 200_000:
        return (_BASE_W, _BASE_H)
    for level in _LEVELS:
        if level.scale == scale:
            return (level.width, level.height)
    raise ValueError(f"unsupported TM35 sheet scale 1:{scale}; supported: {SCALES}")


def sheet_at(easting: float, northing: float, scale: int) -> str:
    """The name of the sheet at `scale` that contains the point (south/west edges inclusive)."""
    sheet_size(scale)
    col = math.floor((easting - GRID_E0) / _BASE_W)
    row = math.floor((northing - GRID_N0) / _BASE_H)
    if not (0 <= col < COLUMNS and 0 <= row < len(ROWS)):
        raise ValueError(f"point ({easting}, {northing}) is outside the TM35 sheet grid")
    name = f"{ROWS[row]}{col + FIRST_COLUMN}"
    rel_e = easting - (GRID_E0 + col * _BASE_W)
    rel_n = northing - (GRID_N0 + row * _BASE_H)
    for level in _LEVELS:
        if scale > level.scale:
            break
        c = min(int(rel_e // level.width), level.cols - 1)
        r = min(int(rel_n // level.height), level.rows - 1)
        name += level.symbols[c * level.rows + r]
        rel_e -= c * level.width
        rel_n -= r * level.height
    return name


def sheet_bounds(name: str) -> BBox:
    """The EPSG:3067 extent of a sheet name such as `L4133B`."""
    match = _NAME_RE.match(name)
    if not match:
        raise ValueError(f"not a TM35 sheet name: {name!r}")
    row_letter, col_digit, digits, letter, last_digit = match.groups()
    symbols = list(digits)
    if letter:
        if len(digits) != 3:
            raise ValueError(f"not a TM35 sheet name: {name!r}")
        symbols.append(letter)
        if last_digit:
            symbols.append(last_digit)
    min_x = GRID_E0 + (int(col_digit) - FIRST_COLUMN) * _BASE_W
    min_y = GRID_N0 + ROWS.index(row_letter) * _BASE_H
    width, height = _BASE_W, _BASE_H
    for level, symbol in zip(_LEVELS, symbols, strict=False):
        index = level.symbols.index(symbol)
        c, r = divmod(index, level.rows)
        min_x += c * level.width
        min_y += r * level.height
        width, height = level.width, level.height
    return BBox(min_x, min_y, min_x + width, min_y + height)


def sheets_covering(bbox: BBox, scale: int) -> list[str]:
    """All sheets at `scale` that overlap `bbox` (EPSG:3067), sorted by name.

    A box edge that lies exactly on a sheet edge does not pull in the neighbouring sheet.
    """
    width, height = sheet_size(scale)
    i0 = math.floor((bbox.min_x - GRID_E0) / width)
    i1 = max(i0, math.ceil((bbox.max_x - GRID_E0) / width) - 1)
    j0 = math.floor((bbox.min_y - GRID_N0) / height)
    j1 = max(j0, math.ceil((bbox.max_y - GRID_N0) / height) - 1)
    names = {
        sheet_at(GRID_E0 + (i + 0.5) * width, GRID_N0 + (j + 0.5) * height, scale)
        for i in range(i0, i1 + 1)
        for j in range(j0, j1 + 1)
    }
    return sorted(names)
