"""Shared fixtures. No test touches the network: every HTTP call goes to FakeHttp."""

import copy
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pytest
import yaml

from mapgen.config import MapConfig, load_map_config
from mapgen.http import HttpError

REPO_ROOT = Path(__file__).resolve().parents[3]
SOMERO_YAML = REPO_ROOT / "maps" / "somero" / "map.yaml"  # only for the smoke test
FIXTURE_YAML = Path(__file__).resolve().parent / "fixtures" / "maps" / "testtown" / "map.yaml"
FIXED_NOW = datetime(2026, 10, 27, 12, 0, 0, tzinfo=UTC)
FAKE_KEY = "s3cr3t-test-key-0123456789"


@dataclass
class Call:
    method: str
    url: str
    body: Any = None
    headers: Mapping[str, str] | None = None
    timeout: float | None = None


Handler = Callable[[Call], Any]


@dataclass
class FakeHttp:
    """Answers requests from `handler`. Return bytes from a download handler to write a file;
    raise HttpError to simulate a failure."""

    handler: Handler
    calls: list[Call] = field(default_factory=list)

    def get_json(self, url: str, *, timeout: float = 60.0) -> Any:
        call = Call("GET", url, timeout=timeout)
        self.calls.append(call)
        return self.handler(call)

    def post_json(self, url: str, body: Any, *, timeout: float = 60.0) -> Any:
        call = Call("POST", url, body=body, timeout=timeout)
        self.calls.append(call)
        return self.handler(call)

    def download(
        self,
        url: str,
        dest: Path,
        *,
        data: bytes | None = None,
        headers: Mapping[str, str] | None = None,
        timeout: float = 60.0,
    ) -> int:
        call = Call("DOWNLOAD", url, body=data, headers=headers, timeout=timeout)
        self.calls.append(call)
        content = self.handler(call)
        assert isinstance(content, bytes)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(content)
        return len(content)


def http_error(status: int, url: str = "https://example.invalid/") -> HttpError:
    return HttpError(url, status, "test")


@pytest.fixture
def fixture_data() -> dict[str, Any]:
    """The frozen test map config (tests/fixtures/maps/testtown/map.yaml) as a dict."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    assert isinstance(data, dict)
    return copy.deepcopy(data)


@dataclass
class TmpRepo:
    root: Path

    def write_map(self, data: Mapping[str, Any], folder: str | None = None) -> Path:
        path = self.root / "maps" / (folder or str(data["id"])) / "map.yaml"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(yaml.safe_dump(dict(data), sort_keys=False), encoding="utf-8")
        return path

    def load(self, data: Mapping[str, Any], folder: str | None = None) -> MapConfig:
        return load_map_config(self.write_map(data, folder))

    def raw_dir(self, map_id: str) -> Path:
        return self.root / "tools" / "mapgen" / "data" / "raw" / map_id


@pytest.fixture
def tmp_repo(tmp_path: Path) -> TmpRepo:
    (tmp_path / "maps").mkdir()
    (tmp_path / "tools" / "mapgen").mkdir(parents=True)
    return TmpRepo(tmp_path)


@pytest.fixture
def fixture_config(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> MapConfig:
    return tmp_repo.load(fixture_data)
