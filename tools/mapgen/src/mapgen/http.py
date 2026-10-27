"""A small HTTP client on top of urllib, behind a Protocol so tests can stub it.

Error messages never contain an `api-key` query value: URLs are redacted before
they go into any message.
"""

import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Mapping
from pathlib import Path
from typing import Any, Protocol

from mapgen import __version__

PROJECT_URL = "https://github.com/CatastrophicCoder/roadman-simulator"
CONTACT_ENV = "MAPGEN_CONTACT"
DEFAULT_TIMEOUT_S = 60.0
_CHUNK = 1024 * 1024
_KEY_RE = re.compile(r"(api[-_]?key=)[^&#\s\"']*", re.IGNORECASE)


def redact(text: str) -> str:
    """Replace any `api-key=...` value in `text` with `***`."""
    return _KEY_RE.sub(r"\1***", text)


def redact_url(url: str) -> str:
    """Redact a URL's `api-key` query value completely, even if it holds odd characters."""
    try:
        parts = urllib.parse.urlsplit(url)
        query = urllib.parse.parse_qsl(parts.query, keep_blank_values=True)
    except ValueError:
        return redact(url)
    if not any(_KEY_RE.match(f"{k}=") for k, _ in query):
        return redact(url)
    cleaned = [(k, "***" if _KEY_RE.match(f"{k}=") else v) for k, v in query]
    query_text = urllib.parse.urlencode(cleaned, safe="*")
    return urllib.parse.urlunsplit(parts._replace(query=query_text, fragment=""))


def with_query_param(url: str, name: str, value: str) -> str:
    """Add or replace one query parameter."""
    parts = urllib.parse.urlsplit(url)
    query = [(k, v) for k, v in urllib.parse.parse_qsl(parts.query, keep_blank_values=True)]
    query = [(k, v) for k, v in query if k != name] + [(name, value)]
    return urllib.parse.urlunsplit(parts._replace(query=urllib.parse.urlencode(query)))


def user_agent(env: Mapping[str, str] | None = None) -> str:
    env = os.environ if env is None else env
    contact = env.get(CONTACT_ENV, "").strip()
    details = f"+{PROJECT_URL}" + (f"; {contact}" if contact else "")
    return f"roadman-simulator-mapgen/{__version__} ({details})"


class HttpError(Exception):
    """An HTTP request failed. `url` is already redacted."""

    def __init__(self, url: str, status: int | None, reason: str, body: str = "") -> None:
        self.url = redact_url(url)
        self.status = status
        self.reason = redact(reason)
        self.body = redact(body[:500])
        status_text = f"HTTP {status}" if status is not None else "request failed"
        detail = f": {self.body}" if self.body else ""
        super().__init__(f"{status_text} ({self.reason}) for {self.url}{detail}")


class HttpClient(Protocol):
    def get_json(self, url: str, *, timeout: float = DEFAULT_TIMEOUT_S) -> Any: ...

    def post_json(self, url: str, body: Any, *, timeout: float = DEFAULT_TIMEOUT_S) -> Any: ...

    def download(
        self,
        url: str,
        dest: Path,
        *,
        data: bytes | None = None,
        headers: Mapping[str, str] | None = None,
        timeout: float = DEFAULT_TIMEOUT_S,
    ) -> int:
        """Stream the response body to `dest` and return its size in bytes."""
        ...


def build_request(
    url: str,
    *,
    data: bytes | None = None,
    headers: Mapping[str, str] | None = None,
    env: Mapping[str, str] | None = None,
) -> urllib.request.Request:
    all_headers = {"User-Agent": user_agent(env), "Accept": "*/*"}
    all_headers.update(headers or {})
    method = "POST" if data is not None else "GET"
    return urllib.request.Request(url, data=data, headers=all_headers, method=method)


class UrllibClient:
    """The real client. Timeouts apply per socket operation (urllib semantics)."""

    def __init__(self, env: Mapping[str, str] | None = None) -> None:
        self._env = env

    def _open(self, request: urllib.request.Request, timeout: float) -> Any:
        url = request.full_url
        try:
            return urllib.request.urlopen(request, timeout=timeout)
        except urllib.error.HTTPError as exc:
            try:
                body = exc.read().decode("utf-8", errors="replace")
            except OSError:
                body = ""
            raise HttpError(url, exc.code, str(exc.reason), body) from None
        except urllib.error.URLError as exc:
            raise HttpError(url, None, str(exc.reason)) from None
        except TimeoutError:
            raise HttpError(url, None, "timed out") from None
        except Exception as exc:  # e.g. http.client.InvalidURL; its message may hold the key
            raise HttpError(url, None, f"request failed ({type(exc).__name__})") from None

    def _request(
        self,
        url: str,
        *,
        data: bytes | None = None,
        headers: Mapping[str, str] | None = None,
    ) -> urllib.request.Request:
        """Build a request; a malformed URL becomes an HttpError without the original text."""
        try:
            return build_request(url, data=data, headers=headers, env=self._env)
        except Exception as exc:  # e.g. ValueError "unknown url type: '<url with key>'"
            raise HttpError(url, None, f"invalid request ({type(exc).__name__})") from None

    def _json(self, request: urllib.request.Request, timeout: float) -> Any:
        with self._open(request, timeout) as response:
            try:
                raw = response.read()
            except Exception as exc:
                raise HttpError(
                    request.full_url, None, f"reading the response failed ({type(exc).__name__})"
                ) from None
        try:
            return json.loads(raw)
        except ValueError:
            raise HttpError(
                request.full_url, None, "response is not JSON", raw[:500].decode("utf-8", "replace")
            ) from None

    def get_json(self, url: str, *, timeout: float = DEFAULT_TIMEOUT_S) -> Any:
        request = self._request(url, headers={"Accept": "application/json"})
        return self._json(request, timeout)

    def post_json(self, url: str, body: Any, *, timeout: float = DEFAULT_TIMEOUT_S) -> Any:
        request = self._request(
            url,
            data=json.dumps(body).encode("utf-8"),
            headers={"Content-Type": "application/json", "Accept": "application/json"},
        )
        return self._json(request, timeout)

    def download(
        self,
        url: str,
        dest: Path,
        *,
        data: bytes | None = None,
        headers: Mapping[str, str] | None = None,
        timeout: float = DEFAULT_TIMEOUT_S,
    ) -> int:
        request = self._request(url, data=data, headers=headers)
        dest.parent.mkdir(parents=True, exist_ok=True)
        part = dest.with_name(dest.name + ".part")
        size = 0
        try:
            with self._open(request, timeout) as response, part.open("wb") as out:
                while chunk := response.read(_CHUNK):
                    out.write(chunk)
                    size += len(chunk)
            part.replace(dest)
        except HttpError:
            raise
        except TimeoutError:
            raise HttpError(url, None, "timed out while downloading") from None
        except Exception as exc:
            raise HttpError(url, None, f"download failed ({type(exc).__name__})") from None
        finally:
            part.unlink(missing_ok=True)
        return size
