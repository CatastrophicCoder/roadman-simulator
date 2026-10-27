"""Replacing a map pack folder safely.

The pack is written into a temporary sibling folder and swapped in only when it is
complete and valid, so a failed build leaves the previous pack untouched. A folder is
only replaced when it is empty or looks like a map pack (has a `manifest.json`).
"""

import shutil
import tempfile
import uuid
from pathlib import Path

from mapgen.build.pack_manifest import MANIFEST_NAME


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


def staging_dir(target: Path) -> Path:
    """A new empty folder next to `target` (same file system, so the swap is a rename)."""
    target.parent.mkdir(parents=True, exist_ok=True)
    return Path(tempfile.mkdtemp(prefix=f".{target.name}.tmp-", dir=target.parent))


def swap_in(staging: Path, target: Path) -> None:
    """Replace `target` with `staging`: move the old one aside, move the new one in, then
    delete the old one. If moving the new one in fails, the old one is put back."""
    check_pack_target(target)
    old = target.with_name(f".{target.name}.old-{uuid.uuid4().hex[:8]}")
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
