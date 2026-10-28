"""The optional `maps/<map-id>/camera_presets.json`: validation, and the copy into the pack."""

import json
import shutil
from pathlib import Path
from typing import Any

import jsonschema
import pytest

from conftest import REPO_ROOT, FixtureBuild
from mapgen.build import BuildError, run_build
from mapgen.build.camera_presets import (
    PRESETS_NAME,
    CameraPresetsError,
    committed_schema,
    generated_schema,
    parse_camera_presets,
)
from mapgen.cli import EXIT_FAILED, EXIT_OK, main
from mapgen.config import load_map_config

SOMERO_PRESETS = REPO_ROOT / "maps" / "somero" / PRESETS_NAME


def _valid() -> dict[str, Any]:
    return {
        "schema_version": 1,
        "presets": {
            "overview": {
                "position": [0, 500, 500],
                "look_at": [0, 0, 0],
                "fov": 60.5,
                "ground_relative": True,
            },
            "street": {"position": [1.5, 1.7, 2], "look_at": [1.5, 1.7, -50]},
        },
    }


def _parse(document: Any) -> Any:
    return parse_camera_presets(json.dumps(document).encode(), "test.json")


def _snapshot(folder: Path) -> dict[str, bytes]:
    return {
        p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()
    }


def _quiet(_: str) -> None:
    pass


# Validation


def test_valid_file_and_defaults() -> None:
    presets = _parse(_valid()).presets
    assert presets["overview"].position == (0.0, 500.0, 500.0)
    assert presets["overview"].fov == 60.5 and presets["overview"].ground_relative
    assert presets["street"].fov == 70.0 and not presets["street"].ground_relative


def _broken(change: str) -> dict[str, Any]:
    document = _valid()
    preset = document["presets"]["street"]
    match change:
        case "version 2":
            document["schema_version"] = 2
        case "version true":
            document["schema_version"] = True
        case "version string":
            document["schema_version"] = "1"
        case "no version":
            del document["schema_version"]
        case "no presets":
            document["presets"] = {}
        case "unknown top-level key":
            document["cameras"] = {}
        case "unknown preset key":
            preset["roll"] = 0
        case "name not a slug":
            document["presets"]["Centre Street"] = document["presets"].pop("street")
        case "position too short":
            preset["position"] = [0, 1]
        case "look_at too long":
            preset["look_at"] = [0, 1, 2, 3]
        case "coordinate is a string":
            preset["position"] = [0, "1.7", 0]
        case "missing look_at":
            del preset["look_at"]
        case "fov too small":
            preset["fov"] = 0.5
        case "fov too large":
            preset["fov"] = 180
        case "fov string":
            preset["fov"] = "70"
        case "ground_relative string":
            preset["ground_relative"] = "true"
        case "looks at itself":
            preset["look_at"] = preset["position"]
        case _:
            raise AssertionError(change)
    return document


@pytest.mark.parametrize(
    "change",
    [
        "version 2",
        "version true",
        "version string",
        "no version",
        "no presets",
        "unknown top-level key",
        "unknown preset key",
        "name not a slug",
        "position too short",
        "look_at too long",
        "coordinate is a string",
        "missing look_at",
        "fov too small",
        "fov too large",
        "fov string",
        "ground_relative string",
        "looks at itself",
    ],
)
def test_invalid_files_are_rejected(change: str) -> None:
    with pytest.raises(CameraPresetsError, match="invalid camera presets test.json"):
        _parse(_broken(change))


@pytest.mark.parametrize("text", ["", "{", "[]", '{"schema_version": 1, "presets": {"a": NaN}}'])
def test_malformed_json_is_rejected(text: str) -> None:
    with pytest.raises(CameraPresetsError, match="invalid camera presets"):
        parse_camera_presets(text.encode(), "test.json")


_PRESET = '{"position": [0, 1, 2], "look_at": [0, 0, 0]}'


def _fill(template: str) -> str:
    """`template` with each `P` replaced by a valid preset object."""
    return template.replace("P", _PRESET)


@pytest.mark.parametrize(
    ("text", "key"),
    [
        # top level
        (
            _fill('{"schema_version": 1, "schema_version": 1, "presets": {"a": P}}'),
            "schema_version",
        ),
        (
            _fill('{"schema_version": 1, "presets": {"a": P}, "presets": {"b": P}}'),
            "presets",
        ),
        # preset names
        (_fill('{"schema_version": 1, "presets": {"a": P, "a": P}}'), "a"),
        # inside a preset, with identical values too
        (
            '{"schema_version": 1, "presets": {"a": {"position": [0, 1, 2], "fov": 60, '
            '"look_at": [0, 0, 0], "fov": 60}}}',
            "fov",
        ),
    ],
)
def test_duplicate_keys_are_rejected(text: str, key: str) -> None:
    assert json.loads(text)  # Python's json alone would accept the file
    with pytest.raises(
        CameraPresetsError, match=f'^invalid camera presets test.json: duplicate key "{key}"$'
    ):
        parse_camera_presets(text.encode(), "test.json")


def test_same_key_in_different_objects_is_allowed() -> None:
    # Two presets with the same field names are not duplicates.
    text = _fill('{"schema_version": 1, "presets": {"a": P, "b": P}}')
    assert set(parse_camera_presets(text.encode(), "test.json").presets) == {"a", "b"}


