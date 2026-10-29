"""Cut a map's downloaded raw data to its test area, for the committed test fixture.

    uv run python scripts/cut_test_area.py <map-id> [--out <folder>]

Runs stage 1 (`run_load`) for the map's test area on `data/raw/<map-id>/`, then writes a
copy of every raw file it read, cut to what that test-area load needs, into
`tests/fixtures/raw/<map-id>-test-area/` (laid out like `data/raw/<map-id>/`, so it can be
copied in as the raw folder):

- `.osm`: the footprints whose centroid is in the test area, with their member ways and
  nodes, and every `highway` way, water feature (water area or waterway line), land use
  area and point of interest (node, way or relation) whose point, line or area meets the
  test area, with all of its nodes (the build cuts roads at the test-area edge, not this
  script). Contributor metadata (`user`, `uid`, `timestamp`, `changeset`, `version`) is
  removed; the ODbL `<note>` stays.
- GeoPackage (Topographic Database): the `rakennus` records whose polygon meets the test
  area, all columns, in a new GeoPackage written with `sqlite3` only (its
  `gpkg_spatial_ref_sys`, `gpkg_contents` and `gpkg_geometry_columns` rows copied; no
  spatial index, whose triggers need SpatiaLite functions).
- GeoTIFF: the test area plus stage 1's DEM margin, rounded outward to the cell grid,
  deflate-compressed.
- LAS/LAZ: every point (all classes) inside the bounds stage 1 requests for the points.
- JPEG 2000 (orthophoto): the test area, rounded outward to the pixel grid, re-encoded
  lossy at `JP2_QUALITY` percent of the uncompressed size to keep the fixture small.

It also writes `download-manifest.json` (the real entries, with the size and checksum of
the cut files) and `NOTICE.md` with the attributions. Each entry's MML `result` record keeps
only the fields in `RESULT_FIELDS`; scanner, flight and project metadata is removed.
Nothing here is map-specific.
"""

import argparse
import json
import shutil
import sqlite3
import sys
import xml.etree.ElementTree as ET
from collections.abc import Mapping
from pathlib import Path
from typing import Any

import laspy
import numpy as np
import rasterio
import shapely
from rasterio.io import DatasetReader
from rasterio.windows import Window, from_bounds
from rasterio.windows import bounds as window_bounds
from shapely.geometry import LineString
from shapely.geometry.base import BaseGeometry

from mapgen import paths
from mapgen.build.area import BuildArea, build_area, to_world
from mapgen.build.stage1_load import DEM_MARGIN_M, LoadResult, run_load
from mapgen.config import MapConfig, load_map_config
from mapgen.geo import BBox
from mapgen.manifest import MANIFEST_NAME, SCHEMA_VERSION, load_entries
from mapgen.sources.base import parse_isoformat, sha256_of
from mapgen.sources.mml import GPKG_SUFFIXES, RAKENNUS_ID_PREFIX, RAKENNUS_TABLE, gpkg_wkb
from mapgen.sources.osm import METADATA_ATTRIBUTES

FIXTURES_DIR = paths.MAPGEN_DIR / "tests" / "fixtures" / "raw"
# Fields of a download's `result` record that the fixture keeps. Anything else (scanner,
# flight altitude, scanning project, point cloud id, or fields MML adds later) is dropped.
RESULT_FIELDS = (
    "url",
    "format",
    "crs",
    "mimeType",
    "length",
    "mapSheetNumber",
    "dataset",
    "datasetVersion",
    "lastModified",
    "dateOfScanning",
    "yearOfScanning",
    "pointDensity",
    "elevationSystem",
    "elevationPrecision",
    "yearOfPhotography",
)
LICENCE_LINKS = {
    "ODbL 1.0": "https://opendatacommons.org/licenses/odbl/1-0/",
    "CC BY 4.0": "https://creativecommons.org/licenses/by/4.0/",
}
OSM_COPYRIGHT = "https://www.openstreetmap.org/copyright"
MML_ATTRIBUTION_PREFIX = "Contains data from the National Land Survey of Finland"
TIFF_SUFFIXES = {".tif", ".tiff"}
LAZ_SUFFIXES = {".laz", ".las"}
JP2_SUFFIXES = {".jp2"}
JP2_QUALITY = 15  # GDAL JP2OpenJPEG QUALITY: compressed size as % of uncompressed


