"""Maanmittauslaitos (National Land Survey of Finland) open data file service.

Uses the OGC API Processes interface: execute a process, poll the job, list the
results and download each file. The API key comes only from the `MML_API_KEY`
environment variable and is never written to a file, log, manifest or error message.

Without a key, each MML source prints the manual download steps for MML's
Karttapaikka file service and registers any files already in its raw folder.

For builds, the DEM adapter loads its GeoTIFFs (mosaicked and warped to the map CRS),
the lidar adapter loads the non-ground points of its LAZ files, the orthophoto adapter
lists the image files that overlap the area (the build reads them per tile for the debug
textures), and the 3D buildings adapter is a stub until its file format is confirmed.
"""

import re
import shutil
import urllib.parse
from abc import abstractmethod
from collections.abc import Callable, Mapping
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import Any, ClassVar, Literal

import laspy
import numpy as np
import rasterio
from pydantic import Field, field_validator, model_validator
from pyproj import CRS as ProjCRS
from rasterio.crs import CRS as RasterioCRS
from rasterio.enums import Resampling
from rasterio.errors import RasterioError
from rasterio.io import DatasetReader
from rasterio.merge import merge
from rasterio.transform import Affine
from rasterio.vrt import WarpedVRT
from rasterio.warp import transform_bounds

from mapgen import tm35
from mapgen.geo import BBox, projected_bbox
from mapgen.http import HttpError, redact, with_query_param
from mapgen.sources.base import (
    BUILDINGS_3D,
    ELEVATION,
    IMAGERY,
    POINT_CLOUD,
    AdapterOptions,
    DownloadContext,
    DownloadedFile,
    ElevationRaster,
    Imagery,
    LoadContext,
    PointCloud,
    SourceAdapter,
    SourceData,
    SourceError,
    SourceLoadError,
)

DEFAULT_BASE_URL = "https://avoin-paikkatieto.maanmittauslaitos.fi/tiedostopalvelu/ogcproc/v1/"
KARTTAPAIKKA_URL = "https://asiointi.maanmittauslaitos.fi/karttapaikka/tiedostopalvelu"
API_KEY_ENV = "MML_API_KEY"
API_KEY_PARAM = "api-key"
MML_CRS = "EPSG:3067"
MML_DOMAIN = "maanmittauslaitos.fi"
LASER_SCANNING_PREFIX = "laserkeilausaineisto"
# The only open laser scanning process: laser scanning data 0.5 p by map sheet.
LIDAR_05P_PROCESS = "laserkeilausaineisto_05_karttalehti"

PENDING_STATUSES = frozenset({"accepted", "running"})
FAILED_STATUSES = frozenset({"failed", "dismissed"})


def ensure_allowed_process(process_id: str) -> str:
    """Refuse every MML laser scanning process except laser scanning data 0.5 p.

    ADR 0005 (amended): the 0.5 p data is open (CC BY 4.0) and allowed through exactly
    `laserkeilausaineisto_05_karttalehti`. The 5 p data isn't open, so any other process id
    starting with `laserkeilausaineisto` (every 5 p process included) is refused.
    """
    normalised = process_id.strip().lower()
    if normalised.startswith(LASER_SCANNING_PREFIX) and normalised != LIDAR_05P_PROCESS:
        raise ValueError(
            f"process {process_id!r} is MML laser scanning data, which this pipeline must not "
            f"use; only {LIDAR_05P_PROCESS!r} (laser scanning data 0.5 p) is allowed"
        )
    return process_id


def mml_bbox(ctx: DownloadContext) -> BBox:
    """The map bbox in EPSG:3067, rounded outward to whole metres (MML is always TM35FIN)."""
    box = ctx.map_config.bbox_wgs84
    return projected_bbox(
        box.lon_min, box.lat_min, box.lon_max, box.lat_max, MML_CRS
    ).rounded_outward()


def _int_list(bbox: BBox) -> list[int]:
    return [int(v) for v in bbox.as_list()]


class MmlOptions(AdapterOptions):
    base_url: str = DEFAULT_BASE_URL
    process_id: str | None = None
    request_timeout_s: float = Field(default=60.0, gt=0)
    download_timeout_s: float = Field(default=600.0, gt=0)
    poll_interval_s: float = Field(default=5.0, gt=0)
    poll_max_interval_s: float = Field(default=60.0, gt=0)
    job_timeout_s: float = Field(default=3600.0, gt=0)

    @field_validator("process_id")
    @classmethod
    def _allowed(cls, value: str | None) -> str | None:
        return None if value is None else ensure_allowed_process(value)

    @field_validator("base_url")
    @classmethod
    def _trailing_slash(cls, value: str) -> str:
        return value if value.endswith("/") else value + "/"