@pytest.mark.parametrize("value", ["NaN", "Infinity", "-Infinity"])
def test_non_finite_coordinates_are_rejected(value: str) -> None:
    text = f'{{"schema_version": 1, "presets": {{"a": {{"position": [0, {value}, 1], '
    text += '"look_at": [0, 0, 0]}}}'
    with pytest.raises(CameraPresetsError, match="presets.a.position"):
        parse_camera_presets(text.encode(), "test.json")


def test_committed_schema_matches_the_model() -> None:
    assert committed_schema() == generated_schema(), (
        "regenerate: uv run python -c "
        '"from mapgen.build.camera_presets import write_schema; write_schema()"'
    )
    validator = jsonschema.Draft202012Validator(committed_schema())
    validator.check_schema(committed_schema())
    assert validator.is_valid(_valid())
    for change in ("version 2", "unknown preset key", "name not a slug", "fov too large"):
        assert not validator.is_valid(_broken(change)), change


def test_somero_presets_are_valid() -> None:
    presets = parse_camera_presets(SOMERO_PRESETS.read_bytes(), SOMERO_PRESETS).presets
    assert {"overview", "centre-street-level"} <= set(presets)
    assert all(p.ground_relative for p in presets.values())
    street = presets["centre-street-level"]
    assert street.position[1] == pytest.approx(1.7)


# Build


@pytest.fixture
def repo(fixture_build: FixtureBuild, tmp_path: Path) -> Path:
    """A copy of the fixture repo (map config and raw data), with no pack yet."""
    root = tmp_path / "repo"
    shutil.copytree(fixture_build.repo / "maps", root / "maps")
    shutil.copytree(fixture_build.raw_dir, root / "tools" / "mapgen" / "data" / "raw" / "testtown")
    return root


def _build_args(root: Path) -> list[str]:
    return ["--repo-root", str(root), "build", "testtown", "--area", "test"]


def test_valid_presets_are_copied_unchanged(repo: Path) -> None:
    # Odd formatting and key order must survive: the file is copied, not rewritten.
    text = '{"presets": {"overview": {"look_at": [0,0,0], "position": [0, 400, 450]}},\n'
    text += ' "schema_version": 1}'
    source = repo / "maps" / "testtown" / PRESETS_NAME
    source.write_text(text, encoding="utf-8")
    assert main(_build_args(repo)) == EXIT_OK
    pack = repo / "game" / "maps" / "testtown"
    assert (pack / PRESETS_NAME).read_bytes() == source.read_bytes()
    assert PRESETS_NAME not in json.loads((pack / "manifest.json").read_text(encoding="utf-8"))


def test_missing_presets_are_allowed(repo: Path) -> None:
    pack = repo / "game" / "maps" / "testtown"
    (repo / "maps" / "testtown" / PRESETS_NAME).write_text(json.dumps(_valid()), encoding="utf-8")
    assert main(_build_args(repo)) == EXIT_OK
    assert (pack / PRESETS_NAME).is_file()
    (repo / "maps" / "testtown" / PRESETS_NAME).unlink()
    assert main(_build_args(repo)) == EXIT_OK
    assert (pack / "manifest.json").is_file()
    assert not (pack / PRESETS_NAME).exists()  # no stale presets from the previous pack


def test_invalid_presets_fail_the_build_and_keep_the_previous_pack(
    repo: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    pack = repo / "game" / "maps" / "testtown"
    assert main(_build_args(repo)) == EXIT_OK
    before = _snapshot(pack)
    source = repo / "maps" / "testtown" / PRESETS_NAME
    source.write_text(json.dumps(_broken("fov too large")), encoding="utf-8")
    capsys.readouterr()
    assert main(_build_args(repo)) == EXIT_FAILED
    err = capsys.readouterr().err
    assert f"invalid camera presets {source}" in err
    assert "presets.street.fov" in err
    assert _snapshot(pack) == before
    assert sorted(p.name for p in pack.parent.iterdir()) == ["testtown"]  # no temp folders


def test_duplicate_keys_fail_the_build(repo: Path, capsys: pytest.CaptureFixture[str]) -> None:
    source = repo / "maps" / "testtown" / PRESETS_NAME
    source.write_text(
        _fill('{"schema_version": 1, "presets": {"a": P, "a": P}}'),
        encoding="utf-8",
    )
    assert main(_build_args(repo)) == EXIT_FAILED
    assert f'invalid camera presets {source}: duplicate key "a"' in capsys.readouterr().err
    assert not (repo / "game" / "maps" / "testtown" / PRESETS_NAME).exists()


def test_invalid_presets_fail_before_the_stages_run(
    repo: Path, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    import mapgen.build as build

    def no_stages(*args: object, **kwargs: object) -> None:
        raise AssertionError("the load stage ran before the presets were checked")

    monkeypatch.setattr(build, "run_load", no_stages)
    map_dir = repo / "maps" / "testtown"
    (map_dir / PRESETS_NAME).write_text("{not json", encoding="utf-8")
    config = load_map_config(map_dir / "map.yaml")
    with pytest.raises(BuildError, match="invalid camera presets"):
        run_build(
            config,
            "test",
            repo / "raw-unused",
            tmp_path / "pack",
            tmp_path / "work",
            map_dir=map_dir,
            log=_quiet,
        )
    assert not (tmp_path / "pack").exists()
