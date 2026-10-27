"""Maps the `adapter` value in map.yaml to an adapter class."""

from dataclasses import dataclass
from pathlib import Path

from pydantic import ValidationError

from mapgen.config import ConfigError, MapConfig
from mapgen.sources.base import AdapterOptions, SourceAdapter
from mapgen.sources.mml import (
    MmlBuildings3dAdapter,
    MmlDem2mAdapter,
    MmlOrthophotoAdapter,
    MmlTopographicAdapter,
)
from mapgen.sources.osm import OverpassAdapter

ADAPTERS: dict[str, type[SourceAdapter]] = {
    cls.name: cls
    for cls in (
        OverpassAdapter,
        MmlDem2mAdapter,
        MmlOrthophotoAdapter,
        MmlBuildings3dAdapter,
        MmlTopographicAdapter,
    )
}


def get_adapter(name: str) -> type[SourceAdapter]:
    try:
        return ADAPTERS[name]
    except KeyError:
        known = ", ".join(sorted(ADAPTERS))
        raise ConfigError(f"unknown adapter {name!r}; known adapters: {known}") from None


def parse_options(source_name: str, adapter: type[SourceAdapter], raw: dict) -> AdapterOptions:
    try:
        return adapter.Options.model_validate(raw)
    except ValidationError as exc:
        raise ConfigError(f"source {source_name!r} ({adapter.name}): {exc}") from exc


def validate_source_options(config: MapConfig, path: Path | None = None) -> None:
    """Check that every source names a known adapter and has valid options."""
    prefix = f"{path}: " if path else ""
    for source_name, source in config.sources.items():
        try:
            parse_options(source_name, get_adapter(source.adapter), source.options)
        except ConfigError as exc:
            raise ConfigError(f"{prefix}{exc}") from exc


@dataclass(frozen=True)
class SelectedSource:
    name: str
    adapter: SourceAdapter
    options: AdapterOptions


def enabled_sources_providing(config: MapConfig, capability: str) -> list[SelectedSource]:
    """The enabled sources whose adapter declares `capability`, in map.yaml order."""
    selected: list[SelectedSource] = []
    for source_name, source in config.enabled_sources().items():
        adapter_cls = get_adapter(source.adapter)
        if capability in adapter_cls.provides:
            options = parse_options(source_name, adapter_cls, source.options)
            selected.append(SelectedSource(source_name, adapter_cls(), options))
    return selected