class MmlClient:
    """One MML OGC API Processes session for a single source."""

    def __init__(self, ctx: DownloadContext, options: MmlOptions, api_key: str) -> None:
        self._ctx = ctx
        self._options = options
        self._key = api_key
        self._base_host = urllib.parse.urlsplit(options.base_url).hostname or ""

    def scrub(self, text: str) -> str:
        return redact(text).replace(self._key, "***") if self._key else redact(text)

    def fail(self, message: str) -> SourceError:
        return SourceError(self.scrub(f"[{self._ctx.source_name}] {message}"))

    def _url(self, relative: str) -> str:
        url = urllib.parse.urljoin(self._options.base_url, relative)
        return with_query_param(url, API_KEY_PARAM, self._key)

    def describe(self, process_id: str) -> Any:
        ensure_allowed_process(process_id)
        url = self._url(f"processes/{urllib.parse.quote(process_id)}")
        return self._ctx.http.get_json(url, timeout=self._options.request_timeout_s)

    def execute(self, process_id: str, inputs: Mapping[str, Any]) -> str:
        ensure_allowed_process(process_id)
        url = self._url(f"processes/{urllib.parse.quote(process_id)}/execution")
        body = {"id": process_id, "inputs": dict(inputs)}
        try:
            response = self._ctx.http.post_json(url, body, timeout=self._options.request_timeout_s)
        except HttpError as exc:
            raise self.fail(f"starting process {process_id} failed: {exc}") from None
        if not isinstance(response, dict):
            raise self.fail(f"unexpected execution response: {response!r:.300}")
        for key in ("jobID", "jobId", "id"):
            job_id = response.get(key)
            if isinstance(job_id, str | int) and str(job_id):
                return str(job_id)
        raise self.fail(f"execution response has no jobID (keys: {sorted(response)})")

    def wait(self, job_id: str) -> None:
        ctx, options = self._ctx, self._options
        url = self._url(f"jobs/{urllib.parse.quote(job_id)}")
        start = ctx.monotonic()
        interval = options.poll_interval_s
        last_status = ""
        while True:
            try:
                response = ctx.http.get_json(url, timeout=options.request_timeout_s)
            except HttpError as exc:
                raise self.fail(f"polling job {job_id} failed: {exc}") from None
            if not isinstance(response, dict) or not isinstance(response.get("status"), str):
                raise self.fail(f"job {job_id}: unexpected status response {response!r:.300}")
            status = response["status"].lower()
            if status == "successful":
                return
            if status in FAILED_STATUSES:
                detail = response.get("message") or response.get("detail") or ""
                raise self.fail(f"job {job_id} {status}: {detail}".rstrip(": "))
            if status not in PENDING_STATUSES:
                raise self.fail(f"job {job_id}: unknown status {response['status']!r}")
            if status != last_status:
                ctx.log(f"[{ctx.source_name}] job {job_id} {status}")
                last_status = status
            if ctx.monotonic() - start + interval > options.job_timeout_s:
                raise self.fail(
                    f"job {job_id} did not finish within {options.job_timeout_s:.0f} s "
                    f"(last status: {status})"
                )
            ctx.sleep(interval)
            interval = min(interval * 1.5, options.poll_max_interval_s)

    def results(self, job_id: str) -> list[dict[str, Any]]:
        url = self._url(f"jobs/{urllib.parse.quote(job_id)}/results")
        try:
            response = self._ctx.http.get_json(url, timeout=self._options.request_timeout_s)
        except HttpError as exc:
            raise self.fail(f"listing results of job {job_id} failed: {exc}") from None
        items: Any = response.get("results") if isinstance(response, dict) else None
        if isinstance(items, dict):
            items = list(items.values())
        if not isinstance(items, list):
            raise self.fail(f"job {job_id}: results response has no 'results' list")
        results: list[dict[str, Any]] = []
        for item in items:
            if isinstance(item, dict) and set(item) == {"zipPath"}:
                continue  # the all-files ZIP link; the files are listed individually
            if not isinstance(item, dict) or not isinstance(item.get("path"), str):
                raise self.fail(f"job {job_id}: result entry without a 'path': {item!r:.300}")
            results.append(item)
        if not results:
            raise self.fail(f"job {job_id} finished but returned no files")
        return results

    def download(self, path: str, dest: Path) -> int:
        """Download a result file. The key is sent only to MML hosts, and only when needed."""
        url = urllib.parse.urljoin(self._options.base_url, quote_result_path(path))
        host = urllib.parse.urlsplit(url).hostname or ""
        is_mml = host == MML_DOMAIN or host.endswith("." + MML_DOMAIN)
        timeout = self._options.download_timeout_s
        has_key = API_KEY_PARAM in urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
        first = with_query_param(url, API_KEY_PARAM, self._key) if host == self._base_host else url
        try:
            return self._ctx.http.download(first, dest, timeout=timeout)
        except HttpError as exc:
            if is_mml and first == url and not has_key and exc.status in (401, 403):
                try:
                    keyed = with_query_param(url, API_KEY_PARAM, self._key)
                    return self._ctx.http.download(keyed, dest, timeout=timeout)
                except HttpError as retry_exc:
                    raise self.fail(f"downloading {url} failed: {retry_exc}") from None
            raise self.fail(f"downloading {url} failed: {exc}") from None


