import http.client
import urllib.request
from pathlib import Path

import pytest

from mapgen import __version__
from mapgen.http import (
    HttpError,
    UrllibClient,
    build_request,
    redact,
    redact_url,
    user_agent,
    with_query_param,
)


def test_user_agent_without_contact() -> None:
    assert user_agent({}) == (
        f"roadman-simulator-mapgen/{__version__} "
        "(+https://github.com/CatastrophicCoder/roadman-simulator)"
    )


def test_user_agent_with_contact() -> None:
    agent = user_agent({"MAPGEN_CONTACT": "mapper@example.org"})
    assert agent.endswith("roadman-simulator; mapper@example.org)")


def test_build_request_sets_headers_and_method() -> None:
    request = build_request(
        "https://example.invalid/x",
        data=b"a=1",
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        env={},
    )
    assert request.get_method() == "POST"
    assert request.get_header("User-agent") == user_agent({})
    assert request.get_header("Content-type") == "application/x-www-form-urlencoded"
    assert build_request("https://example.invalid/x", env={}).get_method() == "GET"


def test_redact_hides_api_key() -> None:
    url = "https://h/jobs/1?a=1&api-key=SECRET&b=2"
    assert redact(url) == "https://h/jobs/1?a=1&api-key=***&b=2"
    assert "SECRET" not in redact(f"failed for {url}: body")


def test_http_error_message_is_redacted() -> None:
    error = HttpError("https://h/x?api-key=SECRET", 500, "boom", "echo api-key=SECRET")
    assert "SECRET" not in str(error)
    assert error.status == 500


def test_with_query_param_adds_and_replaces() -> None:
    assert with_query_param("https://h/p", "api-key", "k") == "https://h/p?api-key=k"
    assert with_query_param("https://h/p?api-key=old&x=1", "api-key", "k") == (
        "https://h/p?x=1&api-key=k"
    )


# Key-leak hardening in UrllibClient. No test here opens a connection.

SECRET = "SECRET-KEY-42"


def _assert_clean(error: HttpError) -> None:
    assert SECRET not in str(error)
    assert error.__cause__ is None
    assert error.__suppress_context__  # raised `from None`, so the original isn't shown


def test_malformed_url_becomes_redacted_http_error(tmp_path: Path) -> None:
    client = UrllibClient(env={})
    bad = f"api-key={SECRET}"  # no scheme: urllib raises ValueError("unknown url type: ...")
    for call in (
        lambda: client.get_json(bad),
        lambda: client.post_json(bad, {}),
        lambda: client.download(bad, tmp_path / "x"),
    ):
        with pytest.raises(HttpError) as info:
            call()
        _assert_clean(info.value)


def test_unexpected_urlopen_error_is_redacted(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    url = f"https://example.invalid/x?api-key={SECRET} with space"

    def fake_urlopen(request: urllib.request.Request, timeout: float) -> None:
        raise http.client.InvalidURL(f"URL can't contain control characters. {request.full_url!r}")

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)
    client = UrllibClient(env={})
    with pytest.raises(HttpError) as info:
        client.get_json(url)
    _assert_clean(info.value)
    assert "InvalidURL" in str(info.value)
    with pytest.raises(HttpError) as info:
        client.download(url, tmp_path / "x")
    _assert_clean(info.value)
    assert not (tmp_path / "x").exists()


def test_redact_url_handles_spaces_in_key() -> None:
    redacted = redact_url(f"https://h/p?a=1&api-key={SECRET} more&b=2")
    assert SECRET not in redacted and "more" not in redacted
    assert redacted == "https://h/p?a=1&api-key=***&b=2"