OSM_CONTENTS = (
    "building footprints whose centroid is in the test area, and highway ways, water "
    "features, land use areas and points of interest whose point, line or area meets it, "
    "with their member ways and all their nodes"
)
GPKG_CONTENTS = "Topographic Database buildings (rakennus) whose polygon meets the test area"
# GeoPackage tables copied as they are (their rows for the cut table only, where keyed).
GPKG_META_TABLES = ("gpkg_spatial_ref_sys", "gpkg_contents", "gpkg_geometry_columns")


def cut_osm(source: Path, dest: Path, feature_ids: set[str]) -> None:
    """Keep the given nodes, ways and relations ("osm:way/1"), their member ways and all
    their nodes."""
    tree = ET.parse(source)
    root = tree.getroot()
    ways = {int(i.split("/")[1]) for i in feature_ids if i.startswith("osm:way/")}
    relations = {int(i.split("/")[1]) for i in feature_ids if i.startswith("osm:relation/")}
    for element in root.iter("relation"):
        if int(element.attrib["id"]) in relations:
            ways |= {
                int(m.attrib["ref"]) for m in element.iter("member") if m.attrib["type"] == "way"
            }
    nodes: set[int] = {int(i.split("/")[1]) for i in feature_ids if i.startswith("osm:node/")}
    for element in root.iter("way"):
        if int(element.attrib["id"]) in ways:
            nodes |= {int(nd.attrib["ref"]) for nd in element.iter("nd")}
    keep = {"node": nodes, "way": ways, "relation": relations}
    for element in list(root):
        if element.tag in keep and int(element.attrib["id"]) not in keep[element.tag]:
            root.remove(element)
        elif element.tag not in keep and element.tag not in ("note", "meta"):
            root.remove(element)
    for element in root.iter():
        for name in METADATA_ATTRIBUTES:
            if element is not root:
                element.attrib.pop(name, None)
    ET.indent(tree, space="  ")
    dest.parent.mkdir(parents=True, exist_ok=True)
    tree.write(dest, encoding="UTF-8", xml_declaration=True)
    with dest.open("a", encoding="utf-8") as handle:
        handle.write("\n")


def cut_gpkg(source: Path, dest: Path, table: str, ids: set[int]) -> int:
    """A new GeoPackage with the rows of `table` whose `mtk_id` is in `ids` (all columns).

    Written with `sqlite3` only: the GeoPackage application id and user version, the
    tables `GPKG_META_TABLES` (all spatial reference systems, and the `gpkg_contents` and
    `gpkg_geometry_columns` rows of `table`, with its extent recomputed) and `table` itself,
    created from the source's own SQL. The spatial index and its triggers are left out
    (they need SpatiaLite functions); they are optional in a GeoPackage. Returns the
    number of rows.
    """
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.unlink(missing_ok=True)
    src = sqlite3.connect(f"{source.resolve().as_uri()}?mode=ro", uri=True)
    out = sqlite3.connect(dest)
    try:
        application_id = src.execute("PRAGMA application_id").fetchone()[0]
        user_version = src.execute("PRAGMA user_version").fetchone()[0]
        out.execute(f"PRAGMA application_id = {int(application_id)}")
        out.execute(f"PRAGMA user_version = {int(user_version)}")
        for name in (*GPKG_META_TABLES, table):
            sql = src.execute(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = ?", (name,)
            ).fetchone()
            if sql is None:
                raise SystemExit(f"{source} has no table {name!r}")
            out.execute(sql[0])
        columns = [row[1] for row in src.execute(f'PRAGMA table_info("{table}")')]
        names = ", ".join(f'"{c}"' for c in columns)
        marks = ", ".join("?" for _ in columns)
        rows = [
            row
            for row in src.execute(f'SELECT {names} FROM "{table}" ORDER BY fid')
            if row[columns.index("mtk_id")] in ids
        ]
        out.executemany(f'INSERT INTO "{table}" ({names}) VALUES ({marks})', rows)
        for name, key in (
            ("gpkg_spatial_ref_sys", None),
            ("gpkg_contents", "table_name"),
            ("gpkg_geometry_columns", "table_name"),
        ):
            meta_columns = [row[1] for row in src.execute(f"PRAGMA table_info({name})")]
            query = f"SELECT * FROM {name}" + (f" WHERE {key} = ?" if key else "")
            meta_rows = src.execute(query, (table,) if key else ()).fetchall()
            out.executemany(
                f"INSERT INTO {name} VALUES ({', '.join('?' for _ in meta_columns)})", meta_rows
            )
        geometry_column = src.execute(
            "SELECT column_name FROM gpkg_geometry_columns WHERE table_name = ?", (table,)
        ).fetchone()[0]
        extent = _gpkg_extent([row[columns.index(geometry_column)] for row in rows])
        if extent is not None:
            out.execute(
                "UPDATE gpkg_contents SET min_x = ?, min_y = ?, max_x = ?, max_y = ? "
                "WHERE table_name = ?",
                (*extent, table),
            )
        out.commit()
        out.execute("VACUUM")
    finally:
        src.close()
        out.close()
    return len(rows)


