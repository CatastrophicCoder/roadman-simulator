"""`mapgen notice`: the NOTICE text from built packs' manifest credits, and `--check`."""

import json
import shutil
from pathlib import Path
from typing import Any

import pytest

from conftest import FixtureBuild
from mapgen.cli import EXIT_FAILED, EXIT_OK, EXIT_USAGE, main
from mapgen.notice import (
    HEADING,
    LICENCE_URLS,
    MODIFIED,
    NoticeError,
    expected_lines,
    missing_lines,
    read_all,
    read_pack_credits,
    render_notice,
)

CC_BY = "Licence: CC BY 4.0, https://creativecommons.org/licenses/by/4.0/"
ODBL = "Licence: ODbL 1.0, https://opendatacommons.org/licenses/odbl/1-0/"


@pytest.fixture
def repo(tmp_path: Path, fixture_build: FixtureBuild) -> Path:
    """A repo with the testtown pack and a second map `othertown` (a copy, renamed)."""
    for map_id in ("testtown", "othertown"):
        pack = tmp_path / "game" / "maps" / map_id
        shutil.copytree(fixture_build.pack_dir, pack)
        manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
        manifest["map_id"] = map_id
        manifest["display_name"] = f"Display {map_id}"
        if map_id == "othertown":
            for credit in manifest["credits"]:
                credit["attribution"] += " (other)"
        (pack / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    (tmp_path / "maps").mkdir()
    return tmp_path


def _manifest(repo: Path, map_id: str) -> dict[str, Any]:
    return json.loads((repo / "game" / "maps" / map_id / "manifest.json").read_text())


def _notice(repo: Path, tmp_path: Path, *map_ids: str) -> tuple[int, Path]:
    out = tmp_path / "out" / "NOTICE.txt"
    code = main(["--repo-root", str(repo), "notice", *map_ids, "--out", str(out)])
    return code, out


def test_notice_has_heading_credits_modification_and_licences(repo: Path, tmp_path: Path) -> None:
    code, out = _notice(repo, tmp_path, "testtown")
    assert code == EXIT_OK
    text = out.read_text(encoding="utf-8")
    lines = text.splitlines()
    assert lines[0] == HEADING
    assert MODIFIED in lines
    assert "processed into 3D map tiles" in MODIFIED
    assert "Map: Display testtown (testtown)" in lines
    for credit in _manifest(repo, "testtown")["credits"]:
        assert credit["attribution"] in lines  # exactly as in the manifest
    # The fixture credits: DEM (CC BY 4.0) and OSM (ODbL 1.0), each followed by its licence.
    assert lines[lines.index("DEM") + 1] == CC_BY
    assert lines[lines.index("© OSM contributors") + 1] == ODBL
    licences = lines[lines.index("Licences") :]
    assert f"CC BY 4.0: {LICENCE_URLS['CC BY 4.0']}" in licences
    assert f"ODbL 1.0: {LICENCE_URLS['ODbL 1.0']}" in licences


def test_licence_urls_are_listed_once_for_several_maps(repo: Path, tmp_path: Path) -> None:
    code, out = _notice(repo, tmp_path, "testtown", "othertown")
    assert code == EXIT_OK
    text = out.read_text(encoding="utf-8")
    assert "Map: Display othertown (othertown)" in text
    assert "DEM (other)" in text.splitlines()
    licences = text.splitlines()[text.splitlines().index("Licences") :]
    assert sum(LICENCE_URLS["CC BY 4.0"] in line for line in licences) == 1
    assert sum(LICENCE_URLS["ODbL 1.0"] in line for line in licences) == 1


def test_expected_lines_are_the_credits_and_licence_lines(repo: Path) -> None:
    packs = read_all(repo, ["testtown"])
    assert expected_lines(packs) == ["DEM", CC_BY, "© OSM contributors", ODBL]


def test_check_passes_on_the_written_notice(repo: Path, tmp_path: Path) -> None:
    _, out = _notice(repo, tmp_path, "testtown", "othertown")
    args = ["--repo-root", str(repo), "notice", "--check", str(out), "testtown", "othertown"]
    assert main(args) == EXIT_OK


def test_check_fails_and_names_each_missing_line(
    repo: Path, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _, out = _notice(repo, tmp_path, "testtown")
    out.write_text(out.read_text().replace("© OSM contributors\n", ""), encoding="utf-8")
    capsys.readouterr()
    args = ["--repo-root", str(repo), "notice", "--check", str(out), "testtown", "othertown"]
    assert main(args) == EXIT_FAILED
    err = capsys.readouterr().err
    assert "  © OSM contributors\n" in err
    assert "  DEM (other)\n" in err  # othertown was never written to this file


def test_check_needs_whole_lines(repo: Path) -> None:
    packs = read_all(repo, ["testtown"])
    assert "DEM" in missing_lines("DEMX\n", packs)
    assert "DEM" not in missing_lines("   DEM  \n", packs)


def test_check_of_a_missing_file_fails(
    repo: Path, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    args = ["--repo-root", str(repo), "notice", "--check", str(tmp_path / "nope"), "testtown"]
    assert main(args) == EXIT_FAILED
    assert "not found" in capsys.readouterr().err


def test_missing_pack_fails_clearly(
    repo: Path, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    code, out = _notice(repo, tmp_path, "testtown", "nowhere")
    assert code == EXIT_FAILED
    assert not out.exists()
    assert "map pack not found" in capsys.readouterr().err
    assert "mapgen build nowhere" in str(
        pytest.raises(NoticeError, read_pack_credits, repo, "nowhere").value
    )


def test_missing_manifest_fails_clearly(repo: Path, tmp_path: Path) -> None:
    (repo / "game" / "maps" / "testtown" / "manifest.json").unlink()
    with pytest.raises(NoticeError, match=r"manifest\.json not found"):
        read_pack_credits(repo, "testtown")
    assert _notice(repo, tmp_path, "testtown")[0] == EXIT_FAILED


def test_invalid_or_mismatched_manifest_fails(repo: Path) -> None:
    path = repo / "game" / "maps" / "othertown" / "manifest.json"
    manifest = _manifest(repo, "othertown")
    manifest["map_id"] = "testtown"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(NoticeError, match="is for map 'testtown'"):
        read_pack_credits(repo, "othertown")
    path.write_text("{not json", encoding="utf-8")
    with pytest.raises(NoticeError, match="invalid"):
        read_pack_credits(repo, "othertown")
    del manifest["credits"]
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(NoticeError, match="invalid"):
        read_pack_credits(repo, "othertown")


def test_unknown_licence_has_no_url_and_fails(repo: Path) -> None:
    path = repo / "game" / "maps" / "testtown" / "manifest.json"
    manifest = _manifest(repo, "testtown")
    manifest["credits"][0]["licence"] = "Some Licence 9"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(NoticeError, match="no URL known for licence 'Some Licence 9'"):
        render_notice(read_all(repo, ["testtown"]))


def test_out_or_check_is_required(repo: Path) -> None:
    with pytest.raises(SystemExit) as exc:
        main(["--repo-root", str(repo), "notice", "testtown"])
    assert exc.value.code == EXIT_USAGE
    with pytest.raises(SystemExit):
        main(["--repo-root", str(repo), "notice", "testtown", "--out", "a", "--check", "b"])


def test_invalid_map_id_in_notice_is_a_usage_error(repo: Path, tmp_path: Path) -> None:
    assert _notice(repo, tmp_path, "testtown", "../etc")[0] == EXIT_USAGE
