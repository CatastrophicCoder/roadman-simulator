"""Replacing a map pack folder safely.

The pack is written into a staging folder in the build's work folder
(`tools/mapgen/build/<map-id>/<area>/`), outside the Godot project, and moved into place
only when it is complete and valid, so a failed build leaves the previous pack untouched
and Godot never sees a half-written pack. Every move is a single rename on one file
system: the build fails before writing anything if the work folder and the pack's parent
folder are on different volumes. A folder is only replaced when it is empty or looks
like a map pack (has a `manifest.json`). When a pack check or the export's read-back
fails, the staging folder is kept as `failed-<UTC time>` in the work folder (the newest
`KEEP_FAILED` only), so the failure leaves its evidence.
"""

import os
import shutil
import tempfile
import uuid
from datetime import UTC, datetime
from pathlib import Path

from mapgen.build.pack_manifest import MANIFEST_NAME

STAGING_PREFIX = ".pack-staging-"
OLD_PREFIX = ".pack-old-"
FAILED_PREFIX = "failed-"
KEEP_FAILED = 3


class PackFolderError(Exception):
    """The pack folder can't be replaced. The message is shown to the user."""


def check_pack_target(target: Path) -> None:
    """Refuse targets that would destroy something that isn't a map pack."""
    if target.exists() and not target.is_dir():
        raise PackFolderError(f"{target} exists and is not a folder")
    if target.is_dir() and any(target.iterdir()) and not (target / MANIFEST_NAME).is_file():
        raise PackFolderError(
            f"refusing to replace {target}: it is not empty and has no {MANIFEST_NAME}, "
            "so it doesn't look like a map pack"
        )


def _device(path: Path) -> int:
    return os.stat(path).st_dev


def _existing(path: Path) -> Path:
    """`path`, or its nearest existing parent (a folder not created yet will be created
    inside it, on its volume)."""
    path = path.absolute()
    while not path.exists() and path.parent != path:
        path = path.parent
    return path


def check_same_volume(staging_root: Path, target: Path) -> None:
    """Fail unless a rename from `staging_root` into `target`'s parent stays on one
    volume (a rename across volumes is not atomic, or fails). Folders that don't exist
    yet count as on their nearest existing parent's volume."""
    if _device(_existing(staging_root)) != _device(_existing(target.parent)):
        raise PackFolderError(
            f"the work folder {staging_root} and the pack folder {target.parent} are on "
            "different volumes, so the pack can't be moved in with a single rename; "
            "put both on the same volume"
        )


def staging_dir(target: Path, staging_root: Path) -> Path:
    """A new empty folder in `staging_root` (the build's work folder) on the same volume
    as `target`'s parent, so the swap is a rename."""
    staging_root.mkdir(parents=True, exist_ok=True)
    target.parent.mkdir(parents=True, exist_ok=True)
    check_same_volume(staging_root, target)
    return Path(tempfile.mkdtemp(prefix=STAGING_PREFIX, dir=staging_root))


def swap_in(staging: Path, target: Path) -> None:
    """Replace `target` with `staging`: move the old pack out next to the staging folder,
    move the new one in with one rename, then delete the old one. If moving the new one
    in fails, the old one is put back. The old pack is deleted outside `target`'s parent,
    so a reader there sees the old pack, then (for an instant) none, then the new one."""
    check_pack_target(target)
    check_same_volume(staging.parent, target)
    old = staging.parent / f"{OLD_PREFIX}{uuid.uuid4().hex[:8]}"
    had_old = target.exists()
    if had_old:
        target.rename(old)
    try:
        staging.rename(target)
    except OSError:
        if had_old:
            old.rename(target)
        raise
    if had_old:
        shutil.rmtree(old, ignore_errors=True)


def keep_failed(staging: Path, when: datetime, keep: int = KEEP_FAILED) -> Path:
    """Move a failed build's staging folder to `failed-<UTC time>` next to it (in the
    work folder) and delete all but the `keep` newest such folders. Returns the new
    path. The names sort by time; a second failure in the same second gets `-2`, `-3`..."""
    root = staging.parent
    stamp = f"{FAILED_PREFIX}{when.astimezone(UTC):%Y%m%dT%H%M%SZ}"
    kept = root / stamp
    n = 2
    while kept.exists():
        kept = root / f"{stamp}-{n}"
        n += 1
    staging.rename(kept)
    failed = sorted(
        (p for p in root.iterdir() if p.is_dir() and p.name.startswith(FAILED_PREFIX)),
        key=lambda p: _failed_order(p.name),
    )
    for old in failed[:-keep] if keep > 0 else failed:
        if old != kept:
            shutil.rmtree(old, ignore_errors=True)
    return kept


def _failed_order(name: str) -> tuple[str, int]:
    """`failed-<stamp>` before `failed-<stamp>-2` before `failed-<stamp>-10`."""
    stamp, _, n = name[len(FAILED_PREFIX) :].partition("-")
    return stamp, int(n) if n.isdigit() else 1
