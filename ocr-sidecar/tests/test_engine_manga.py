import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from conftest import FIXTURES
from page import Line

engine_manga = pytest.importorskip("engine_manga")
layout = engine_manga.layout

model_dir = Path(os.environ.get("MANGA_MODEL_DIR", "/opt/pp-ocrv6-manga"))


def column(text, x, y=100, w=30, h=300, kind="body"):
    return Line(0, text, [x, y, w, h], True, kind, 1.0, 1.0)


def test_columns_of_a_bubble_read_right_to_left():
    lines = [column("b", 440), column("c", 400), column("a", 480)]
    out = layout(lines, 1000, 1500)
    assert [l.text for l in out] == ["a", "b", "c"]
    assert {l.block for l in out} == {0}


def test_separate_bubbles_get_separate_blocks():
    right = [column("r1", 800), column("r2", 760)]
    # Far enough left that the gap is several column widths.
    left = [column("l1", 300), column("l2", 260)]
    out = layout(right + left, 1000, 1500)
    assert [l.text for l in out] == ["r1", "r2", "l1", "l2"]
    assert [l.block for l in out] == [0, 0, 1, 1]


def test_bubbles_read_in_rows_from_the_top():
    lower_right = column("lower", 800, y=900)
    upper_left = column("upper", 200, y=100)
    out = layout([lower_right, upper_left], 1000, 1500)
    assert [l.text for l in out] == ["upper", "lower"]


def test_bubble_in_two_tiers_stays_one_block_and_reads_the_upper_tier_first():
    upper = [column("u1", 700, y=50, h=80), column("u2", 670, y=50, h=80)]
    # The lower tier starts just under the upper one and further right, as in a narrowing bubble.
    lower = [column("d1", 710, y=150, h=150), column("d2", 680, y=150, h=150)]
    out = layout(lower + upper, 1000, 1500)
    assert [l.text for l in out] == ["u1", "u2", "d1", "d2"]
    assert {l.block for l in out} == {0}


def test_narrow_kana_column_beside_a_line_is_ruby_and_follows_it():
    base = column("羅生門の下で", 400, w=40)
    ruby = column("らしょうもん", 441, y=110, w=14, h=90)
    other = column("雨やみを", 340, w=40)
    out = layout([other, ruby, base], 1000, 1500)
    assert [(l.text, l.kind) for l in out] == [("羅生門の下で", "body"), ("らしょうもん", "ruby"), ("雨やみを", "body")]
    assert out[1].block == out[0].block


def test_square_two_kana_fragment_is_still_ruby():
    base = column("私の墓に", 400, w=40)
    fragment = Line(0, "わた", [441, 100, 12, 12], False, "body", 1.0, 1.0)
    out = layout([base, fragment], 1000, 1500)
    assert fragment.kind == "ruby"
    assert fragment.vertical is True
    assert out == [base, fragment]


def test_narrow_line_with_kanji_is_not_ruby():
    base = column("まぁ、イケメンである", 400, w=40)
    narrow = column("甚だ疑問", 441, w=20)
    layout([base, narrow], 1000, 1500)
    assert narrow.kind == "body"


def test_digits_at_the_page_edge_are_a_folio_and_go_last():
    folio = Line(0, "12", [480, 1450, 30, 20], False, "body", 1.0, 1.0)
    shout = Line(0, "10", [480, 700, 30, 20], False, "body", 1.0, 1.0)
    out = layout([folio, column("a", 480)], 1000, 1500)
    assert [(l.text, l.kind, l.block) for l in out] == [("a", "body", 0), ("12", "folio", None)]
    # The same digits mid-page are text, for example a number in a shout.
    assert layout([shout], 1000, 1500)[0].kind == "body"


def test_order_matches_position():
    out = layout([column("b", 440), column("a", 480), column("c", 100, y=900)], 1000, 1500)
    assert [l.order for l in out] == list(range(len(out)))


@pytest.mark.slow
@pytest.mark.skipif(not (model_dir / "ppocrv6_dict.txt").exists(), reason="PP-OCRv6_manga is not installed")
def test_real_page_with_two_bubbles_and_ruby(validator):
    import app as sidecar

    with TestClient(sidecar.create_app({"manga": sidecar.load_manga})) as client:
        assert client.get("/health").json()["ready"] is True
        with open(FIXTURES / "rashomon-bubbles.png", "rb") as f:
            resp = client.post("/ocr", files={"image": ("page.png", f, "image/png")}, data={"engine": "manga"})

    assert resp.status_code == 200
    body = resp.json()
    validator.validate(body)
    assert (body["imageWidth"], body["imageHeight"]) == Image.open(FIXTURES / "rashomon-bubbles.png").size
    assert body["engine"] == "pp-ocrv6-manga"

    lines = body["lines"]
    assert [line["order"] for line in lines] == list(range(len(lines)))
    bubbles = {}
    for line in lines:
        if line["kind"] == "body":
            bubbles.setdefault(line["block"], []).append(line["text"])
    texts = ["".join(bubbles[b]) for b in sorted(bubbles)]
    # Upper bubble first, columns right to left, no furigana spliced in.
    assert texts[0].startswith("或日の暮方の事")
    assert "羅生門の下で雨やみを待って" in texts[1]
    assert all(line["vertical"] for line in lines if line["kind"] == "body")

    ruby = [line for line in lines if line["kind"] == "ruby"]
    assert len(ruby) >= 2
    for r in ruby:
        assert lines[r["order"] - 1]["kind"] in ("body", "ruby")
    assert [line["text"] for line in lines if line["kind"] == "folio"] == ["12"]