_URL_SAFE = ":/?#[]@!$&'()*+,;=%~"


def quote_result_path(path: str) -> str:
    """Percent-quote spaces, control and non-ASCII characters; keep URL syntax and escapes."""
    return urllib.parse.quote(path, safe=_URL_SAFE)


def scrub_value(value: Any, scrub: Callable[[str], str]) -> Any:
    """Apply `scrub` to every string inside a JSON-like value."""
    if isinstance(value, str):
        return scrub(value)
    if isinstance(value, dict):
        return {k: scrub_value(v, scrub) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub_value(v, scrub) for v in value]
    return value


_UNSAFE_NAME_CHARS = re.compile(r"[^A-Za-z0-9._-]+")


def result_filename(result: Mapping[str, Any], index: int) -> str:
    """A safe local file name for a result: its URL basename, or sheet number and format."""
    raw = PurePosixPath(urllib.parse.unquote(urllib.parse.urlsplit(result["path"]).path)).name
    name = _UNSAFE_NAME_CHARS.sub("_", raw.strip()).strip("._")
    if name:
        return name
    stem = _UNSAFE_NAME_CHARS.sub("_", str(result.get("mapSheetNumber") or f"result-{index}"))
    fmt = _UNSAFE_NAME_CHARS.sub("_", str(result.get("format") or "").lower()).strip("._")
    fmt = fmt or "bin"
    return f"{stem}.{fmt}"


def public_url(url: str) -> str:
    """The URL without its query string, safe to store in the manifest."""
    parts = urllib.parse.urlsplit(quote_result_path(url))
    return urllib.parse.urlunsplit(parts._replace(query="", fragment=""))


def list_files(folder: Path) -> list[Path]:
    if not folder.is_dir():
        return []
    return sorted(
        p
        for p in folder.rglob("*")
        if p.is_file()
        and not p.name.endswith(".part")
        and not any(part.startswith(".") for part in p.relative_to(folder).parts)
    )


def swap_in(staging: Path, target: Path) -> None:
    """Replace `target` with `staging` (both folders), removing the old contents."""
    old = target.with_name(f".{target.name}.old")
    shutil.rmtree(old, ignore_errors=True)
    if target.exists():
        target.rename(old)
    staging.rename(target)
    shutil.rmtree(old, ignore_errors=True)


