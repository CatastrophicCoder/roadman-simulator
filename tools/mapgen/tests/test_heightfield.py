import numpy as np
import pytest
from rasterio.transform import Affine
from shapely.geometry import Polygon, box

from conftest import mesh_height_at
from mapgen.build.area import BuildArea
from mapgen.build.heightfield import Heightfield, fill_nodata, heightfield_from_raster
from mapgen.build.terrain import MeshTerrainBuilder
from mapgen.geo import BBox
from mapgen.sources.base import ElevationRaster


def plane_field() -> Heightfield:
    xs = np.arange(-10, 11) * 2.0
    ys = np.arange(-10, 11) * 2.0
    gx, gy = np.meshgrid(xs, ys)
    return Heightfield(z=10 + 0.5 * gx - 0.25 * gy, x0=-20.0, y0=-20.0, dx=2.0, dy=2.0)


def bumpy_field() -> Heightfield:
    rng = np.random.default_rng(7)
    return Heightfield(z=rng.uniform(0, 5, (30, 30)), x0=0.0, y0=0.0, dx=2.0, dy=2.0)


def test_bilinear_sample_is_exact_on_a_plane() -> None:
    hf = plane_field()
    x = np.array([-3.3, 0.0, 7.1, 19.0])
    y = np.array([4.4, 0.0, -9.9, 1.0])
    assert np.allclose(hf.sample(x, y), 10 + 0.5 * x - 0.25 * y)


def test_sample_clamps_outside_the_grid() -> None:
    hf = plane_field()
    assert hf.sample(-100.0, 0.0) == pytest.approx(hf.sample(-20.0, 0.0))
    assert hf.sample(0.0, 500.0) == pytest.approx(hf.sample(0.0, 20.0))


def test_min_under_is_at_or_below_the_surface_and_close_to_it() -> None:
    hf = plane_field()
    footprint = Polygon([(-3.1, -2.7), (5.2, -2.1), (4.4, 6.3), (-2.2, 5.0)])
    base = hf.min_under(footprint)
    xs, ys = np.asarray(footprint.exterior.coords).T
    exact = float(np.min(10 + 0.5 * xs - 0.25 * ys))  # a plane's minimum is at a vertex
    assert base <= exact
    assert base >= exact - (0.5 + 0.25) * 2.0  # at most one cell's rise lower


def test_min_under_bounds_the_terrain_mesh_on_rough_ground() -> None:
    hf = bumpy_field()
    footprint = box(13.3, 17.1, 27.9, 24.6)
    base = hf.min_under(footprint)
    chunk = MeshTerrainBuilder().build_tile(
        hf, BuildArea("test", BBox(0, 0, 58, 58), 64.0).tiles()[0]
    )
    rng = np.random.default_rng(1)
    pts = rng.uniform([13.3, 17.1], [27.9, 24.6], (300, 2))
    assert np.all(hf.sample(pts[:, 0], pts[:, 1]) >= base - 1e-9)
    mesh_z = mesh_height_at(chunk.vertices, chunk.faces, pts)
    assert not np.isnan(mesh_z).any()
    assert np.all(mesh_z >= base - 1e-9)


def test_min_under_outside_the_grid_uses_edge_cells() -> None:
    hf = plane_field()
    base = hf.min_under(box(30, -1, 40, 1))  # east of the grid: clamped to the east edge
    assert base <= float(hf.sample(20.0, 1.0)) + 1e-9


def test_fill_nodata_uses_neighbours() -> None:
    data = np.full((4, 4), 5.0)
    data[1:3, 1:3] = np.nan
    filled, count = fill_nodata(data)
    assert count == 4
    assert np.allclose(filled, 5.0)


def test_fill_nodata_rejects_empty_data() -> None:
    with pytest.raises(ValueError, match="no valid cells"):
        fill_nodata(np.full((3, 3), np.nan))


def test_heightfield_from_raster_places_nodes_at_pixel_centres() -> None:
    data = np.array([[3.0, 4.0], [1.0, 2.0]])  # north row first
    raster = ElevationRaster(data, Affine(2, 0, 1000, 0, -2, 2004), "EPSG:3067")
    hf, filled = heightfield_from_raster(raster, origin_x=1000, origin_y=2000)
    assert filled == 0
    assert (hf.x0, hf.y0) == (1.0, 1.0)
    assert hf.sample(1.0, 1.0) == pytest.approx(1.0)  # south-west pixel
    assert hf.sample(3.0, 3.0) == pytest.approx(4.0)  # north-east pixel


def test_heightfield_rejects_nan() -> None:
    with pytest.raises(ValueError):
        Heightfield(z=np.array([[1.0, np.nan], [1.0, 1.0]]), x0=0, y0=0, dx=1, dy=1)