def _gpkg_extent(blobs: list[Any]) -> tuple[float, float, float, float] | None:
    bounds = [
        shapely.from_wkb(wkb).bounds
        for blob in blobs
        if blob is not None and (wkb := gpkg_wkb(bytes(blob))) is not None
    ]
    if not bounds:
        return None
    array = np.asarray(bounds, dtype=float)
    return (
        float(array[:, 0].min()),
        float(array[:, 1].min()),
        float(array[:, 2].max()),
        float(array[:, 3].max()),
    )


def _intersects(geometry: BaseGeometry, area: BuildArea) -> bool:
    """Whether the geometry (a line or an area) meets the closed area box."""
    return bool(shapely.intersects(geometry, shapely.box(*area.bounds.as_list())))


def snapped_window(src: DatasetReader, bounds: BBox) -> Window:
    """The whole cells covering `bounds` (rounded outward to the grid), inside the raster."""
    window = from_bounds(*bounds.as_list(), transform=src.transform)
    col0, row0 = int(np.floor(window.col_off)), int(np.floor(window.row_off))
    col1 = int(np.ceil(window.col_off + window.width))
    row1 = int(np.ceil(window.row_off + window.height))
    col0, row0 = max(col0, 0), max(row0, 0)
    col1, row1 = min(col1, src.width), min(row1, src.height)
    return Window(col0, row0, col1 - col0, row1 - row0)


def cut_geotiff(source: Path, dest: Path, bounds: BBox) -> None:
    """The cells covering `bounds` (rounded outward to the grid), deflate-compressed."""
    with rasterio.open(source) as src:
        snapped = snapped_window(src, bounds)
        data = src.read(1, window=snapped)
        profile = {
            "driver": "GTiff",
            "dtype": src.dtypes[0],
            "nodata": src.nodata,
            "width": snapped.width,
            "height": snapped.height,
            "count": 1,
            "crs": src.crs,
            "transform": src.window_transform(snapped),
            "compress": "deflate",
            "predictor": 3 if np.issubdtype(data.dtype, np.floating) else 2,
            "zlevel": 9,
        }
    dest.parent.mkdir(parents=True, exist_ok=True)
    with rasterio.open(dest, "w", **profile) as out:
        out.write(data, 1)


def cut_jp2(source: Path, dest: Path, bounds: BBox) -> list[float]:
    """The pixels covering `bounds` (all bands), re-encoded as lossy JPEG 2000.

    Returns the cut's bounds. Fails when the cut is empty.
    """
    with rasterio.open(source) as src:
        snapped = snapped_window(src, bounds)
        if snapped.width <= 0 or snapped.height <= 0:
            raise SystemExit(f"{source} doesn't overlap {bounds.as_list()}")
        data = src.read(window=snapped)
        transform = src.window_transform(snapped)
        profile = {
            "driver": "JP2OpenJPEG",
            "dtype": src.dtypes[0],
            "width": snapped.width,
            "height": snapped.height,
            "count": src.count,
            "crs": src.crs,
            "transform": transform,
            "QUALITY": str(JP2_QUALITY),
        }
        cut_bounds = list(window_bounds(snapped, src.transform))
    dest.parent.mkdir(parents=True, exist_ok=True)
    with rasterio.open(dest, "w", **profile) as out:
        out.write(data)
    return [float(v) for v in cut_bounds]