class MmlProcessAdapter(SourceAdapter):
    """Shared flow for MML processes. Subclasses define the inputs and manual instructions."""

    licence = "CC BY 4.0"
    Options: ClassVar[type[AdapterOptions]] = MmlOptions
    required_env: ClassVar[tuple[str, ...]] = (API_KEY_ENV,)
    dataset_name: ClassVar[str]
    product_name: ClassVar[str]
    layer: ClassVar[str]
    default_process_id: ClassVar[str]

    def attribution(self, retrieved_at: datetime) -> str:
        return (
            "Contains data from the National Land Survey of Finland "
            f"{self.dataset_name}, {retrieved_at:%m/%Y}"
        )

    def process_id(self, options: MmlOptions) -> str:
        return ensure_allowed_process(options.process_id or self.default_process_id)

    @abstractmethod
    def job_inputs(self, ctx: DownloadContext) -> list[dict[str, Any]]:
        """The `inputs` of each job to run (usually one)."""

    @abstractmethod
    def manual_area(self, ctx: DownloadContext) -> list[str]:
        """Lines describing the area or sheets for a manual download."""

    def target_dir(self, ctx: DownloadContext) -> Path:
        return ctx.raw_dir / "mml" / self.layer

    def _register_existing(self, ctx: DownloadContext, files: list[Path]) -> list[DownloadedFile]:
        return [ctx.existing_file(path, self, self.dataset_name) for path in files]

    def _print_manual_steps(self, ctx: DownloadContext, target: Path) -> None:
        lines = [
            f"[{ctx.source_name}] {API_KEY_ENV} is not set, so {self.dataset_name} is not "
            "downloaded automatically.",
            f"  Manual step: open the MML Karttapaikka file service {KARTTAPAIKKA_URL}",
            f"  Product: {self.product_name}",
            *(f"  {line}" for line in self.manual_area(ctx)),
            f"  Save the files into: {ctx.display_path(target)}/",
            "  Then run the download command again to register them in the manifest,",
            f"  or set {API_KEY_ENV} to download automatically.",
        ]
        for line in lines:
            ctx.log(line)

    def describe_inputs(self, ctx: DownloadContext, client: MmlClient, process_id: str) -> None:
        """Log the process input description (helps verify input names and types)."""
        try:
            description = client.describe(process_id)
        except HttpError as exc:
            ctx.log(
                f"[{ctx.source_name}] could not describe {process_id}: {client.scrub(str(exc))}"
            )
            return
        inputs = description.get("inputs") if isinstance(description, dict) else None
        if isinstance(inputs, dict):
            for name, spec in inputs.items():
                schema = spec.get("schema") if isinstance(spec, dict) else None
                ctx.log(f"[{ctx.source_name}]   input {name}: {client.scrub(str(schema))[:300]}")
        else:
            text = client.scrub(str(description))[:500]
            ctx.log(f"[{ctx.source_name}] {process_id} description: {text}")

    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        options = ctx.options
        assert isinstance(options, MmlOptions)
        target = self.target_dir(ctx)
        existing = list_files(target)
        api_key = ctx.env.get(API_KEY_ENV, "").strip()

        if not api_key:
            try:
                self._print_manual_steps(ctx, target)
            except ValueError as exc:  # e.g. the bbox is outside the TM35 sheet grid
                raise SourceError(f"[{ctx.source_name}] {exc}") from None
            if existing:
                ctx.log(f"[{ctx.source_name}] registering {len(existing)} file(s) found there")
            return self._register_existing(ctx, existing)

        if existing and not ctx.force:
            ctx.log(
                f"[{ctx.source_name}] {ctx.display_path(target)} already has {len(existing)} "
                "file(s), skipping download (use --force)"
            )
            return self._register_existing(ctx, existing)

        process_id = self.process_id(options)
        client = MmlClient(ctx, options, api_key)
        try:
            jobs = self.job_inputs(ctx)
        except ValueError as exc:
            raise client.fail(str(exc)) from None
        self.before_execute(ctx, client, process_id)

        # Download into a sibling staging folder and swap it in only when every job has
        # succeeded, so a failed --force run leaves the previous files and manifest intact.
        staging = target.with_name(f".{target.name}.partial")
        shutil.rmtree(staging, ignore_errors=True)
        staging.mkdir(parents=True)
        downloaded: list[tuple[str, dict[str, Any], datetime]] = []
        try:
            for inputs in jobs:
                ctx.log(f"[{ctx.source_name}] starting {process_id}")
                job_id = client.execute(process_id, inputs)
                client.wait(job_id)
                for index, result in enumerate(client.results(job_id)):
                    name = result_filename(result, index)
                    ctx.log(f"[{ctx.source_name}] downloading {name}")
                    client.download(result["path"], staging / name)
                    meta = {k: v for k, v in result.items() if k != "path"}
                    params = {
                        "service": options.base_url,
                        "process_id": process_id,
                        "inputs": inputs,
                        "job_id": job_id,
                        "result": {"url": public_url(result["path"]), **meta},
                    }
                    downloaded.append((name, scrub_value(params, client.scrub), ctx.now()))
            swap_in(staging, target)
        finally:
            shutil.rmtree(staging, ignore_errors=True)

        return [
            ctx.record(
                target / name,
                dataset=self.dataset_name,
                request_params=params,
                retrieved_at=retrieved_at,
                licence=self.licence,
                attribution=self.attribution(retrieved_at),
                origin="downloaded",
            )
            for name, params, retrieved_at in downloaded
        ]

    def before_execute(self, ctx: DownloadContext, client: MmlClient, process_id: str) -> None:
        """Hook run once before the jobs start."""


# Processes that take a bounding box in EPSG:3067.


class MmlBBoxOptions(MmlOptions):
    file_format: str


class MmlBBoxAdapter(MmlProcessAdapter):
    def extra_inputs(self, options: MmlBBoxOptions) -> dict[str, Any]:
        return {}

    def job_inputs(self, ctx: DownloadContext) -> list[dict[str, Any]]:
        options = ctx.options
        assert isinstance(options, MmlBBoxOptions)
        return [
            {
                "boundingBoxInput": _int_list(mml_bbox(ctx)),
                **self.extra_inputs(options),
                "fileFormatInput": options.file_format,
            }
        ]

    def manual_area(self, ctx: DownloadContext) -> list[str]:
        box = _int_list(mml_bbox(ctx))
        options = ctx.options
        assert isinstance(options, MmlBBoxOptions)
        return [
            f"Format: {options.file_format}",
            f"Area in EPSG:3067 (min E, min N, max E, max N): {', '.join(map(str, box))}",
        ]


class Dem2mOptions(MmlBBoxOptions):
    file_format: str = "TIFF"


GEOTIFF_SUFFIXES = frozenset({".tif", ".tiff"})


def _snap_outward(bounds: BBox, transform: Affine) -> tuple[float, float, float, float]:
    """`bounds` grown to whole pixels of `transform`'s grid, so cropping doesn't resample."""
    res_x, res_y = abs(transform.a), abs(transform.e)
    left = transform.c + np.floor((bounds.min_x - transform.c) / res_x) * res_x
    right = transform.c + np.ceil((bounds.max_x - transform.c) / res_x) * res_x
    top = transform.f - np.floor((transform.f - bounds.max_y) / res_y) * res_y
    bottom = transform.f - np.ceil((transform.f - bounds.min_y) / res_y) * res_y
    return (float(left), float(bottom), float(right), float(top))


