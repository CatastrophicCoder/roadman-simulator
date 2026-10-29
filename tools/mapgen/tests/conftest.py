"""Shared fixtures. No test touches the network: every HTTP call goes to FakeHttp."""

import copy
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pytest
import yaml

from mapgen.config import MapConfig, load_map_config
from mapgen.http import HttpError

REPO_ROOT = Path(__file__).resolve().parents[3]
SOMERO_YAML = REPO_ROOT / "maps" / "somero" / "map.yaml"  # only for the smoke test
FIXTURE_YAML = Path(__file__).resolve().parent / "fixtures" / "maps" / "testtown" / "map.yaml"
FIXED_NOW = datetime(2026, 10, 27, 12, 0, 0, tzinfo=UTC)
FAKE_KEY = "s3cr3t-test-key-0123456789"


def pytest_configure(config: pytest.Config) -> None:
    # Also registered in pyproject.toml; this covers runs from the repo root.
    config.addinivalue_line(
        "markers", "rawdata: builds from the downloaded raw data (skipped when it is missing)"
    )


@dataclass
class Call:
    method: str
    url: str
    body: Any = None
    headers: Mapping[str, str] | None = None
    timeout: float | None = None


Handler = Callable[[Call], Any]


@dataclass
class FakeHttp:
    """Answers requests from `handler`. Return bytes from a download handler to write a file;
    raise HttpError to simulate a failure."""

    handler: Handler
    calls: list[Call] = field(default_factory=list)

    def get_json(self, url: str, *, timeout: float = 60.0) -> Any:
        call = Call("GET", url, timeout=timeout)
        self.calls.append(call)
        return self.handler(call)

    def post_json(self, url: str, body: Any, *, timeout: float = 60.0) -> Any:
        call = Call("POST", url, body=body, timeout=timeout)
        self.calls.append(call)
        return self.handler(call)

    def download(
        self,
        url: str,
        dest: Path,
        *,
        data: bytes | None = None,
        headers: Mapping[str, str] | None = None,
        timeout: float = 60.0,
    ) -> int:
        call = Call("DOWNLOAD", url, body=data, headers=headers, timeout=timeout)
        self.calls.append(call)
        content = self.handler(call)
        assert isinstance(content, bytes)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(content)
        return len(content)


def http_error(status: int, url: str = "https://example.invalid/") -> HttpError:
    return HttpError(url, status, "test")


