import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from conftest import FIXTURES

ndlocr_src = Path(os.environ.get("NDLOCR_SRC", "/opt/ndlocr-lite/src"))
pytestmark = pytest.mark.skipif(not (ndlocr_src / "ocr.py").exists(), reason="NDLOCR-Lite is not installed")


@pytest.fixture(scope="module")
def engine_module():
    import engine

    return engine


def test_scored_text_survives_concatenation(engine_module):
    joined = engine_module.ScoredText("吾輩", [0.9, 0.8]) + engine_module.ScoredText("は", [0.7])
    assert joined == "吾輩は"
    assert joined.probs == [0.9, 0.8, 0.7]
    assert len(joined) == 3


def test_ruby_anchors_to_line_on_its_left_when_vertical(engine_module):
    Line = engine_module.Line
    right = Line(0, "a", [200, 0, 40, 500], True, "body", 1, 1)
    left = Line(1, "b", [100, 0, 40, 500], True, "body", 1, 1)
    # The ruby sits just right of the left column, so it belongs to that column.
    assert engine_module._ruby_anchor([142, 50, 15, 60], [right, left]) is left


def test_ruby_anchors_to_line_below_when_horizontal(engine_module):
    Line = engine_module.Line
    upper = Line(0, "a", [0, 100, 500, 30], False, "body", 1, 1)
    lower = Line(1, "b", [0, 160, 500, 30], False, "body", 1, 1)
    # Two pixels under the upper line, but ruby goes above its base text.
    assert engine_module._ruby_anchor([50, 132, 60, 15], [upper, lower]) is lower


def test_vertical_block_reads_right_to_left_whatever_ndlocr_ranked(engine_module):
    Line = engine_module.Line
    columns = [Line(0, str(i), [800 - 40 * i, 120, 30, 500], True, "body", 1, 1) for i in range(4)]
    # The order NDLOCR-Lite gave the essay page in Kavita's render: 0, 6, 5, 1
    entries = [(0, 0, 0, columns[0]), (6, 1, 0, columns[1]), (5, 2, 0, columns[2]), (1, 3, 0, columns[3])]
    assert [l.text for l in engine_module._reading_order(entries)] == ["0", "1", "2", "3"]


def test_two_tier_block_reads_the_upper_tier_first(engine_module):
    Line = engine_module.Line
    upper = [Line(0, f"u{i}", [800 - 40 * i, 100, 30, 400], True, "body", 1, 1) for i in range(2)]
    lower = [Line(0, f"l{i}", [800 - 40 * i, 600, 30, 400], True, "body", 1, 1) for i in range(2)]
    entries = [(i, i, 0, line) for i, line in enumerate([lower[1], upper[1], lower[0], upper[0]])]
    assert [l.text for l in engine_module._reading_order(entries)] == ["u0", "u1", "l0", "l1"]


def test_blocks_and_horizontal_lines_keep_ndlocr_order(engine_module):
    Line = engine_module.Line
    title = Line(0, "title", [100, 50, 600, 30], False, "title", 1, 1)
    second = Line(0, "second", [100, 300, 600, 30], False, "body", 1, 1)
    first = Line(0, "first", [100, 400, 600, 30], False, "body", 1, 1)
    # Horizontal lines stay as ranked even when that isn't top to bottom
    entries = [(2, 0, 1, first), (3, 1, 1, second), (0, 2, None, title)]
    assert [l.text for l in engine_module._reading_order(entries)] == ["title", "first", "second"]


@pytest.mark.slow
def test_real_page_with_vertical_text_and_ruby(validator):
    import app as sidecar

    with TestClient(sidecar.create_app()) as client:
        assert client.get("/health").json()["ready"] is True
        with open(FIXTURES / "neko-vertical-ruby.png", "rb") as f:
            resp = client.post("/ocr", files={"image": ("page.png", f, "image/png")})

    assert resp.status_code == 200
    body = resp.json()
    validator.validate(body)
    assert (body["imageWidth"], body["imageHeight"]) == Image.open(FIXTURES / "neko-vertical-ruby.png").size

    lines = body["lines"]
    assert [line["order"] for line in lines] == list(range(len(lines)))

    main = [line for line in lines if line["kind"] == "body"]
    assert len(main) >= 4
    assert all(line["vertical"] for line in main)
    # Vertical columns read right to left.
    xs = [line["box"][0] for line in main]
    assert xs == sorted(xs, reverse=True)
    assert main[0]["text"].startswith("吾輩は猫である")
    assert "名前はまだ無い" in "".join(line["text"] for line in main)

    ruby = [line for line in lines if line["kind"] == "ruby"]
    assert ruby and all(line["text"] for line in ruby)
    assert any("うすぐら" in line["text"] for line in ruby)
    # Each ruby line comes right after the column it annotates, or after
    # another ruby line on that column.
    for line in ruby:
        before = lines[line["order"] - 1]
        assert before["kind"] in ("body", "ruby")
        base = next(l for l in reversed(lines[:line["order"]]) if l["kind"] != "ruby")
        assert line["box"][0] >= base["box"][0] + base["box"][2] - line["box"][2]