def same_raster_crs(crs: RasterioCRS, target: str) -> bool:
    """True when a raster's CRS is equivalent to `target`, so no warp is needed.

    GeoTIFF CRS definitions often differ textually from the EPSG definition (names,
    axis order), so compare semantically: rasterio equality, EPSG code, then pyproj.
    """
    target_crs = RasterioCRS.from_user_input(target)
    if crs == target_crs:
        return True
    epsg = crs.to_epsg()
    if epsg is not None and epsg == target_crs.to_epsg():
        return True
    return ProjCRS.from_wkt(crs.to_wkt()).equals(
        ProjCRS.from_user_input(target), ignore_axis_order=True
    )


def read_dem(files: list[Path], target_crs: str, bounds: BBox) -> ElevationRaster:
    """Mosaic GeoTIFF DEM files, warp them to `target_crs` if needed, and crop to `bounds`.

    `bounds` are in `target_crs` and are grown to whole pixels. Each file's nodata value
    becomes NaN; areas outside every file are NaN too.
    """
    if not files:
        raise SourceLoadError("no DEM files to read")
    datasets: list[DatasetReader | WarpedVRT] = []
    opened: list[DatasetReader] = []
    warped: list[str] = []
    try:
        for path in files:
            src = rasterio.open(path)
            opened.append(src)
            if src.crs is None:
                raise SourceLoadError(f"{path} has no CRS")
            if not same_raster_crs(src.crs, target_crs):
                warped.append(src.crs.to_string())
                datasets.append(WarpedVRT(src, crs=target_crs, resampling=Resampling.bilinear))
            else:
                datasets.append(src)
        first = datasets[0]
        if first.transform.b != 0 or first.transform.d != 0 or first.transform.e >= 0:
            raise SourceLoadError(f"{files[0]}: only north-up rasters are supported")
        crop = _snap_outward(bounds, first.transform)
        data, transform = merge(
            datasets,
            bounds=crop,
            res=(abs(first.transform.a), abs(first.transform.e)),
            nodata=np.nan,
            dtype="float64",
            indexes=[1],
        )
    finally:
        for dataset in datasets:
            if isinstance(dataset, WarpedVRT):
                dataset.close()
        for src in opened:
            src.close()
    return ElevationRaster(
        data=data[0], transform=transform, crs=target_crs, warped_from=tuple(sorted(set(warped)))
    )


class MmlDem2mAdapter(MmlBBoxAdapter):
    name = "mml_dem_2m"
    Options = Dem2mOptions
    dataset_name = "Elevation model 2 m"
    product_name = "Korkeusmalli 2 m (Elevation model 2 m)"
    layer = "dem_2m"
    default_process_id = "korkeusmalli_2m_bbox"
    provides = frozenset({ELEVATION})

    def load(self, ctx: LoadContext) -> SourceData:
        folder = ctx.raw_dir / "mml" / self.layer
        files = [p for p in list_files(folder) if p.suffix.lower() in GEOTIFF_SUFFIXES]
        if not files:
            raise SourceLoadError(
                f"[{ctx.source_name}] no GeoTIFF files in {folder}; run `mapgen download "
                f"{ctx.map_config.id} --only {ctx.source_name}`"
            )
        try:
            raster = read_dem(files, ctx.target_crs, ctx.bounds)
        except RasterioError as exc:
            raise SourceLoadError(f"[{ctx.source_name}] cannot read the DEM: {exc}") from exc
        return SourceData(files=files, elevation=raster)


class TopographicOptions(MmlBBoxOptions):
    file_format: str = "GPKG"
    theme: str = "maastotietokanta_kaikki"


class MmlTopographicAdapter(MmlBBoxAdapter):
    name = "mml_topographic"
    Options = TopographicOptions
    dataset_name = "Topographic Database"
    product_name = "Maastotietokanta (Topographic Database)"
    layer = "topographic"
    default_process_id = "maastotietokanta_bbox"

    def extra_inputs(self, options: MmlBBoxOptions) -> dict[str, Any]:
        assert isinstance(options, TopographicOptions)
        return {"themeInput": options.theme}

    def manual_area(self, ctx: DownloadContext) -> list[str]:
        options = ctx.options
        assert isinstance(options, TopographicOptions)
        return [f"Theme: {options.theme}", *super().manual_area(ctx)]


# Processes that take TM35 map sheet names.


class MmlSheetOptions(MmlOptions):
    # Verified from MML's process descriptions: the orthophoto and 3D buildings processes
    # take a list of up to 100 TM35 sheets in one job (orthophoto 1:10 000; 3D buildings
    # accepts sheet names but has no Somero coverage), lidar 0.5 p takes 1:5 000 sheets.
    sheet_scale: int = 10_000
    map_sheet_input: Literal["list", "single"] = "list"
    describe_process: bool = True

    @field_validator("sheet_scale")
    @classmethod
    def _known_scale(cls, value: int) -> int:
        tm35.sheet_size(value)
        return value