@pytest.fixture
def fixture_data() -> dict[str, Any]:
    """The frozen test map config (tests/fixtures/maps/testtown/map.yaml) as a dict."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    assert isinstance(data, dict)
    return copy.deepcopy(data)


@dataclass
class TmpRepo:
    root: Path

    def write_map(self, data: Mapping[str, Any], folder: str | None = None) -> Path:
        path = self.root / "maps" / (folder or str(data["id"])) / "map.yaml"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(yaml.safe_dump(dict(data), sort_keys=False), encoding="utf-8")
        return path

    def load(self, data: Mapping[str, Any], folder: str | None = None) -> MapConfig:
        return load_map_config(self.write_map(data, folder))

    def raw_dir(self, map_id: str) -> Path:
        return self.root / "tools" / "mapgen" / "data" / "raw" / map_id


@pytest.fixture
def tmp_repo(tmp_path: Path) -> TmpRepo:
    (tmp_path / "maps").mkdir()
    (tmp_path / "tools" / "mapgen").mkdir(parents=True)
    return TmpRepo(tmp_path)


@pytest.fixture
def fixture_config(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> MapConfig:
    return tmp_repo.load(fixture_data)


# Build fixtures: a synthetic DEM, the hand-made OSM file and a download manifest.

OSM_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "osm" / "testtown.osm"
FIXTURE_ORIGIN = (309657.0, 6726616.0)  # tests/fixtures/maps/testtown/map.yaml
# Hand-counted from tests/fixtures/osm/testtown.osm (see its header comment).
FIXTURE_TEST_AREA_BUILDINGS = 12
PLANE = (100.0, 0.05, 0.02)  # z = a + b * x_local + c * y_local


def plane_z(x: Any, y: Any) -> Any:
    a, b, c = PLANE
    return a + b * x + c * y


def write_plane_dem(
    path: Path,
    origin: tuple[float, float] = FIXTURE_ORIGIN,
    half_size: float = 220.0,
    res: float = 2.0,
    crs: str = "EPSG:3067",
    nodata_cells: tuple[slice, slice] | None = None,
) -> Path:
    """A north-up GeoTIFF of the sloped plane `PLANE` around `origin` (pixel centres sampled).

    The pixel edges are at even local metres, so pixel centres sit at odd local metres
    and never on tile edges.
    """
    import numpy as np
    import rasterio
    from rasterio.transform import from_origin

    ox, oy = origin
    size = int(round(2 * half_size / res))
    left, top = ox - half_size, oy + half_size
    cols = left + (np.arange(size) + 0.5) * res - ox
    rows = top - (np.arange(size) + 0.5) * res - oy
    gx, gy = np.meshgrid(cols, rows)
    data = plane_z(gx, gy).astype("float32")
    nodata = -9999.0
    if nodata_cells is not None:
        data[nodata_cells] = nodata
    path.parent.mkdir(parents=True, exist_ok=True)
    with rasterio.open(
        path,
        "w",
        driver="GTiff",
        width=size,
        height=size,
        count=1,
        dtype="float32",
        crs=crs,
        transform=from_origin(left, top, res, res),
        nodata=nodata,
    ) as dst:
        dst.write(data, 1)
    return path


def write_laz(
    path: Path,
    x: Any,
    y: Any,
    z: Any,
    classification: Any = 5,
    number_of_returns: Any = 1,
    crs: str | None = "EPSG:3067",
) -> Path:
    """A LAZ file (point format 6) of the given world points; `crs=None` writes no CRS.

    `classification` and `number_of_returns` are scalars or per-point arrays; every point
    is the last return of its pulse.
    """
    import laspy
    import numpy as np
    import pyproj

    x, y, z = (np.asarray(a, dtype=np.float64) for a in (x, y, z))
    n = len(x)
    header = laspy.LasHeader(point_format=6, version="1.4")
    if n:
        header.offsets = [float(np.floor(x.min())), float(np.floor(y.min())), 0.0]
    header.scales = [0.001, 0.001, 0.001]
    if crs is not None:
        header.add_crs(pyproj.CRS(crs))
    las = laspy.LasData(header)
    las.x, las.y, las.z = x, y, z
    las.classification = np.broadcast_to(np.asarray(classification, dtype=np.uint8), n).copy()
    returns = np.broadcast_to(np.asarray(number_of_returns, dtype=np.uint8), n).copy()
    las.number_of_returns = returns
    las.return_number = returns
    path.parent.mkdir(parents=True, exist_ok=True)
    las.write(path)
    return path


def lidar_entry(rel: str) -> dict:
    """A download-manifest entry for a lidar file, as the `mml_lidar` adapter writes it."""
    return _entry(
        rel,
        "mml_lidar",
        "Laser scanning data 0,5 p",
        "CC BY 4.0",
        "Contains data from the National Land Survey of Finland Laser scanning data 0,5 p, 10/2026",
    )


def _entry(rel: str, source: str, dataset: str, licence: str, attribution: str) -> dict:
    return {
        "path": rel,
        "source": source,
        "dataset": dataset,
        "request": {},
        "retrieved_at": "2026-10-01T00:00:00Z",
        "size": 0,
        "sha256": "",
        "licence": licence,
        "attribution": attribution,
        "origin": "manual",
    }


def write_fixture_raw(raw_dir: Path, map_id: str = "testtown") -> Path:
    """The raw folder for a fixture build, with a download manifest.

    The manifest also lists an orthophoto file the build never reads, so tests can
    check that only read files are credited.
    """
    import json
    import shutil

    (raw_dir / "osm").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(OSM_FIXTURE, raw_dir / "osm" / f"{map_id}.osm")
    write_plane_dem(raw_dir / "mml" / "dem_2m" / "plane.tif")
    entries = [
        _entry("mml/dem_2m/plane.tif", "mml_dem_2m", "Elevation model 2 m", "CC BY 4.0", "DEM"),
        _entry("mml/orthophoto/x.jp2", "mml_orthophoto", "Orthophoto", "CC BY 4.0", "Ortho"),
        _entry(f"osm/{map_id}.osm", "osm", "OpenStreetMap", "ODbL 1.0", "© OSM contributors"),
    ]
    document = {"schema_version": 1, "map_id": map_id, "files": entries}
    (raw_dir / "download-manifest.json").write_text(json.dumps(document), encoding="utf-8")
    return raw_dir


@dataclass
class TileMeshes:
    """A tile .glb read back: node name -> (local vertices, faces)."""

    nodes: dict[str, tuple[Any, Any]]

    def buildings(self) -> list[str]:
        return [n for n in self.nodes if n.startswith("bld_") and not n.endswith("-colonly")]


def read_tile(path: Path) -> TileMeshes:
    import numpy as np
    import trimesh

    from mapgen.build.export_glb import from_gltf

    scene = trimesh.load(path, file_type="glb", force="scene", process=False)
    nodes: dict[str, tuple[Any, Any]] = {}
    for name in scene.graph.nodes_geometry:
        matrix, geom_name = scene.graph[name]
        mesh = scene.geometry[geom_name]
        vertices = trimesh.transform_points(np.asarray(mesh.vertices), matrix)
        nodes[name] = (from_gltf(vertices), np.asarray(mesh.faces))
    return TileMeshes(nodes)


@dataclass
class FixtureBuild:
    repo: Path
    raw_dir: Path
    pack_dir: Path
    work_dir: Path
    report: Any


@pytest.fixture(scope="session")
def fixture_build(tmp_path_factory: pytest.TempPathFactory) -> FixtureBuild:
    """The testtown test area built once from the fixture data (shared, treat as read-only)."""
    import shutil

    from mapgen.build import run_build

    root = tmp_path_factory.mktemp("repo")
    map_yaml = root / "maps" / "testtown" / "map.yaml"
    map_yaml.parent.mkdir(parents=True)
    shutil.copyfile(FIXTURE_YAML, map_yaml)
    (root / "tools" / "mapgen").mkdir(parents=True)
    raw = write_fixture_raw(root / "tools" / "mapgen" / "data" / "raw" / "testtown")
    pack = root / "game" / "maps" / "testtown"
    work = root / "tools" / "mapgen" / "build" / "testtown" / "test"
    report = run_build(
        load_map_config(map_yaml),
        "test",
        raw,
        pack,
        work,
        now=lambda: FIXED_NOW,
        log=lambda _: None,
    )
    return FixtureBuild(root, raw, pack, work, report)


def mesh_height_at(vertices: Any, faces: Any, points: Any) -> Any:
    """Height of a z-up triangle mesh at 2D points (NaN where no triangle covers a point)."""
    import numpy as np

    v = np.asarray(vertices, dtype=float)
    tri = v[np.asarray(faces)]  # (m, 3, 3)
    a, b, c = tri[:, 0, :2], tri[:, 1, :2], tri[:, 2, :2]
    result = np.full(len(points), np.nan)
    det = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (b[:, 1] - a[:, 1])
    ok = np.abs(det) > 1e-12
    for k, (px, py) in enumerate(np.asarray(points, dtype=float)):
        w1 = (
            (px - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (py - a[:, 1])
        ) / np.where(ok, det, 1.0)
        w2 = (
            (b[:, 0] - a[:, 0]) * (py - a[:, 1]) - (px - a[:, 0]) * (b[:, 1] - a[:, 1])
        ) / np.where(ok, det, 1.0)
        w0 = 1 - w1 - w2
        hit = ok & (w0 >= -1e-9) & (w1 >= -1e-9) & (w2 >= -1e-9)
        if hit.any():
            i = int(np.argmax(hit))
            result[k] = w0[i] * tri[i, 0, 2] + w1[i] * tri[i, 1, 2] + w2[i] * tri[i, 2, 2]
    return result


_GLB_DTYPES = {5121: "u1", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
_GLB_WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def glb_primitive(path: Path, node_name: str) -> dict[str, Any]:
    """The stored arrays of a node's first primitive, read straight from the GLB.

    Keys: the attribute names (e.g. "POSITION", "NORMAL", in glTF axes) and "indices"
    as (m, 3) triangles.
    """
    import json
    import struct

    import numpy as np

    data = path.read_bytes()
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20 : 20 + json_len])
    bin_start = 20 + json_len + 8
    (node,) = [n for n in gltf["nodes"] if n.get("name") == node_name]
    primitive = gltf["meshes"][node["mesh"]]["primitives"][0]

    def accessor(index: int) -> Any:
        acc = gltf["accessors"][index]
        view = gltf["bufferViews"][acc["bufferView"]]
        assert "byteStride" not in view, "strided buffers are not handled by this test helper"
        width = _GLB_WIDTH[acc["type"]]
        start = bin_start + view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        array = np.frombuffer(
            data, dtype=_GLB_DTYPES[acc["componentType"]], count=acc["count"] * width, offset=start
        )
        return array.reshape(acc["count"], width) if width > 1 else array

    result = {name: accessor(index) for name, index in primitive["attributes"].items()}
    result["indices"] = accessor(primitive["indices"]).reshape(-1, 3)
    return result


# The committed Somero test-area fixture, built the way CI builds the test-area pack.

SOMERO_FIXTURE_RAW = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"


@dataclass
class SomeroArea:
    root: Path
    pack: Path
    work: Path
    config: MapConfig


def build_somero_fixture(root: Path) -> SomeroArea:
    """`mapgen build somero --area test` in a temporary repo, with the committed fixture
    copied in as `data/raw/somero/` (as CI does) and a copy of `maps/somero/`."""
    import shutil

    from mapgen import paths
    from mapgen.cli import EXIT_OK, main

    map_id = SOMERO_YAML.parent.name
    shutil.copytree(SOMERO_YAML.parent, paths.map_dir(root, map_id))
    shutil.copytree(SOMERO_FIXTURE_RAW, paths.raw_dir(root, map_id))
    assert main(["--repo-root", str(root), "build", map_id, "--area", "test"]) == EXIT_OK
    return SomeroArea(
        root,
        paths.pack_dir(root, map_id),
        paths.work_dir(root, map_id, "test"),
        load_map_config(paths.map_config_path(root, map_id)),
    )


@pytest.fixture(scope="session")
def somero_area(tmp_path_factory: pytest.TempPathFactory) -> SomeroArea:
    """The Somero test area built once from the committed fixture (treat as read-only)."""
    return build_somero_fixture(tmp_path_factory.mktemp("somero-area"))
