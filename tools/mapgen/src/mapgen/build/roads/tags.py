"""OSM road tags: the road class, flags with access tags, and the parsed values.

OSM values always win over the map's class defaults (`roads.classes` in map.yaml). Each
value comes with its source, `osm` or `default`. Nothing here is map-specific: the class
mapping (`service=driveway` -> `driveway`) is OSM's.
"""

import re
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal

# The shared name format (`mapgen.build.names`), re-exported for the road code.
from mapgen.build.names import name_fields as name_fields
from mapgen.build.names import name_value as name_value
from mapgen.config import RoadClassConfig, RoadsConfig

Source = Literal["osm", "default"]
MPH_TO_KMH = 1.609344
SERVICE_CLASSES = ("driveway", "parking_aisle")  # `service` values with their own class

_NUMBER_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*$")
_MPH_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*mph\s*$")
_WIDTH_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*(?:m)?\s*$")
_INT_RE = re.compile(r"^\s*([+-]?\d+)\s*$")

DRIVABLE_NO = ("no", "private")
DRIVABLE_YES = ("yes", "destination", "designated")
WALKABLE_YES = ("yes", "designated")


def road_class(tags: Mapping[str, str]) -> str | None:
    """The `highway` value, except `service=driveway` / `parking_aisle` are their own class."""
    highway = tags.get("highway")
    if highway is None:
        return None
    if highway == "service" and tags.get("service") in SERVICE_CLASSES:
        return tags["service"]
    return highway


def is_bridge(tags: Mapping[str, str]) -> bool:
    """Any `bridge` value other than `no`."""
    value = tags.get("bridge")
    return value is not None and value != "no"


def is_tunnel(tags: Mapping[str, str]) -> bool:
    """Any `tunnel` value other than `no` (on a waterway: a culvert)."""
    value = tags.get("tunnel")
    return value is not None and value != "no"


def parse_maxspeed(value: str | None) -> int | None:
    """km/h from a plain number or `<n> mph` (rounded); None for anything else or <= 0."""
    if value is None:
        return None
    if match := _NUMBER_RE.match(value):
        speed = round(float(match.group(1)))
    elif match := _MPH_RE.match(value):
        speed = round(float(match.group(1)) * MPH_TO_KMH)
    else:
        return None
    return speed if speed > 0 else None


def parse_lanes(value: str | None) -> int | None:
    """A positive integer, else None."""
    match = _INT_RE.match(value) if value is not None else None
    if match is None:
        return None  # also digits int() can't read, such as "²"
    lanes = int(match.group(1))
    return lanes if lanes > 0 else None


def parse_width(value: str | None) -> float | None:
    """Metres from `3`, `3 m` or `3.5`; None for invalid or <= 0 values."""
    if value is None:
        return None
    match = _WIDTH_RE.match(value)
    if not match:
        return None
    width = float(match.group(1))
    return width if width > 0 else None


def parse_layer(tags: Mapping[str, str]) -> int:
    """`layer` as an integer, default 0; a bridge without `layer` counts as layer 1."""
    value = tags.get("layer")
    if value is not None and (match := _INT_RE.match(value)):
        return int(match.group(1))
    return 1 if is_bridge(tags) else 0


def parse_oneway(tags: Mapping[str, str]) -> int:
    """1: one-way along the way, -1: against it, 0: two-way.

    `junction=roundabout` implies one-way along the way unless `oneway=no` (`-1` still
    reverses it; any other value, e.g. `reversible` or `0`, keeps it one-way).
    """
    value = tags.get("oneway")
    if value in ("yes", "true", "1"):
        return 1
    if value == "-1":
        return -1
    if tags.get("junction") == "roundabout" and value != "no":
        return 1
    return 0


def access_flags(
    tags: Mapping[str, str], defaults: RoadClassConfig
) -> tuple[bool, Source, bool, Source]:
    """(drivable, its source, walkable, its source) after the access tags.

    `access=no` clears both flags unless a more specific tag says otherwise;
    `motor_vehicle`, then `motorcar` (the most specific) set drivable; `foot` sets
    walkable. A flag whose value differs from the class default has the source `osm`.
    """
    drivable, walkable = defaults.drivable, defaults.walkable
    if tags.get("access") == "no":
        drivable = walkable = False
    for key in ("motor_vehicle", "motorcar"):
        value = tags.get(key)
        if value in DRIVABLE_NO:
            drivable = False
        elif value in DRIVABLE_YES:
            drivable = True
    foot = tags.get("foot")
    if foot == "no":
        walkable = False
    elif foot in WALKABLE_YES:
        walkable = True
    drivable_source: Source = "osm" if drivable != defaults.drivable else "default"
    walkable_source: Source = "osm" if walkable != defaults.walkable else "default"
    return drivable, drivable_source, walkable, walkable_source


@dataclass(frozen=True)
class WayAttributes:
    """A road way's resolved attributes; `invalid` lists the tags that fell back."""

    road_class: str
    drivable: bool
    drivable_source: Source
    walkable: bool
    walkable_source: Source
    lanes: int
    lanes_source: Source
    speed_kmh: int | None
    speed_source: Source | None
    width_m: float
    width_source: Source
    oneway: int  # 1, -1 or 0 (see `parse_oneway`)
    bridge: bool
    tunnel: bool
    layer: int
    names: dict[str, str | None]
    invalid: tuple[str, ...] = ()  # tags present but unparseable: "maxspeed", "lanes", ...


def resolve_attributes(tags: Mapping[str, str], cls: str, roads: RoadsConfig) -> WayAttributes:
    """The attributes of a way of class `cls` (which must be configured)."""
    defaults = roads.classes[cls]
    invalid: list[str] = []
    drivable, drivable_source, walkable, walkable_source = access_flags(tags, defaults)

    lanes = parse_lanes(tags.get("lanes"))
    if lanes is None and "lanes" in tags:
        invalid.append("lanes")
    width = parse_width(tags.get("width"))
    if width is None and "width" in tags:
        invalid.append("width")
    speed = parse_maxspeed(tags.get("maxspeed"))
    if speed is None and "maxspeed" in tags:
        invalid.append("maxspeed")
    if not drivable:
        speed_kmh, speed_source = None, None
    elif speed is not None:
        speed_kmh, speed_source = speed, "osm"
    else:
        speed_kmh = defaults.speed_kmh or roads.default_speed_kmh
        speed_source = "default"
    bridge = is_bridge(tags)
    return WayAttributes(
        road_class=cls,
        drivable=drivable,
        drivable_source=drivable_source,
        walkable=walkable,
        walkable_source=walkable_source,
        lanes=lanes if lanes is not None else defaults.lanes,
        lanes_source="osm" if lanes is not None else "default",
        speed_kmh=speed_kmh,
        speed_source=speed_source,
        width_m=width if width is not None else defaults.width_m,
        width_source="osm" if width is not None else "default",
        oneway=parse_oneway(tags),
        bridge=bridge,
        tunnel=is_tunnel(tags) and not bridge,  # both tags: treated as a bridge
        layer=parse_layer(tags),
        names=name_fields(tags),
        invalid=tuple(invalid),
    )