class MmlSheetAdapter(MmlProcessAdapter):
    def sheets(self, ctx: DownloadContext) -> list[str]:
        options = ctx.options
        assert isinstance(options, MmlSheetOptions)
        return tm35.sheets_covering(mml_bbox(ctx), options.sheet_scale)

    def extra_inputs(self, options: MmlSheetOptions) -> dict[str, Any]:
        return {}

    def job_inputs(self, ctx: DownloadContext) -> list[dict[str, Any]]:
        options = ctx.options
        assert isinstance(options, MmlSheetOptions)
        sheets = self.sheets(ctx)
        sheet_values: list[Any] = [sheets] if options.map_sheet_input == "list" else list(sheets)
        return [{"mapSheetInput": value, **self.extra_inputs(options)} for value in sheet_values]

    def manual_area(self, ctx: DownloadContext) -> list[str]:
        options = ctx.options
        assert isinstance(options, MmlSheetOptions)
        return [f"Map sheets (TM35, 1:{options.sheet_scale}): {', '.join(self.sheets(ctx))}"]

    def before_execute(self, ctx: DownloadContext, client: MmlClient, process_id: str) -> None:
        options = ctx.options
        assert isinstance(options, MmlSheetOptions)
        if options.describe_process:
            self.describe_inputs(ctx, client, process_id)


LATEST_YEAR = 9999  # MML yearInput: 9999 returns the latest year, 0 every year


# The debug orthophoto textures' resolution in metres per pixel: the source's 0.5 m down
# to 1.0 m (a quarter of the pixels).
DEBUG_TEXTURE_MIN_M_PER_PX = 0.5
DEBUG_TEXTURE_MAX_M_PER_PX = 1.0
IMAGE_SUFFIXES = frozenset({".jp2", ".tif", ".tiff"})


class OrthophotoOptions(MmlSheetOptions):
    year: int = LATEST_YEAR
    file_format: str = "JPEG2000"
    dataset: str = "ortokuva"
    # Build only: resolution of the per-tile debug textures (`debug/orthophoto/`).
    debug_texture_m_per_px: float = Field(
        default=DEBUG_TEXTURE_MIN_M_PER_PX,
        ge=DEBUG_TEXTURE_MIN_M_PER_PX,
        le=DEBUG_TEXTURE_MAX_M_PER_PX,
    )


def raster_bounds_in(path: Path, target_crs: str) -> BBox:
    """A raster file's bounds in `target_crs` (transformed when its CRS differs)."""
    with rasterio.open(path) as src:
        if src.crs is None:
            raise SourceLoadError(f"{path} has no CRS")
        bounds = tuple(src.bounds)
        if not same_raster_crs(src.crs, target_crs):
            bounds = transform_bounds(src.crs, target_crs, *bounds, densify_pts=21)
    return BBox(*bounds)


class MmlOrthophotoAdapter(MmlSheetAdapter):
    name = "mml_orthophoto"
    Options = OrthophotoOptions
    dataset_name = "Orthophoto"
    product_name = "Ortokuva (Orthophoto)"
    layer = "orthophoto"
    default_process_id = "ortoilmakuva_karttalehti"
    provides = frozenset({IMAGERY})

    def load(self, ctx: LoadContext) -> SourceData:
        """The image files that overlap `ctx.bounds`; the build reads them per tile.

        Missing files are not an error: the orthophoto only feeds the debug textures, so
        the build goes on without them (`imagery` is None).
        """
        options = ctx.options
        assert isinstance(options, OrthophotoOptions)
        folder = ctx.raw_dir / "mml" / self.layer
        files = [p for p in list_files(folder) if p.suffix.lower() in IMAGE_SUFFIXES]
        if not files:
            ctx.log(
                f"[{ctx.source_name}] no orthophoto files in {folder}; building without "
                f"debug orthophoto textures (run `mapgen download {ctx.map_config.id} "
                f"--only {ctx.source_name}` to add them)"
            )
            return SourceData()
        overlapping: list[Path] = []
        for path in files:
            try:
                bounds = raster_bounds_in(path, ctx.target_crs)
            except (RasterioError, OSError) as exc:
                raise SourceLoadError(f"[{ctx.source_name}] cannot read {path}: {exc}") from exc
            if bounds.intersects(ctx.bounds):
                overlapping.append(path)
        return SourceData(
            files=overlapping,
            imagery=Imagery(tuple(overlapping), options.debug_texture_m_per_px),
        )

    def extra_inputs(self, options: MmlSheetOptions) -> dict[str, Any]:
        assert isinstance(options, OrthophotoOptions)
        return {
            "fileFormatInput": options.file_format,
            "dataSetInput": options.dataset,
            "yearInput": options.year,
        }

    def manual_area(self, ctx: DownloadContext) -> list[str]:
        options = ctx.options
        assert isinstance(options, OrthophotoOptions)
        latest = " (latest)" if options.year == LATEST_YEAR else ""
        return [
            f"Format: {options.file_format}, year {options.year}{latest}",
            *super().manual_area(ctx),
        ]


