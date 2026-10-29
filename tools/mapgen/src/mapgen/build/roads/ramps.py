"""Ramps at bridge deck ends: the road surface blends from the deck end to the terrain.

A deck is straight from end to end, and its end edge (perpendicular to the deck's last
centre-line segment, as wide as the deck) is level. The terrain across that edge is
usually not. On the road side of every deck end that a ground way meets, the road and
path surfaces (and the graph points on them) blend over `bridge.ramp_length_m` from the
deck-end height to the normal surface height (terrain + `surface_offset_m`):

- The deck-end height `z_end` is the highest terrain + offset along the end edge, so the
  deck end is never below the terrain beside it, and the ramp never has to dig in.
- `alpha = 1 - d / L` (clamped to [0, 1]), d = the distance to the end edge segment and
  L = `ramp_length_m`. The field is continuous everywhere. The roads stage removes every
  road and path surface from the deck footprint within L behind a ramped end, so the only
  surface at an end edge is the ramp beyond it.
- One ramp: `blend = alpha * z_end + (1 - alpha) * base`. Overlapping ramps combine with
  weights `alpha / (1 - alpha)` (plus weight 1 for `base`), so each ramp is exact at its own
  end edge. A point on an end edge (alpha = 1) takes that ramp's `z_end`.
- `z = max(blend, base)`: the surface never goes below terrain + offset, so the terrain
  can't poke through. On an end edge `z = z_end`, so there is no step to the deck.

`base` is the terrain mesh height + `surface_offset_m`. The field is continuous, so the
draped surface has no steps; inside each terrain triangle both the surface and the terrain
are linear between vertices, so the surface stays above the terrain between vertices too.
The terrain itself is never changed.
"""

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

import numpy as np
import shapely
import shapely.ops
from shapely.geometry import LineString
from shapely.geometry.base import BaseGeometry

EDGE_TOL = 1e-6  # m: a point this close to an end edge is on it (float noise of the cuts)
ON_EDGE = 1e-9  # alpha within this of 1 counts as on the end edge


@dataclass(frozen=True)
class Ramp:
    """The ramp beyond one deck end. `u` points away from the deck, into the road side."""

    node: str  # the span end node
    end: tuple[float, float]  # local x, y of the end node (centre of the end edge)
    u: tuple[float, float]
    half_width: float  # half the deck width at this end
    z_end: float  # deck-end height
    length: float  # ramp length L

    @property
    def corners(self) -> np.ndarray:
        """The end edge's two ends (local x, y)."""
        e, n = np.array(self.end), np.array([-self.u[1], self.u[0]])
        return np.array([e + n * self.half_width, e - n * self.half_width])

    def zone(self) -> BaseGeometry:
        """The ramp area beyond the deck end: deck width x ramp length."""
        e, u = np.array(self.end), np.array(self.u)
        line = LineString([e, e + u * self.length])
        return line.buffer(self.half_width, cap_style="flat")

    def under(self, deck_footprint: BaseGeometry) -> BaseGeometry:
        """The deck footprint within `length` behind the end edge (no surface goes there)."""
        e, u = np.array(self.end), np.array(self.u)
        line = LineString([e, e - u * self.length])
        return line.buffer(self.half_width, cap_style="flat").intersection(deck_footprint)

    def alpha(self, xy: np.ndarray, tol: float = EDGE_TOL) -> np.ndarray:
        """The blend weight at (n, 2) local points (1 on the end edge, 0 from L away).

        Points within `tol` of the end edge are on it.
        """
        rel = np.asarray(xy, dtype=float).reshape(-1, 2) - np.array(self.end)
        u = np.array(self.u)
        t = rel @ u  # along, > 0 on the road side
        s = rel @ np.array([-u[1], u[0]])  # across
        lateral = np.maximum(np.abs(s) - self.half_width, 0.0)
        d = np.hypot(np.abs(t), lateral)
        alpha = np.clip(1.0 - d / self.length, 0.0, 1.0)
        alpha[d <= tol] = 1.0  # on the end edge
        return alpha

    def as_dict(self) -> dict[str, Any]:
        return {
            "node": self.node,
            "end_local": list(self.end),  # full precision: the checks rebuild the field
            "direction": list(self.u),
            "half_width_m": self.half_width,
            "z_end": self.z_end,
            "length_m": self.length,
        }

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Ramp":
        return cls(
            str(data["node"]),
            (float(data["end_local"][0]), float(data["end_local"][1])),
            (float(data["direction"][0]), float(data["direction"][1])),
            float(data["half_width_m"]),
            float(data["z_end"]),
            float(data["length_m"]),
        )


GRADED_TOL = 1e-6  # m: a point this close to a graded ramp's zone is in it


