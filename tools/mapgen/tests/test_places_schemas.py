"""The committed place schemas equal the ones generated from their models."""

import json

import jsonschema
import pytest

from conftest import SomeroArea
from mapgen.build.places.model import SCHEMAS, CommittedSchema


@pytest.mark.parametrize("schema", SCHEMAS, ids=lambda s: s.file)
def test_committed_schema_matches_the_model(schema: CommittedSchema) -> None:
    assert schema.committed() == schema.generated(), (
        f"{schema.file} is out of date; regenerate it with: uv run python -c "
        '"from mapgen.build.places.model import write_schemas; write_schemas()"'
    )
    jsonschema.Draft202012Validator.check_schema(schema.committed())


# docs/specs/place-information.md, criterion 1: the test-area build's files validate.


def test_ac1_test_area_buildings_and_pois_validate_against_the_committed_schemas(
    somero_area: SomeroArea,
) -> None:
    from mapgen.build.places.model import BUILDINGS_SCHEMA, POIS_SCHEMA

    for schema, name, version in (
        (BUILDINGS_SCHEMA, "buildings.json", 2),
        (POIS_SCHEMA, "pois.json", 1),
    ):
        document = json.loads((somero_area.pack / name).read_text(encoding="utf-8"))
        assert document["schema_version"] == version
        assert document["map_id"] == "somero"
        jsonschema.validate(document, schema.committed())
        schema.validate(document, name)  # the pydantic model too
    pois = json.loads((somero_area.pack / "pois.json").read_text(encoding="utf-8"))
    assert pois["area"] == "test"


def test_ac1_buildings_v1_is_rejected() -> None:
    from mapgen.build.places.model import BUILDINGS_SCHEMA, PlacesError

    with pytest.raises(PlacesError):
        BUILDINGS_SCHEMA.validate({"schema_version": 1, "map_id": "x", "buildings": []}, "b")
    BUILDINGS_SCHEMA.validate({"schema_version": 2, "map_id": "x", "buildings": []}, "b")