class Buildings3dOptions(MmlSheetOptions):
    file_format: str | None = None


class MmlBuildings3dAdapter(MmlSheetAdapter):
    name = "mml_buildings_3d"
    Options = Buildings3dOptions
    dataset_name = "Buildings 3D"
    product_name = "3D-rakennukset (3D buildings)"
    layer = "buildings_3d"
    default_process_id = "3d-rakennukset_karttalehti"
    provides = frozenset({BUILDINGS_3D})

    def load(self, ctx: LoadContext) -> SourceData:
        """Stub: returns no buildings and reads no files.

        The file format of MML's 3D buildings (delivery format, id field and how the
        height is stored) isn't confirmed yet (ADR 0005), so there is nothing to parse
        reliably. Once it is, this returns footprints with `measured_height_m` set, and
        the build already uses those heights first.
        """
        ctx.log(f"[{ctx.source_name}] 3D buildings reader not implemented yet; no heights used")
        return SourceData()

    def extra_inputs(self, options: MmlSheetOptions) -> dict[str, Any]:
        assert isinstance(options, Buildings3dOptions)
        return {} if options.file_format is None else {"fileFormatInput": options.file_format}


# Laser scanning data 0.5 p (LAZ point clouds on TM35 1:5 000 sheets).

LAZ_SUFFIXES = frozenset({".laz", ".las"})
# Classes dropped on load (spec docs/specs/building-heights.md, ASPRS LAS 1.4 codes):
# 2 ground, and if they appear, the noise classes 7 low noise and 18 high noise.
DROPPED_CLASSES: tuple[int, ...] = (2, 7, 18)
LAZ_CHUNK_POINTS = 2_000_000


class LidarOptions(MmlSheetOptions):
    """Download options (sheet scale, dataset, format) and the lidar height thresholds.

    The height options are read by stage 3 (`mapgen.build.lidar`).
    """

    sheet_scale: int = 5_000  # MML product description: 3 x 3 km TM35 1:5 000 sheets
    dataset: str = "Uusin"  # dataSetInput: "Uusin" (latest), "05p_2008-2019" or "05p_2020-"
    file_format: str = "LAZ"
    inner_buffers_m: tuple[float, ...] = (1.0, 0.5, 0.0)
    min_points: int = Field(default=5, ge=1)
    tree_spread_m: float = Field(default=8.0, gt=0)
    min_height_m: float = Field(default=1.0, gt=0)
    max_height_m: float = Field(default=40.0, gt=0)

    @field_validator("process_id")
    @classmethod
    def _only_05p(cls, value: str | None) -> str | None:
        if value is not None and value.strip().lower() != LIDAR_05P_PROCESS:
            raise ValueError(f"the lidar source only allows process {LIDAR_05P_PROCESS!r}")
        return value

    @field_validator("inner_buffers_m")
    @classmethod
    def _buffers(cls, value: tuple[float, ...]) -> tuple[float, ...]:
        if not value:
            raise ValueError("inner_buffers_m needs at least one buffer")
        if any(not np.isfinite(b) or b < 0 for b in value):
            raise ValueError("inner_buffers_m values must be finite and >= 0")
        return value

    @model_validator(mode="after")
    def _height_range(self) -> "LidarOptions":
        if not self.max_height_m > self.min_height_m:
            raise ValueError("max_height_m must be greater than min_height_m")
        return self


def same_las_crs(crs: ProjCRS, target: str) -> bool:
    """True when a LAS file's horizontal CRS is equivalent to `target`.

    LAS files may carry a compound CRS (horizontal plus vertical datum); only the
    horizontal part matters for x and y.
    """
    target_crs = ProjCRS.from_user_input(target)
    candidates = [crs, *(crs.sub_crs_list or [])]
    for candidate in candidates:
        if candidate.is_vertical:
            continue
        if candidate.equals(target_crs, ignore_axis_order=True):
            return True
        epsg = candidate.to_epsg()
        if epsg is not None and epsg == target_crs.to_epsg():
            return True
    return False


def check_las_crs(path: Path, header: laspy.LasHeader, target_crs: str) -> None:
    """Raise unless the file's CRS is equivalent to `target_crs` (points are not reprojected)."""
    crs = header.parse_crs()
    if crs is None:
        raise SourceLoadError(f"{path} has no CRS")
    if not same_las_crs(crs, target_crs):
        raise SourceLoadError(
            f"{path} is in {crs.name}, not {target_crs}; reprojecting point clouds is not supported"
        )