@dataclass(frozen=True)
class GradedRamp:
    """A gradient-limited ramp along a path, for a walk-only deck end that meets no path.

    The surface descends from the deck end at `grade` by distance to the deck's end edge:
    `z = max(z_end - grade * d, terrain + offset)`, inside `zone` (the path, as wide as the
    path, from the deck end to where the descent has met the terrain, plus the deck-wide
    area right beyond the end edge so the whole edge meets the ramp). On the end edge
    `z = z_end`. Along the path, the distance to the edge grows at most 1 m per metre, so
    the surface is never steeper than `grade` where it is above the terrain.
    """

    edge: Ramp  # the deck end edge (and the standard ramp area it replaces)
    grade: float
    path: tuple[tuple[float, float], ...]  # the path's centre line from the deck end
    path_half_width: float
    zone_length: float  # along the path

    def zone(self) -> BaseGeometry:
        line = LineString(self.path)
        length = min(self.zone_length, line.length)
        along = shapely.ops.substring(line, 0.0, length).buffer(
            self.path_half_width, cap_style="flat"
        )
        return along.union(self.edge.zone())

    def height(self, xy: np.ndarray) -> np.ndarray:
        """The descent from the deck end (before the max with the terrain)."""
        rel = np.asarray(xy, dtype=float).reshape(-1, 2) - np.array(self.edge.end)
        u = np.array(self.edge.u)
        t = rel @ u
        s = rel @ np.array([-u[1], u[0]])
        lateral = np.maximum(np.abs(s) - self.edge.half_width, 0.0)
        d = np.hypot(np.maximum(t, 0.0), lateral)
        return self.edge.z_end - self.grade * d

    def as_dict(self) -> dict[str, Any]:
        return {
            "edge": self.edge.as_dict(),
            "grade": self.grade,
            "path_local": [list(p) for p in self.path],
            "path_half_width_m": self.path_half_width,
            "zone_length_m": self.zone_length,
        }

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "GradedRamp":
        return cls(
            Ramp.from_dict(data["edge"]),
            float(data["grade"]),
            tuple((float(x), float(y)) for x, y in data["path_local"]),
            float(data["path_half_width_m"]),
            float(data["zone_length_m"]),
        )


class RampField:
    """The surface height with ramps: `apply(xy, base)` (see the module docstring)."""

    def __init__(
        self,
        ramps: Sequence[Ramp],
        tol: float = EDGE_TOL,
        graded: Sequence[GradedRamp] = (),
        zone_tol: float = GRADED_TOL,
    ) -> None:
        self.ramps = list(ramps)
        self.tol = tol  # on-edge tolerance (the pack checks read float32 vertices)
        self.graded = list(graded)
        self.zones = [g.zone().buffer(zone_tol) for g in self.graded]

    def apply(self, xy: np.ndarray, base: np.ndarray) -> np.ndarray:
        xy = np.asarray(xy, dtype=float).reshape(-1, 2)
        base = np.asarray(base, dtype=float).reshape(-1)
        if len(xy) == 0 or (not self.ramps and not self.graded):
            return base.copy()
        weight = np.zeros(len(xy))
        weighted = np.zeros(len(xy))
        exact = np.full(len(xy), np.nan)
        for ramp in self.ramps:
            alpha = ramp.alpha(xy, self.tol)
            on_edge = (alpha >= 1.0 - ON_EDGE) & np.isnan(exact)
            exact[on_edge] = ramp.z_end
            w = np.where(alpha < 1.0 - ON_EDGE, alpha / np.maximum(1.0 - alpha, ON_EDGE), 0.0)
            weight += w
            weighted += w * ramp.z_end
        blend = (weighted + base) / (weight + 1.0)
        blend = np.where(np.isnan(exact), blend, exact)
        return self._graded(xy, base, np.maximum(blend, base))

    def _graded(self, xy: np.ndarray, base: np.ndarray, z: np.ndarray) -> np.ndarray:
        """Inside a graded ramp's zone: max(descent from the deck end, terrain + offset)."""
        for ramp, zone in zip(self.graded, self.zones, strict=True):
            inside = shapely.contains_xy(zone, xy[:, 0], xy[:, 1])
            if inside.any():
                z[inside] = np.maximum(ramp.height(xy[inside]), base[inside])
        return z

    def in_zone(self, xy: np.ndarray) -> np.ndarray:
        """Where any ramp has a weight > 0."""
        xy = np.asarray(xy, dtype=float).reshape(-1, 2)
        result = np.zeros(len(xy), dtype=bool)
        for ramp in self.ramps:
            result |= ramp.alpha(xy, self.tol) > 0
        return result
