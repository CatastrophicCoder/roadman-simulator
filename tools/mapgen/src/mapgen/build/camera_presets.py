"""The optional `maps/<map-id>/camera_presets.json` and its committed JSON Schema.

Named camera positions for screenshots, in Godot local metres (+X east, +Y up, -Z north,
origin = the map origin). With `ground_relative`, `position[1]` and `look_at[1]` are
heights above the terrain at that point's x/z. The build validates the file and copies
it unchanged into the pack (spec: `docs/specs/map-loading.md`, ADR 0006 amendment).

The schema in `schemas/camera-presets-v1.schema.json` is generated from `CameraPresets`
and committed, so the game side can read it. A test checks that the two match. After a
model change, regenerate it with:

    uv run python -c "from mapgen.build.camera_presets import write_schema; write_schema()"
"""

import json
from importlib import resources
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    ValidationError,
    field_validator,
    model_validator,
)

from mapgen.config import SLUG_PATTERN

PRESETS_NAME = "camera_presets.json"
SCHEMA_FILE = "camera-presets-v1.schema.json"

Vec3 = tuple[float, float, float]
PresetName = Annotated[str, StringConstraints(pattern=SLUG_PATTERN)]


class CameraPresetsError(Exception):
    """The camera presets file is invalid. The message is shown to the user."""


class _StrictJsonModel(BaseModel):
    # Unknown keys are errors (as in config.py). Strict: no "70" -> 70 or "true" -> True.
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


class CameraPreset(_StrictJsonModel):
    position: Vec3 = Field(description="camera position [x, y, z] in local metres")
    look_at: Vec3 = Field(description="point the camera looks at [x, y, z] in local metres")
    fov: float = Field(default=70.0, ge=1, le=179, description="vertical field of view, degrees")
    ground_relative: bool = Field(
        default=False, description="y values are heights above the terrain at their x/z"
    )

    @model_validator(mode="after")
    def _looks_somewhere(self) -> "CameraPreset":
        if self.position == self.look_at:
            raise ValueError("position and look_at must differ")
        return self


class CameraPresets(_StrictJsonModel):
    schema_version: Literal[1]
    presets: dict[PresetName, CameraPreset] = Field(
        min_length=1,
        description="preset name (lowercase slug) -> preset",
        json_schema_extra={"additionalProperties": False},  # names must match the pattern
    )

    @field_validator("schema_version", mode="before")
    @classmethod
    def _not_a_bool(cls, value: Any) -> Any:
        if isinstance(value, bool):  # True == 1 would pass the Literal check
            raise ValueError("schema_version must be the integer 1")
        return value


class _DuplicateKeyError(Exception):
    # Not a ValueError, so it isn't mistaken for a JSON syntax error below.
    def __init__(self, key: str) -> None:
        super().__init__(key)
        self.key = key


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _DuplicateKeyError(key)
        result[key] = value
    return result


def _check_no_duplicate_keys(data: bytes, source: Path | str) -> None:
    """Reject a repeated key in any JSON object.

    Python's json and pydantic keep the last value, but the game's reader rejects the
    file, and the build copies it into the pack unchanged.
    """
    try:
        json.loads(data, object_pairs_hook=_reject_duplicate_keys)
    except _DuplicateKeyError as exc:
        raise CameraPresetsError(
            f"invalid camera presets {source}: duplicate key {json.dumps(exc.key)}"
        ) from None
    except ValueError:
        pass  # malformed JSON or bad encoding: pydantic reports it in its usual form


def parse_camera_presets(data: bytes, source: Path | str) -> CameraPresets:
    """Validate the bytes of a presets file; `source` names it in the error message."""
    _check_no_duplicate_keys(data, source)
    try:
        return CameraPresets.model_validate_json(data)
    except ValidationError as exc:
        raise CameraPresetsError(f"invalid camera presets {source}: {exc}") from exc


def read_camera_presets(path: Path) -> bytes | None:
    """The validated bytes of the presets file at `path`, to copy into the pack unchanged.

    A missing file is allowed and gives None.
    """
    if not path.is_file():
        return None
    data = path.read_bytes()
    parse_camera_presets(data, path)
    return data


def schema_path() -> Path:
    return Path(str(resources.files("mapgen.build").joinpath("schemas", SCHEMA_FILE)))


def generated_schema() -> dict[str, Any]:
    schema = CameraPresets.model_json_schema()
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": SCHEMA_FILE,
        **schema,
    }


def committed_schema() -> dict[str, Any]:
    return json.loads(schema_path().read_text(encoding="utf-8"))


def write_schema(path: Path | None = None) -> Path:
    target = path or schema_path()
    target.write_text(json.dumps(generated_schema(), indent=2) + "\n", encoding="utf-8")
    return target
