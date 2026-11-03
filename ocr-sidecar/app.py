"""HTTP front for the OCR engine. Kavita posts one rendered page at a time."""

import io
import logging
import os
import threading
from contextlib import asynccontextmanager

import numpy as np
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from PIL import Image

log = logging.getLogger("ocr-sidecar")

MAX_BYTES = int(os.environ.get("OCR_MAX_BYTES", 25 * 1024 * 1024))
# Kavita renders pages with the long side near 2400 px, so 40 MP leaves room
# for odd page shapes while refusing decompression bombs.
MAX_PIXELS = int(os.environ.get("OCR_MAX_PIXELS", 40_000_000))

# Pillow's own bomb check warns or raises at sizes that depend on its version.
# MAX_PIXELS replaces it.
Image.MAX_IMAGE_PIXELS = None


def ocr_threads():
    return max(1, int(os.environ.get("OCR_THREADS", "2")))


def load_ndlocr():
    from engine import NdlocrEngine

    return NdlocrEngine(threads=ocr_threads())


def load_manga():
    from engine_manga import MangaEngine

    return MangaEngine(threads=ocr_threads())


# The first engine is the default, so a request without an engine field gets
# what it got before the manga engine existed.
ENGINES = {"ndlocr": load_ndlocr, "manga": load_manga}


def serialize(engine, result):
    return {
        "engine": engine.name,
        "engineVersion": engine.version,
        "imageWidth": result.image_width,
        "imageHeight": result.image_height,
        "lines": [
            {
                "order": line.order,
                "text": line.text,
                "box": [int(v) for v in line.box],
                "vertical": bool(line.vertical),
                "kind": line.kind,
                "detConf": round(float(line.det_conf), 4),
                "recConf": round(float(line.rec_conf), 4),
                **({"block": int(line.block)} if getattr(line, "block", None) is not None else {}),
            }
            for line in result.lines
        ],
    }


def decode(data):
    try:
        with Image.open(io.BytesIO(data)) as im:
            # The header gives the size before any pixel is decoded.
            if im.width * im.height > MAX_PIXELS:
                raise HTTPException(413, f"image is {im.width}x{im.height}, over the {MAX_PIXELS} pixel limit")
            return np.array(im.convert("RGB"))
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(400, f"could not decode image: {exc}") from exc


def create_app(engine_factories=None):
    factories = engine_factories or ENGINES
    default = next(iter(factories))

    @asynccontextmanager
    async def lifespan(app):
        # Models load once, before the server accepts requests.
        app.state.engines = {key: factory() for key, factory in factories.items()}
        for key, engine in app.state.engines.items():
            log.info("engine %s (%s %s) ready", key, engine.name, engine.version)
        yield

    app = FastAPI(title="Kavita OCR sidecar", lifespan=lifespan)
    # Each engine already spreads one page over OCR_THREADS threads, so two
    # pages at once would only compete for the same cores, whatever the engine.
    lock = threading.Lock()

    def describe(engine):
        return {"engine": engine.name, "engineVersion": engine.version, "ready": bool(engine.ready)}

    @app.get("/health")
    def health():
        engines = app.state.engines
        return {
            **describe(engines[default]),
            "ready": all(engine.ready for engine in engines.values()),
            "engines": {key: describe(engine) for key, engine in engines.items()},
        }

    # Plain def endpoints run in Starlette's thread pool, so the lock blocks a
    # worker thread and never the event loop.
    @app.post("/ocr")
    def ocr(image: UploadFile = File(...), engine: str = Form(default)):
        chosen = app.state.engines.get(engine)
        if chosen is None:
            raise HTTPException(400, f"unknown engine {engine!r}, expected one of {sorted(app.state.engines)}")
        data = image.file.read(MAX_BYTES + 1)
        if len(data) > MAX_BYTES:
            raise HTTPException(413, f"image is over the {MAX_BYTES} byte limit")
        img = decode(data)
        with lock:
            result = chosen.recognize(img)
        return serialize(chosen, result)

    return app


# Nothing loads until uvicorn runs the lifespan, so importing this module in
# tests stays cheap.
app = create_app()
