"""Pack folder safety: atomic replacement, refusing folders that aren't map packs, credits."""

import json
from pathlib import Path
from typing import Any

import pytest

from conftest import FixtureBuild, TmpRepo, write_fixture_raw
from mapgen.build import BuildError, run_build
from mapgen.build.area import build_area
from mapgen.build.pack_io import PackFolderError, check_pack_target, swap_in
from mapgen.build.pack_manifest import ManifestError, credits_for
from mapgen.build.stage1_load import run_load
from mapgen.cli import EXIT_FAILED, main


def _quiet(_: str) -> None:
    pass


def _snapshot(folder: Path) -> dict[str, bytes]:
    return {
        p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()
    }


@pytest.fixture
def built_pack(tmp_repo: TmpRepo, fixture_data: dict) -> Any:
    """A tmp repo with fixture raw data and a finished pack in game/maps/testtown/."""
    config = tmp_repo.load(fixture_data)
    raw = write_fixture_raw(tmp_repo.raw_dir("testtown"))
    pack = tmp_repo.root / "game" / "maps" / "testtown"
    run_build(config, "test", raw, pack, tmp_repo.root / "work", log=_quiet)
    return config, raw, pack


def test_failed_build_leaves_the_previous_pack_untouched(built_pack: Any) -> None:
    config, raw, pack = built_pack
    before = _snapshot(pack)
    (raw / "download-manifest.json").unlink()  # credits fail after all stages ran
    with pytest.raises(BuildError, match="not in the download manifest"):
        run_build(config, "test", raw, pack, pack.parent.parent / "work", log=_quiet)
    assert _snapshot(pack) == before
    assert sorted(p.name for p in pack.parent.iterdir()) == ["testtown"]  # no temp folders


def test_export_failure_leaves_the_previous_pack_untouched(
    built_pack: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    import mapgen.build as build

    config, raw, pack = built_pack
    before = _snapshot(pack)

    def fail(*args: object, **kwargs: object) -> None:
        raise OSError("disk full")

    monkeypatch.setattr(build, "export_glb", fail)
    with pytest.raises(OSError, match="disk full"):
        run_build(config, "test", raw, pack, pack.parent.parent / "work", log=_quiet)
    assert _snapshot(pack) == before
    assert sorted(p.name for p in pack.parent.iterdir()) == ["testtown"]


def test_refuses_to_clear_a_folder_that_is_not_a_pack(
    tmp_repo: TmpRepo, fixture_data: dict, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    tmp_repo.write_map(fixture_data)
    write_fixture_raw(tmp_repo.raw_dir("testtown"))
    out = tmp_path / "documents"
    out.mkdir()
    (out / "thesis.txt").write_text("keep me", encoding="utf-8")
    args = ["--repo-root", str(tmp_repo.root), "build", "testtown", "--area", "test"]
    assert main([*args, "--out", str(out)]) == EXIT_FAILED
    assert "doesn't look like a map pack" in capsys.readouterr().err
    assert [p.name for p in out.iterdir()] == ["thesis.txt"]


def test_pack_target_rules(tmp_path: Path) -> None:
    check_pack_target(tmp_path / "missing")  # new folder: fine
    (tmp_path / "empty").mkdir()
    check_pack_target(tmp_path / "empty")
    (tmp_path / "pack").mkdir()
    (tmp_path / "pack" / "manifest.json").write_text("{}", encoding="utf-8")
    (tmp_path / "pack" / "anything").write_text("", encoding="utf-8")
    check_pack_target(tmp_path / "pack")
    (tmp_path / "file").write_text("", encoding="utf-8")
    with pytest.raises(PackFolderError, match="not a folder"):
        check_pack_target(tmp_path / "file")


def test_swap_in_replaces_the_old_pack(tmp_path: Path) -> None:
    target, staging = tmp_path / "pack", tmp_path / ".pack.tmp"
    target.mkdir()
    (target / "manifest.json").write_text("old", encoding="utf-8")
    staging.mkdir()
    (staging / "manifest.json").write_text("new", encoding="utf-8")
    swap_in(staging, target)
    assert (target / "manifest.json").read_text(encoding="utf-8") == "new"
    assert sorted(p.name for p in tmp_path.iterdir()) == ["pack"]


def _entry(source: str, retrieved_at: str) -> dict[str, str]:
    return {
        "source": source,
        "dataset": "Elevation model 2 m",
        "licence": "CC BY 4.0",
        "attribution": "DEM",
        "retrieved_at": retrieved_at,
    }


def test_credits_fold_files_of_the_same_dataset(tmp_path: Path) -> None:
    files = [tmp_path / "mml" / "a.tif", tmp_path / "mml" / "b.tif", tmp_path / "osm" / "x.osm"]
    entries = {
        "mml/a.tif": _entry("mml_dem_2m", "2026-10-01T00:00:00Z"),
        "mml/b.tif": _entry("mml_dem_2m", "2026-10-05T00:00:00Z"),
        "osm/x.osm": {**_entry("osm", "2026-09-01T00:00:00Z"), "dataset": "OpenStreetMap"},
    }
    credits = credits_for(files, tmp_path, entries)
    assert [(c.source, c.retrieved_at) for c in credits] == [
        ("mml_dem_2m", "2026-10-05T00:00:00Z"),
        ("osm", "2026-09-01T00:00:00Z"),
    ]


def test_credits_reject_files_outside_the_raw_folder(tmp_path: Path) -> None:
    with pytest.raises(ManifestError, match="outside the raw data folder"):
        credits_for([tmp_path / "elsewhere.tif"], tmp_path / "raw", {})


def test_more_than_one_source_per_capability_is_an_error(
    tmp_repo: TmpRepo, fixture_data: dict
) -> None:
    fixture_data["sources"]["second_dem"] = {"adapter": "mml_dem_2m", "options": {}}
    config = tmp_repo.load(fixture_data)
    raw = write_fixture_raw(tmp_repo.raw_dir("testtown"))
    with pytest.raises(BuildError, match="more than one enabled source provides elevation"):
        run_load(config, build_area(config, "test"), raw, log=_quiet)


def test_manifest_written_by_the_build_is_valid_json(fixture_build: FixtureBuild) -> None:
    manifest = json.loads((fixture_build.pack_dir / "manifest.json").read_text(encoding="utf-8"))
    assert [c["source"] for c in manifest["credits"]] == ["mml_dem_2m", "osm"]
