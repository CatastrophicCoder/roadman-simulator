"""Command line interface: `mapgen <command> <map-id> ...`."""

import argparse
import os
import re
import sys
from collections.abc import Sequence
from pathlib import Path

from mapgen import __version__, paths
from mapgen.config import (
    REPO_PATTERN,
    SLUG_PATTERN,
    BBoxWGS84,
    ConfigError,
    load_map_config,
    read_yaml,
)
from mapgen.http import redact

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_USAGE = 2


def _split_csv(value: str) -> list[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


def _non_negative_int(value: str) -> int:
    try:
        number = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError(f"not a whole number: {value!r}") from None
    if number < 0:
        raise argparse.ArgumentTypeError(f"must be 0 or more: {number}")
    return number


def _repo_name(value: str) -> str:
    if not re.fullmatch(REPO_PATTERN, value):
        raise argparse.ArgumentTypeError(f"not OWNER/NAME: {value!r}")
    return value


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
    download.add_argument(
        "--release",
        action="store_true",
        help="skip sources marked debug_only in map.yaml (naming one in --only is an error)",
    )
    download.add_argument(
        "--strict",
        action="store_true",
        help="fail when a selected source returns no files or lacks its API key",
    )
    download.add_argument(
        "--retries",
        type=_non_negative_int,
        default=0,
        help="retry a busy server (Overpass HTTP 429, 502, 503, 504) up to N times with "
        "backoff (default 0: one attempt)",
    )
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
    build.add_argument(
        "--release",
        action="store_true",
        help="skip sources marked debug_only in map.yaml (no debug/ folder in the pack)",
    )
    build.set_defaults(handler=cmd_build)

    notice = commands.add_parser(
        "notice", help="write or check a NOTICE file with the credits of built map packs"
    )
    notice.add_argument("map_ids", nargs="+", metavar="<map-id>")
    mode = notice.add_mutually_exclusive_group(required=True)
    mode.add_argument("--out", type=Path, help="write the NOTICE to this file")
    mode.add_argument(
        "--check",
        type=Path,
        metavar="FILE",
        help="exit non-zero if FILE lacks any credit or licence line of the packs",
    )
    notice.set_defaults(handler=cmd_notice)

    snapshot = commands.add_parser(
        "snapshot", help="create, publish or fetch a pinned raw data snapshot of a map"
    )
    actions = snapshot.add_subparsers(dest="snapshot_command", required=True, metavar="<action>")
    create = actions.add_parser(
        "create", help="zip the non-debug raw data into an archive (no network)"
    )
    create.add_argument("map_id", metavar="<map-id>")
    create.add_argument(
        "--out",
        type=Path,
        default=None,
        help="output folder (default: tools/mapgen/build/snapshots/)",
    )
    create.set_defaults(handler=cmd_snapshot_create)
    publish = actions.add_parser(
        "publish", help="upload an archive as a GitHub data release and pin it in map.yaml"
    )
    publish.add_argument("map_id", metavar="<map-id>")
    publish.add_argument(
        "--archive",
        type=Path,
        default=None,
        help="archive to publish (default: today's archive in tools/mapgen/build/snapshots/)",
    )
    publish.add_argument(
        "--repo",
        type=_repo_name,
        default=None,
        help="OWNER/NAME (default: snapshot.repo in map.yaml, else `gh repo view`)",
    )
    publish.set_defaults(handler=cmd_snapshot_publish)
    fetch = actions.add_parser(
        "fetch", help="download the snapshot pinned in map.yaml into the raw data folder"
    )
    fetch.add_argument("map_id", metavar="<map-id>")
    fetch.add_argument(
        "--force",
        action="store_true",
        help="replace differing and delete extra local files of the snapshot's sources",
    )
    fetch.set_defaults(handler=cmd_snapshot_fetch)

    # Later: `enhance <map-id>` is added here as a further subcommand.
    return parser


def cmd_download(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.download import run_download

    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    raw = paths.raw_dir(repo_root, args.map_id)
    report = run_download(
        config,
        repo_root=repo_root,
        raw_dir=raw,
        only=args.only,
        force=args.force,
        release=args.release,
        strict=args.strict,
        retries=args.retries,
    )
    if report.manifest_path:
        print(f"manifest: {report.manifest_path}")
    if not report.ok:
        print(f"failed sources: {', '.join(report.failed)}", file=sys.stderr)
        for name, reason in report.failed.items():
            print(f"  {name}: {_scrub(reason)}", file=sys.stderr)
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
            release=args.release,
        )
    except (BuildError, ExportError) as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED
    sources = ", ".join(f"{k} {v}" for k, v in report.height_source_counts.items())
    kind = "release" if args.release else "dev"
    print(f"pack: {report.pack_dir} ({report.area} area, {kind} build)")
    print(f"tiles: {report.tile_count}, buildings: {report.building_count} ({sources})")
    if report.skipped:
        print(f"skipped features: {len(report.skipped)} (see {work / '01-load' / 'report.json'})")
    print(f"stage output: {work}")
    print(f"total: {sum(report.timings_s.values()):.2f} s")
    return EXIT_OK


def cmd_notice(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.notice import NoticeError, missing_lines, read_all, render_notice

    try:
        packs = read_all(repo_root, args.map_ids)
        if args.check is not None:
            if not args.check.is_file():
                print(f"error: {args.check} not found", file=sys.stderr)
                return EXIT_FAILED
            missing = missing_lines(args.check.read_text(encoding="utf-8"), packs)
            if missing:
                print(f"error: {args.check} is missing {len(missing)} line(s):", file=sys.stderr)
                for line in missing:
                    print(f"  {line}", file=sys.stderr)
                return EXIT_FAILED
            print(f"{args.check}: all credit lines present ({', '.join(args.map_ids)})")
            return EXIT_OK
        text = render_notice(packs)
    except NoticeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_FAILED
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(text, encoding="utf-8")
    print(f"notice: {args.out} ({len(packs)} map(s))")
    return EXIT_OK


def cmd_snapshot_create(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.snapshot import SnapshotError, create_snapshot

    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    try:
        result = create_snapshot(
            config,
            repo_root=repo_root,
            raw_dir=paths.raw_dir(repo_root, args.map_id),
            out_dir=(args.out or paths.snapshots_dir(repo_root)).resolve(),
        )
    except SnapshotError as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED
    print(f"archive: {result.path}")
    print(f"size: {result.size:,} bytes")
    print(f"sha256: {result.sha256}")
    print(f"release tag: {result.tag}")
    print("members:")
    for name, size in result.members:
        print(f"  {size:>12,}  {name}")
    return EXIT_OK


def cmd_snapshot_publish(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.snapshot import SnapshotError, publish_snapshot

    map_yaml = paths.map_config_path(repo_root, args.map_id)
    config = load_map_config(map_yaml)
    try:
        result = publish_snapshot(
            config,
            repo_root=repo_root,
            map_yaml=map_yaml,
            archive=args.archive.resolve() if args.archive else None,
            repo=args.repo,
        )
    except SnapshotError as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED
    print(f"published {result.asset} as release {result.tag} in {result.repo}")
    print(f"sha256: {result.sha256}")
    print(f"updated the snapshot block in {result.map_yaml}; commit it")
    return EXIT_OK


def cmd_snapshot_fetch(args: argparse.Namespace, repo_root: Path) -> int:
    from mapgen.snapshot import SnapshotError, fetch_snapshot

    config = load_map_config(paths.map_config_path(repo_root, args.map_id))
    try:
        report = fetch_snapshot(
            config, raw_dir=paths.raw_dir(repo_root, args.map_id), force=args.force
        )
    except SnapshotError as exc:
        print(f"error: {_scrub(str(exc))}", file=sys.stderr)
        return EXIT_FAILED
    counts = (
        f"{len(report.added)} added, {len(report.replaced)} replaced, "
        f"{len(report.deleted)} deleted, {len(report.unchanged)} unchanged"
    )
    print(f"snapshot: {report.url}")
    print(f"files: {counts}")
    print(f"manifest: {report.manifest_path}")
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
    map_ids = getattr(args, "map_ids", None) or [getattr(args, "map_id", None)]
    for map_id in map_ids:
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
