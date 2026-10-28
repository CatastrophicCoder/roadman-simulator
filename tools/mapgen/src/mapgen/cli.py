"""Command line interface: `mapgen <command> <map-id> ...`."""

import argparse
import os
import re
import sys
from collections.abc import Sequence
from pathlib import Path

from mapgen import __version__, paths
from mapgen.config import SLUG_PATTERN, BBoxWGS84, ConfigError, load_map_config, read_yaml
from mapgen.http import redact

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_USAGE = 2


def _split_csv(value: str) -> list[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="mapgen", description="Roadman Simulator map pipeline")
    parser.add_argument("--version", action="version", version=f"mapgen {__version__}")
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=None,
        help="repository root (default: found by walking up to a folder with maps/)",
    )
    commands = parser.add_subparsers(dest="command", required=True, metavar="<command>")

    download = commands.add_parser("download", help="download raw data for a map")
    download.add_argument("map_id", metavar="<map-id>")
    download.add_argument(
        "--only", type=_split_csv, default=None, help="comma-separated source names from map.yaml"
    )
    download.add_argument("--force", action="store_true", help="download again over existing files")
    download.set_defaults(handler=cmd_download)

    origin = commands.add_parser(
        "origin", help="print the bbox centre in the map CRS (to set origin in map.yaml)"
    )
    origin.add_argument("map_id", metavar="<map-id>")
    origin.set_defaults(handler=cmd_origin)

    build = commands.add_parser("build", help="build the map pack from the raw data")
    build.add_argument("map_id", metavar="<map-id>")
    build.add_argument(
        "--area",
        choices=["full", "test"],
        default="full",
        help="full: the whole bbox (default); test: the small test area from map.yaml",
    )
    build.add_argument(
        "--out",
        type=Path,
        default=None,
        help="pack folder (default: game/maps/<map-id>/; meant for tests)",
    )
    build.set_defaults(handler=cmd_build)

    # Later: `enhance <map-id>` is added here as a further subcommand.
    return parser


def cmd_download(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.download import run_download

    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    raw = paths.raw_dir(repo_root, args.map_id)
    report = run_download(
        config, repo_root=repo_root, raw_dir=raw, only=args.only, force=args.force
    )
    if report.manifest_path:
        print(f"manifest: {report.manifest_path}")
    if not report.ok:
        print(f"failed sources: {', '.join(report.failed)}", file=sys.stderr)
        return EXIT_FAILED
    return EXIT_OK


def cmd_build(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.build import BuildError, run_build
    from mapgen.build.export_glb import ExportError

    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    out = (args.out or paths.pack_dir(repo_root, args.map_id)).resolve()
    work = paths.work_dir(repo_root, args.map_id, args.area)
    try:
        report = run_build(
            config,
            args.area,
            raw_dir=paths.raw_dir(repo_root, args.map_id),
            out_dir=out,
            work_dir=work,
            map_dir=paths.map_dir(repo_root, args.map_id),
        )
    except (BuildError, ExportError) as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED
    sources = ", ".join(f"{k} {v}" for k, v in report.height_source_counts.items())
    print(f"pack: {report.pack_dir} ({report.area} area)")
    print(f"tiles: {report.tile_count}, buildings: {report.building_count} ({sources})")
    if report.skipped:
        print(f"skipped features: {len(report.skipped)} (see {work / '01-load' / 'report.json'})")
    print(f"stage output: {work}")
    print(f"total: {sum(report.timings_s.values()):.2f} s")
    return EXIT_OK


def cmd_origin(args: argparse.Namespace, repo_root: Path) -> int:
    path = paths.map_config_path(repo_root, args.map_id)
    data = read_yaml(path)
    try:
        bbox = BBoxWGS84.model_validate(data.get("bbox_wgs84"))
    except ValueError as exc:
        raise ConfigError(f"{path}: bbox_wgs84: {exc}") from exc
    crs = str(data.get("crs", "EPSG:3067"))
    x, y = bbox.centre_in(crs)
    box = bbox.projected(crs)
    lat = (bbox.lat_min + bbox.lat_max) / 2
    lon = (bbox.lon_min + bbox.lon_max) / 2
    print(f"# bbox centre {lat:.6f} N, {lon:.6f} E in {crs}, rounded to the metre")
    print(f"origin: {{x: {round(x)}, y: {round(y)}}}")
    print(f"# projected bbox: {', '.join(f'{v:.1f}' for v in box.as_list())}")
    return EXIT_OK


def _scrub(text: str) -> str:
    """Redact `api-key=` values and the literal MML key from text shown to the user."""
    text = redact(text)
    key = os.environ.get("MML_API_KEY", "").strip()
    return text.replace(key, "***") if key else text


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    map_id = getattr(args, "map_id", None)
    if map_id is not None and not re.fullmatch(SLUG_PATTERN, map_id):
        print(
            f"error: invalid map id {map_id!r}: use lowercase letters, digits, '-' and '_'",
            file=sys.stderr,
        )
        return EXIT_USAGE
    try:
        repo_root = (args.repo_root or paths.find_repo_root()).resolve()
        return int(args.handler(args, repo_root))
    except (ConfigError, paths.RepoRootNotFoundError) as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_USAGE
    except Exception as exc:  # last resort: never print a traceback that may hold the key
        print(f"error: unexpected {type(exc).__name__}: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
