import json
from concurrent.futures import ThreadPoolExecutor

from fastapi.testclient import TestClient

import app as sidecar
from conftest import ROOT, FakeEngine, png_bytes


def post(client, data, name="page.png"):
    return client.post("/ocr", files={"image": (name, data, "image/png")})


def test_health(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    assert resp.json() == {"engine": "fake", "engineVersion": "0", "ready": True}


def test_ocr_response_matches_schema(client, validator):
    resp = post(client, png_bytes((64, 48)))
    assert resp.status_code == 200
    body = resp.json()
    validator.validate(body)
    assert body["imageWidth"] == 64
    assert body["imageHeight"] == 48


def test_ocr_serializes_numpy_values(client):
    line = post(client, png_bytes()).json()["lines"][0]
    assert line == {
        "order": 0,
        "text": "吾輩は猫である。",
        "box": [10, 20, 30, 400],
        "vertical": True,
        "kind": "body",
        "detConf": 0.95,
        "recConf": 0.8765,
    }


def test_engine_gets_rgb(client, engine):
    post(client, png_bytes((20, 10), mode="L"))
    post(client, png_bytes((20, 10), mode="RGBA"))
    assert engine.calls == [(10, 20, 3), (10, 20, 3)]


def test_undecodable_image_is_400(client, engine):
    resp = post(client, b"this is not an image")
    assert resp.status_code == 400
    assert engine.calls == []


def test_truncated_image_is_400(client):
    resp = post(client, png_bytes((200, 200))[:60])
    assert resp.status_code == 400


def test_missing_image_field_is_422(client):
    assert client.post("/ocr").status_code == 422


def test_too_many_bytes_is_413(client, engine, monkeypatch):
    data = png_bytes()
    monkeypatch.setattr(sidecar, "MAX_BYTES", len(data) - 1)
    assert post(client, data).status_code == 413
    assert engine.calls == []


def test_too_many_pixels_is_413(client, engine, monkeypatch):
    monkeypatch.setattr(sidecar, "MAX_PIXELS", 100 * 100 - 1)
    assert post(client, png_bytes((100, 100))).status_code == 413
    assert engine.calls == []


def test_requests_run_one_at_a_time():
    engine = FakeEngine(delay=0.2)
    with TestClient(sidecar.create_app(engine_factory=lambda: engine)) as client:
        with ThreadPoolExecutor(max_workers=4) as pool:
            codes = list(pool.map(lambda _: post(client, png_bytes()).status_code, range(4)))
    assert codes == [200] * 4
    assert engine.max_active == 1


def test_ocr_threads_from_env(monkeypatch):
    monkeypatch.setenv("OCR_THREADS", "4")
    assert sidecar.ocr_threads() == 4
    monkeypatch.delenv("OCR_THREADS")
    assert sidecar.ocr_threads() == 2


def test_sample_response_matches_schema(validator):
    sample = json.loads((ROOT / "contract" / "sample-response.json").read_text())
    validator.validate(sample)
    assert [line["order"] for line in sample["lines"]] == list(range(len(sample["lines"])))
    assert any(line["kind"] == "ruby" for line in sample["lines"])
