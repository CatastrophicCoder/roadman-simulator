"""The shared name format for map features (ADR 0012, "Name fields").

Roads, buildings and points of interest carry `name`, `name_sv` and `ref` (from the OSM
tags `name`, `name:sv` and `ref`), always present, each a string or None. Values are
trimmed and NFC-normalised, otherwise kept exactly as tagged (including `;` separators):
never split, translated or shortened. Addresses (`street`, `housenumber` from
`addr:street` and `addr:housenumber`) follow the same value rules. This is the only
implementation; every stage uses it.
"""

import unicodedata
from collections.abc import Mapping

NAME_KEYS = ("name", "name_sv", "ref")
ADDRESS_KEYS = ("street", "housenumber")


def name_value(value: str | None) -> str | None:
    """A name tag trimmed and NFC-normalised; None when missing or empty."""
    if value is None:
        return None
    text = unicodedata.normalize("NFC", value.strip())
    return text or None


def name_fields(tags: Mapping[str, str]) -> dict[str, str | None]:
    """The shared name format: `name`, `name_sv` and `ref`, always present."""
    return {
        "name": name_value(tags.get("name")),
        "name_sv": name_value(tags.get("name:sv")),
        "ref": name_value(tags.get("ref")),
    }


def address_fields(tags: Mapping[str, str]) -> dict[str, str | None]:
    """`street` and `housenumber` from `addr:street` and `addr:housenumber` (name rules).

    House numbers such as `12a` or `3-5` are kept as tagged.
    """
    return {
        "street": name_value(tags.get("addr:street")),
        "housenumber": name_value(tags.get("addr:housenumber")),
    }


def has_any(fields: Mapping[str, str | None]) -> bool:
    return any(value is not None for value in fields.values())
