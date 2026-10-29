"""Locating the repository, map configs, raw data, build work folders and map packs."""

from pathlib import Path

MAPS_DIR = "maps"
MAPGEN_DIR = Path("tools") / "mapgen"
RAW_DIR = MAPGEN_DIR / "data" / "raw"
WORK_DIR = MAPGEN_DIR / "build"  # intermediate stage output, git-ignored
PACKS_DIR = Path("game") / "maps"


class RepoRootNotFoundError(RuntimeError):
    """Raised when no directory with both `maps/` and `tools/mapgen/` is found."""


def _is_repo_root(path: Path) -> bool:
    return (path / MAPS_DIR).is_dir() and (path / MAPGEN_DIR).is_dir()


def find_repo_root(start: Path | None = None) -> Path:
    """Walk up from `start` (default: cwd), then from this package, to find the repo root."""
    candidates = [start or Path.cwd(), Path(__file__).resolve()]
    for candidate in candidates:
        for path in [candidate.resolve(), *candidate.resolve().parents]:
            if _is_repo_root(path):
                return path
    raise RepoRootNotFoundError(
        "could not find the repository root (a folder with maps/ and tools/mapgen/); "
        "run from inside the repository or pass --repo-root"
    )


def map_dir(repo_root: Path, map_id: str) -> Path:
    return repo_root / MAPS_DIR / map_id


def map_config_path(repo_root: Path, map_id: str) -> Path:
    return map_dir(repo_root, map_id) / "map.yaml"


def raw_dir(repo_root: Path, map_id: str) -> Path:
    return repo_root / RAW_DIR / map_id


def work_dir(repo_root: Path, map_id: str, area: str) -> Path:
    """Intermediate output of one build: `tools/mapgen/build/<map-id>/<area>/`."""
    return repo_root / WORK_DIR / map_id / area


def snapshots_dir(repo_root: Path) -> Path:
    """Where `mapgen snapshot create` writes archives: `tools/mapgen/build/snapshots/`."""
    return repo_root / WORK_DIR / "snapshots"


def pack_dir(repo_root: Path, map_id: str) -> Path:
    """The generated map pack: `game/maps/<map-id>/`."""
    return repo_root / PACKS_DIR / map_id