def cut_laz(source: Path, dest: Path, bounds: BBox) -> int:
    """Every point inside `bounds` (all classes), with the source header. Returns the count."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    count = 0
    with laspy.open(source) as reader:
        header = reader.header
        with laspy.open(dest, mode="w", header=header, do_compress=True) as writer:
            for chunk in reader.chunk_iterator(2_000_000):
                x, y = np.asarray(chunk.x), np.asarray(chunk.y)
                keep = (
                    (x >= bounds.min_x)
                    & (x <= bounds.max_x)
                    & (y >= bounds.min_y)
                    & (y <= bounds.max_y)
                )
                if keep.any():
                    writer.write_points(chunk[keep])
                    count += int(keep.sum())
    return count


def _entry(
    original: Mapping[str, Any], path: Path, rel: str, cut: dict[str, Any]
) -> dict[str, Any]:
    entry = dict(original)
    entry["path"] = rel
    entry["size"] = path.stat().st_size
    entry["sha256"] = sha256_of(path)
    request = dict(original.get("request") or {})
    if isinstance(request.get("result"), Mapping):
        request["result"] = {k: v for k, v in request["result"].items() if k in RESULT_FIELDS}
    entry["request"] = {
        **request,
        "test_area_cut": {
            **cut,
            "source_size": original.get("size"),
            "source_sha256": original.get("sha256"),
        },
    }
    return entry


def _notice(map_id: str, entries: list[dict[str, Any]]) -> str:
    lines = [
        f"# Test-area raw data for `{map_id}`",
        "",
        "Raw data cut to the map's test area, laid out like `tools/mapgen/data/raw/"
        f"{map_id}/`. Used by the tests and to build the test-area map pack.",
        "",
        "## Attribution and licences",
        "",
    ]
    seen: set[tuple[str, str]] = set()
    for entry in entries:
        licence, attribution = str(entry["licence"]), str(entry["attribution"])
        if (licence, attribution) in seen:
            continue
        seen.add((licence, attribution))
        link = LICENCE_LINKS.get(licence)
        licence_text = f"[{licence}]({link})" if link else licence
        extra = f" ({OSM_COPYRIGHT})" if licence == "ODbL 1.0" else ""
        lines.append(f"- {attribution}{extra}. Licence: {licence_text}.")
    licences = {licence for licence, _ in seen}
    lines += ["", "## Modifications", ""]
    cc_by = [attribution for licence, attribution in seen if licence == "CC BY 4.0"]
    if cc_by:
        mml = all(a.startswith(MML_ATTRIBUTION_PREFIX) for a in cc_by)
        subject = "National Land Survey of Finland (MML) data" if mml else "CC BY 4.0 data"
        lines.append(
            f"- The {subject} listed above was modified, as CC BY 4.0 section 3(a)(1)(B) "
            "requires us to state: every file was cut to the test area, and the orthophoto "
            "was also re-encoded (lossy JPEG 2000) to keep it small. The originals are "
            "identified by `source_size` and `source_sha256` in `download-manifest.json`."
        )
    if "ODbL 1.0" in licences:
        lines.append(
            "- The OpenStreetMap extract is a derivative database of the OpenStreetMap "
            "database (cut to the test area, contributor metadata removed). It is offered "
            "under the ODbL 1.0, https://opendatacommons.org/licenses/odbl/1-0/."
        )
    lines += [
        "",
        "## How it was produced",
        "",
        f"`uv run python scripts/cut_test_area.py {map_id}` (in `tools/mapgen/`) cut the "
        "downloaded files listed in `download-manifest.json` to what a test-area build reads: "
        "OSM building footprints whose centroid is in the test area with their ways and nodes, "
        "and the OSM road ways (`highway`), water features, land use areas and points of "
        "interest whose point, line or area meets the test area with all their nodes "
        "(contributor metadata removed), the elevation model around the test area, the laser "
        "scanning points around those footprints, the Topographic Database buildings "
        "(`rakennus`, all columns) that meet the test area, and the orthophoto over the test "
        "area (re-encoded lossy to keep it small). Each manifest entry records the cut bounds "
        "or contents and the checksum of the original file.",
        "",
    ]
    return "\n".join(lines)


def cut_test_area(config: MapConfig, raw_dir: Path, out_dir: Path) -> dict[str, int]:
    """Write the cut files, manifest and notice to `out_dir`. Returns file sizes by path."""
    area = build_area(config, "test")
    loaded: LoadResult = run_load(config, area, raw_dir, log=lambda _: None)
    entries = load_entries(raw_dir)
    world = to_world(area.bounds, config)
    m = DEM_MARGIN_M
    dem_bounds = BBox(world.min_x - m, world.min_y - m, world.max_x + m, world.max_y + m)
    point_bounds_list = loaded.report["point_cloud"]["bounds_world"]
    point_bounds = BBox(*point_bounds_list) if point_bounds_list else None
    footprint_ids = {f.id for f in loaded.footprints}
    road_ids = {f"osm:way/{w.osm_id}" for w in loaded.roads if _intersects(LineString(w.xy), area)}
    water_ids = {
        w.id for w in loaded.water_areas + loaded.water_lines if _intersects(w.geometry, area)
    }
    poi_ids = {p.id for p in loaded.pois if _intersects(p.geometry, area)}
    landuse_ids = {a.id for a in loaded.landuse if _intersects(a.geometry, area)}
    rakennus_ids = {
        int(r.id.removeprefix(RAKENNUS_ID_PREFIX))
        for r in loaded.building_use or []
        if r.id.startswith(RAKENNUS_ID_PREFIX) and _intersects(r.geometry, area)
    }

    if out_dir.exists() and any(out_dir.iterdir()):
        if not (out_dir / MANIFEST_NAME).is_file():
            raise SystemExit(f"{out_dir} is not empty and has no {MANIFEST_NAME}; not replacing")
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    written: list[dict[str, Any]] = []
    for source in sorted(set(loaded.files)):
        rel = source.resolve().relative_to(raw_dir.resolve()).as_posix()
        if rel not in entries:
            raise SystemExit(f"{rel} is not in the download manifest; run `mapgen download`")
        dest = out_dir / rel
        suffix = source.suffix.lower()
        if suffix == ".osm":
            cut_osm(source, dest, footprint_ids | road_ids | water_ids | poi_ids | landuse_ids)
            cut: dict[str, Any] = {
                "contents": OSM_CONTENTS,
                "footprints": len(footprint_ids),
                "highway_ways": len(road_ids),
                "water_features": len(water_ids),
                "landuse_areas": len(landuse_ids),
                "points_of_interest": len(poi_ids),
            }
        elif suffix in GPKG_SUFFIXES:
            records = cut_gpkg(source, dest, RAKENNUS_TABLE, rakennus_ids)
            cut = {"contents": GPKG_CONTENTS, "table": RAKENNUS_TABLE, "records": records}
        elif suffix in TIFF_SUFFIXES:
            cut_geotiff(source, dest, dem_bounds)
            cut = {"bounds": dem_bounds.as_list()}
        elif suffix in LAZ_SUFFIXES:
            if point_bounds is None:
                continue
            points = cut_laz(source, dest, point_bounds)
            if points == 0:
                dest.unlink()
                continue
            cut = {"bounds": point_bounds.as_list(), "points": points}
        elif suffix in JP2_SUFFIXES:
            cut = {"bounds": cut_jp2(source, dest, world), "jp2_quality": JP2_QUALITY}
        else:
            raise SystemExit(f"don't know how to cut {rel}")
        written.append(_entry(entries[rel], dest, rel, cut))

    written.sort(key=lambda e: e["path"])
    generated = max(parse_isoformat(str(e["retrieved_at"])) for e in written)
    document = {
        "schema_version": SCHEMA_VERSION,
        "map_id": config.id,
        "generated_at": generated.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "tool": {"name": "mapgen", "version": "test-area-cut"},
        "files": written,
    }
    (out_dir / MANIFEST_NAME).write_text(
        json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    (out_dir / "NOTICE.md").write_text(_notice(config.id, written), encoding="utf-8")
    return {
        p.relative_to(out_dir).as_posix(): p.stat().st_size
        for p in sorted(out_dir.rglob("*"))
        if p.is_file()
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("map_id", metavar="<map-id>")
    parser.add_argument("--out", type=Path, default=None, help="output folder")
    args = parser.parse_args(argv)
    repo_root = paths.find_repo_root()
    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    raw_dir = paths.raw_dir(repo_root, args.map_id)
    out_dir = args.out or repo_root / FIXTURES_DIR / f"{args.map_id}-test-area"
    sizes = cut_test_area(config, raw_dir, out_dir)
    for rel, size in sizes.items():
        print(f"{size:>10,}  {rel}")
    print(f"{sum(sizes.values()):>10,}  total in {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
