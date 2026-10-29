"""The `download` command: run each enabled source's adapter and write the manifest.

`release` skips the sources marked `debug_only` in map.yaml. `strict` turns two cases
that are otherwise only logged into failures: a selected source that needs environment
variables (the MML API key) that aren't set, and a selected source that returned no files.
"""

import os
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from mapgen import manifest
from mapgen.config import ConfigError, MapConfig
from mapgen.http import HttpClient, UrllibClient
from mapgen.sources.base import DownloadContext, DownloadedFile, SourceError, utc_now
from mapgen.sources.registry import get_adapter, parse_options


@dataclass
class DownloadReport:
    files: list[DownloadedFile] = field(default_factory=list)
    failed: dict[str, str] = field(default_factory=dict)
    manifest_path: Path | None = None

    @property
    def ok(self) -> bool:
        return not self.failed


def select_sources(
    config: MapConfig,
    only: list[str] | None,
    *,
    release: bool = False,
    log: Callable[[str], None] = print,
) -> list[str]:
    """Enabled sources in map.yaml order, or exactly the ones named in `only`.

    With `release`, `debug_only` sources are skipped (and logged); naming one in `only`
    is a usage error, because a release must not download debug-only data.
    """
    if not only:
        names = list(config.enabled_sources())
    else:
        unknown = [name for name in only if name not in config.sources]
        if unknown:
            known = ", ".join(config.sources)
            raise ConfigError(f"unknown source(s) {', '.join(unknown)}; map.yaml has: {known}")
        disabled = [name for name in only if not config.sources[name].enabled]
        if disabled:
            raise ConfigError(
                f"source(s) {', '.join(disabled)} are disabled in map.yaml; set enabled: true first"
            )
        names = list(dict.fromkeys(only))
        if release:
            debug = [name for name in names if config.sources[name].debug_only]
            if debug:
                raise ConfigError(
                    f"source(s) {', '.join(debug)} are debug_only in map.yaml and are never "
                    "downloaded with --release; drop them from --only or drop --release"
                )
    if release:
        for name in names:
            if config.sources[name].debug_only:
                log(f"[{name}] skipped (debug only)")
        names = [name for name in names if not config.sources[name].debug_only]
    return names


def run_download(
    config: MapConfig,
    *,
    repo_root: Path,
    raw_dir: Path,
    only: list[str] | None = None,
    force: bool = False,
    release: bool = False,
    strict: bool = False,
    retries: int = 0,
    http: HttpClient | None = None,
    env: Mapping[str, str] | None = None,
    log: Callable[[str], None] = print,
    now: Callable[[], datetime] = utc_now,
    sleep: Callable[[float], None] | None = None,
    monotonic: Callable[[], float] | None = None,
) -> DownloadReport:
    names = select_sources(config, only, release=release, log=log)
    env = os.environ if env is None else env
    http = http or UrllibClient()
    previous = manifest.load_entries(raw_dir)
    report = DownloadReport()
    succeeded: list[str] = []

    for name in names:
        source = config.sources[name]
        adapter_cls = get_adapter(source.adapter)
        missing_env = [var for var in adapter_cls.required_env if not env.get(var, "").strip()]
        if strict and missing_env:
            # Never fall back to manual steps or files already on disk: a strict run must
            # download fresh data. Only the variable's name is reported, never a value.
            reason = f"{', '.join(missing_env)} is not set (required with --strict)"
            report.failed[name] = reason
            log(f"[{name}] FAILED: {reason}")
            continue
        ctx = DownloadContext(
            map_config=config,
            source_name=name,
            source_config=source,
            options=parse_options(name, adapter_cls, source.options),
            raw_dir=raw_dir,
            repo_root=repo_root,
            force=force,
            http=http,
            env=env,
            previous_entries=previous,
            retries=retries,
            log=log,
            now=now,
        )
        if sleep is not None:
            ctx.sleep = sleep
        if monotonic is not None:
            ctx.monotonic = monotonic
        try:
            files = adapter_cls().download(ctx)
        except SourceError as exc:
            report.failed[name] = str(exc)
            log(f"[{name}] FAILED: {exc}")
            continue
        report.files.extend(files)
        succeeded.append(name)
        if strict and not files:
            reason = "returned no files (required with --strict)"
            report.failed[name] = reason
            log(f"[{name}] FAILED: {reason}")

    entries = manifest.merge_entries(previous, succeeded, report.files, raw_dir)
    if entries or manifest.manifest_path(raw_dir).exists():
        report.manifest_path = manifest.write_manifest(raw_dir, config.id, entries, now())
    return report