def read_points(
    path: Path, target_crs: str, bounds: BBox, classes: tuple[int, ...] | None = None
) -> PointCloud:
    """The points of one LAS/LAZ file inside `bounds`, without `DROPPED_CLASSES`, or with
    only `classes` when given (e.g. ground points for bridge decks).

    Reads in chunks so a whole sheet never sits in memory at once. The file's CRS must be
    equivalent to `target_crs` (points are not reprojected).
    """
    clouds: list[PointCloud] = []
    with laspy.open(path) as reader:
        check_las_crs(path, reader.header, target_crs)
        dropped = np.array(DROPPED_CLASSES, dtype=np.uint8)
        wanted = np.array(classes or (), dtype=np.uint8)
        for chunk in reader.chunk_iterator(LAZ_CHUNK_POINTS):
            x = np.asarray(chunk.x, dtype=np.float64)
            y = np.asarray(chunk.y, dtype=np.float64)
            classification = np.asarray(chunk.classification, dtype=np.uint8)
            keep = (
                (x >= bounds.min_x)
                & (x <= bounds.max_x)
                & (y >= bounds.min_y)
                & (y <= bounds.max_y)
                & (
                    np.isin(classification, wanted)
                    if classes is not None
                    else ~np.isin(classification, dropped)
                )
            )
            if not keep.any():
                continue
            clouds.append(
                PointCloud(
                    x=x[keep],
                    y=y[keep],
                    z=np.asarray(chunk.z, dtype=np.float64)[keep],
                    classification=classification[keep],
                    return_number=np.asarray(chunk.return_number, dtype=np.uint8)[keep],
                    number_of_returns=np.asarray(chunk.number_of_returns, dtype=np.uint8)[keep],
                    crs=target_crs,
                )
            )
    return PointCloud.concatenate(clouds, target_crs)


def laz_overlaps(path: Path, target_crs: str, bounds: BBox) -> bool:
    """True when the file's header extent overlaps `bounds` (reads only the header).

    The CRS is checked first, so a file in another CRS fails instead of being skipped.
    """
    with laspy.open(path) as reader:
        check_las_crs(path, reader.header, target_crs)
        hmin, hmax = reader.header.mins, reader.header.maxs
    return not (
        hmax[0] < bounds.min_x
        or hmin[0] > bounds.max_x
        or hmax[1] < bounds.min_y
        or hmin[1] > bounds.max_y
    )


class MmlLidarAdapter(MmlSheetAdapter):
    """MML laser scanning data 0.5 p. Never any other laser scanning product (ADR 0005)."""

    name = "mml_lidar"
    Options = LidarOptions
    dataset_name = "Laser scanning data 0,5 p"
    product_name = "Laserkeilausaineisto 0,5 p (Laser scanning data 0,5 p)"
    layer = "lidar_05p"
    default_process_id = LIDAR_05P_PROCESS
    provides = frozenset({POINT_CLOUD})

    def process_id(self, options: MmlOptions) -> str:
        process_id = super().process_id(options)
        if process_id.strip().lower() != LIDAR_05P_PROCESS:
            raise ValueError(f"the lidar source only allows process {LIDAR_05P_PROCESS!r}")
        return process_id

    def extra_inputs(self, options: MmlSheetOptions) -> dict[str, Any]:
        assert isinstance(options, LidarOptions)
        return {"fileFormatInput": options.file_format, "dataSetInput": options.dataset}

    def manual_area(self, ctx: DownloadContext) -> list[str]:
        options = ctx.options
        assert isinstance(options, LidarOptions)
        return [
            f"Format: {options.file_format}, data set {options.dataset}",
            *super().manual_area(ctx),
        ]

    def load(self, ctx: LoadContext) -> SourceData:
        """Points inside `ctx.bounds` from every LAZ file that overlaps them: non-ground
        points, or only `ctx.point_classes` when set.

        Only files that contribute to the area are listed in `files` (for the credits).
        """
        folder = ctx.raw_dir / "mml" / self.layer
        files = [p for p in list_files(folder) if p.suffix.lower() in LAZ_SUFFIXES]
        if not files:
            raise SourceLoadError(
                f"[{ctx.source_name}] no LAZ files in {folder}; run `mapgen download "
                f"{ctx.map_config.id} --only {ctx.source_name}`"
            )
        read: list[Path] = []
        clouds: list[PointCloud] = []
        for path in files:
            try:
                if not laz_overlaps(path, ctx.target_crs, ctx.bounds):
                    continue
                clouds.append(read_points(path, ctx.target_crs, ctx.bounds, ctx.point_classes))
            except SourceLoadError as exc:
                raise SourceLoadError(f"[{ctx.source_name}] {exc}") from exc
            except (laspy.errors.LaspyException, OSError, ValueError) as exc:
                raise SourceLoadError(f"[{ctx.source_name}] cannot read {path}: {exc}") from exc
            read.append(path)
        return SourceData(files=read, points=PointCloud.concatenate(clouds, ctx.target_crs))
