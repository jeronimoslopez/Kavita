"""HTTP front for the OCR engine. Kavita posts one rendered page at a time."""

import io
import logging
import os
import threading
from contextlib import asynccontextmanager

import numpy as np
from fastapi import FastAPI, File, HTTPException, UploadFile
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


def create_app(engine_factory=load_ndlocr):
    @asynccontextmanager
    async def lifespan(app):
        # Models load once, before the server accepts requests.
        app.state.engine = engine_factory()
        log.info("engine %s %s ready", app.state.engine.name, app.state.engine.version)
        yield

    app = FastAPI(title="Kavita OCR sidecar", lifespan=lifespan)
    # The engine already spreads one page over OCR_THREADS threads, so two pages
    # at once would only compete for the same cores.
    lock = threading.Lock()

    @app.get("/health")
    def health():
        engine = app.state.engine
        return {"engine": engine.name, "engineVersion": engine.version, "ready": bool(engine.ready)}

    # Plain def endpoints run in Starlette's thread pool, so the lock blocks a
    # worker thread and never the event loop.
    @app.post("/ocr")
    def ocr(image: UploadFile = File(...)):
        data = image.file.read(MAX_BYTES + 1)
        if len(data) > MAX_BYTES:
            raise HTTPException(413, f"image is over the {MAX_BYTES} byte limit")
        img = decode(data)
        engine = app.state.engine
        with lock:
            result = engine.recognize(img)
        return serialize(engine, result)

    return app


# Nothing loads until uvicorn runs the lifespan, so importing this module in
# tests stays cheap.
app = create_app()
