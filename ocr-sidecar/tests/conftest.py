import io
import json
import sys
import threading
import time
from pathlib import Path

import numpy as np
import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import app as sidecar  # noqa: E402

FIXTURES = Path(__file__).parent / "fixtures"


class FakeLine:
    def __init__(self, order, text, box, vertical, kind, det_conf, rec_conf, block=None):
        self.order = order
        self.text = text
        self.box = box
        self.vertical = vertical
        self.kind = kind
        self.det_conf = det_conf
        self.rec_conf = rec_conf
        self.block = block


class FakeResult:
    def __init__(self, width, height, lines):
        self.image_width = width
        self.image_height = height
        self.lines = lines


class FakeEngine:
    """Returns two fixed lines and records what it was given.

    Values are numpy scalars on purpose, the way the real engine's math can
    produce them, so the tests catch anything the JSON encoder can't take.
    """

    version = "0"
    ready = True

    def __init__(self, delay=0.0, name="fake", block=None):
        self.name = name
        self.block = block
        self.delay = delay
        self.calls = []
        self.active = 0
        self.max_active = 0
        self._count_lock = threading.Lock()

    def recognize(self, img):
        with self._count_lock:
            self.active += 1
            self.max_active = max(self.max_active, self.active)
        try:
            self.calls.append(img.shape)
            time.sleep(self.delay)
            h, w = img.shape[:2]
            return FakeResult(w, h, [
                FakeLine(0, "吾輩は猫である。", [np.int64(10), 20, 30, np.int32(400)], np.bool_(True), "body",
                         np.float32(0.95), np.float64(0.87654321), self.block),
                FakeLine(1, "わがはい", [42, 20, 12, 60], True, "ruby", 0.8, 0.5, self.block),
            ])
        finally:
            with self._count_lock:
                self.active -= 1


@pytest.fixture
def engine():
    return FakeEngine()


@pytest.fixture
def client(engine):
    with TestClient(sidecar.create_app({"ndlocr": lambda: engine})) as c:
        yield c


@pytest.fixture(scope="session")
def validator():
    schema = json.loads((ROOT / "contract" / "ocr-response.schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def png_bytes(size=(64, 48), mode="RGB"):
    buf = io.BytesIO()
    Image.new(mode, size, 255 if mode == "L" else (255,) * len(mode)).save(buf, format="PNG")
    return buf.getvalue()
