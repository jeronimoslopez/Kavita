"""Adapter over NDLOCR-Lite's private page pipeline.

NDLOCR-Lite only recognizes `.//LINE` elements. Ruby, running heads and page
numbers come back as BLOCK boxes with no text, so this module reads those crops
itself. It also replaces the recognizer's argmax-only `read` with one that keeps
per-character probabilities, because the pipeline's own `CONF` is the detector's
box score.
"""

import os
import sys
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from functools import partial
from pathlib import Path

import numpy as np

from page import Line, PageResult, vertical_order as _vertical_order

NDLOCR_SRC = Path(os.environ.get("NDLOCR_SRC", "/opt/ndlocr-lite/src"))
NDLOCR_COMMIT = os.environ.get("NDLOCR_COMMIT", "e57f7c6")
# Bump when the adapter changes what it returns for the same page, so Kavita
# can tell old stored lines from new ones.
ADAPTER_VERSION = "2"

sys.path.insert(0, str(NDLOCR_SRC))

import cv2  # noqa: E402
import onnxruntime  # noqa: E402

import ocr as ndl  # noqa: E402
from deim import DEIM  # noqa: E402
from parseq import PARSEQ  # noqa: E402

# Detector thresholds are NDLOCR-Lite's CLI defaults (ocr.py:1396-1398).
DET_SCORE_THRESHOLD = 0.2
DET_CONF_THRESHOLD = 0.25
DET_IOU_THRESHOLD = 0.2

LINE_KINDS = {
    "本文": "body",
    "広告文字": "body",
    "割注": "body",
    "頭注": "body",
    "キャプション": "caption",
    "タイトル本文": "title",
}
BLOCK_KINDS = {
    "ルビ": "ruby",
    "柱": "header",
    "ノンブル": "folio",
}


class ScoredText(str):
    """Recognized text that keeps one probability per character.

    `process_cascade` only measures and concatenates what `read` returns, so a
    str subclass passes through it with its probabilities intact.
    """

    def __new__(cls, text, probs):
        obj = super().__new__(cls, text)
        obj.probs = list(probs)
        return obj

    def __add__(self, other):
        return ScoredText(str(self) + str(other), self.probs + list(getattr(other, "probs", [])))


class ScoringPARSEQ(PARSEQ):
    def read(self, img):
        if img is None or img.size == 0:
            return ScoredText("", [])
        input_tensor = self.preprocess(img)
        logits = self.session.run(self.output_names, {self.input_names[0]: input_tensor})[0][0]
        indices = np.argmax(logits, axis=1)
        stop_idx = np.where(indices == 0)[0]
        end_pos = stop_idx[0] if stop_idx.size > 0 else len(indices)
        logits = logits[:end_pos]
        indices = indices[:end_pos]
        # The model emits raw logits, so softmax each position before picking.
        shifted = np.exp(logits - logits.max(axis=1, keepdims=True))
        probs = shifted[np.arange(end_pos), indices] / shifted.sum(axis=1)
        text = "".join(self.charlist[i - 1] for i in indices.tolist())
        return ScoredText(text, probs.tolist())


@contextmanager
def _ort_threads(intra):
    """Set onnxruntime's intra-op thread count for sessions created inside.

    DEIM leaves the count at onnxruntime's default, which is one thread per
    core the process can see, and inside Docker that's every host core.
    """
    real = onnxruntime.InferenceSession

    def session(path, options, **kwargs):
        options.intra_op_num_threads = intra
        options.inter_op_num_threads = 1
        return real(path, options, **kwargs)

    onnxruntime.InferenceSession = session
    try:
        yield
    finally:
        onnxruntime.InferenceSession = real


def _is_vertical(w, h):
    # The recognizer rotates a crop when h > 0.8w (parseq.py:54).
    return h > 0.8 * w


def _inside_fraction(inner, outer):
    """Fraction of box `inner`'s area that lies inside box `outer` (x, y, w, h)."""
    ix = max(0, min(inner[0] + inner[2], outer[0] + outer[2]) - max(inner[0], outer[0]))
    iy = max(0, min(inner[1] + inner[3], outer[1] + outer[3]) - max(inner[1], outer[1]))
    area = inner[2] * inner[3]
    return ix * iy / area if area else 0.0


def _reading_order(entries):
    """Put lines in reading order from (rank, doc_index, block, line) entries.

    Blocks keep NDLOCR-Lite's order, taken from the lowest ORDER among their
    lines. Inside a block of mostly vertical lines, the columns are re-ordered
    by position. NDLOCR-Lite's own ORDER can shuffle the columns of one block
    when a column is taller than its neighbours, for example with a margin number
    fused onto its top, and the order changed with small differences in how a
    page was rendered. Horizontal blocks keep NDLOCR-Lite's order.
    """
    groups = {}
    for entry in entries:
        key = entry[2] if entry[2] is not None else ("own", entry[1])
        groups.setdefault(key, []).append(entry)

    ordered = []
    for members in sorted(groups.values(), key=lambda g: min((e[0], e[1]) for e in g)):
        lines = [e[3] for e in sorted(members, key=lambda e: (e[0], e[1]))]
        if len(lines) > 1 and 2 * sum(l.vertical for l in lines) > len(lines):
            lines = _vertical_order(lines)
        ordered.extend(lines)
    return ordered


def _ruby_anchor(ruby, lines):
    """Pick the line a ruby box annotates.

    Ruby sits right of a vertical line and above a horizontal one, so the anchor
    is the nearest line on that side that overlaps the ruby along the line's axis.
    """
    rx, ry, rw, rh = ruby
    best, best_gap = None, None
    for line in lines:
        x, y, w, h = line.box
        if line.vertical:
            overlap = min(ry + rh, y + h) - max(ry, y)
            gap = rx - (x + w)
        else:
            overlap = min(rx + rw, x + w) - max(rx, x)
            gap = y - (ry + rh)
        # A ruby box often clips its base line by a few pixels.
        tolerance = 0.5 * min(rw, rh)
        if overlap <= 0 or gap < -tolerance:
            continue
        if best_gap is None or gap < best_gap:
            best, best_gap = line, gap
    return best


class NdlocrEngine:
    name = "ndlocr-lite"
    version = f"{NDLOCR_COMMIT}+adapter.{ADAPTER_VERSION}"

    def __init__(self, threads):
        self.threads = threads
        onnxruntime.disable_telemetry_events()
        cv2.setNumThreads(threads)
        # process_cascade opens a ThreadPoolExecutor with no max_workers, which
        # sizes itself from os.cpu_count().
        ndl.ThreadPoolExecutor = partial(ThreadPoolExecutor, max_workers=threads)
        self._pool = ThreadPoolExecutor(max_workers=threads, thread_name_prefix="block-recognizer")

        model = NDLOCR_SRC / "model"
        config = NDLOCR_SRC / "config"
        with _ort_threads(threads):
            self.detector = DEIM(
                model_path=str(model / "deim-s-1024x1024.onnx"),
                class_mapping_path=str(config / "ndl.yaml"),
                score_threshold=DET_SCORE_THRESHOLD,
                conf_threshold=DET_CONF_THRESHOLD,
                iou_threshold=DET_IOU_THRESHOLD,
                device="cpu",
            )
        charlist = list(ndl._load_charlist(str(config / "NDLmoji.yaml")))

        def recognizer(width, chars, epochs):
            name = f"parseq-ndl-24x{width}-{chars}-tiny-{epochs}epoch-tegaki3-r8data-202604.onnx"
            return ScoringPARSEQ(model_path=str(model / name), charlist=charlist, device="cpu")

        self.rec30 = recognizer(256, 30, 189)
        self.rec50 = recognizer(384, 50, 300)
        self.rec100 = recognizer(768, 100, 153)
        self.ready = True

    def recognize(self, img):
        """OCR one RGB page and return its lines in reading order."""
        img_h, img_w = img.shape[:2]
        result = ndl._run_ocr_on_image_array(
            self.detector, self.rec30, self.rec50, self.rec100,
            inputname="page", img=img, outputpath="", save_viz=False,
        )
        page = ET.fromstring(result["page_xml"])

        # json_lines and page_xml both come from root.findall(".//LINE") on one
        # tree, so they line up index for index.
        line_elems = page.findall(".//LINE")
        block_of = {id(line): b for b, block in enumerate(page.iter("TEXTBLOCK")) for line in block.iter("LINE")}
        texts = [entry["text"] for entry in result["json_lines"]]
        if len(line_elems) != len(texts):
            raise RuntimeError(f"NDLOCR-Lite returned {len(texts)} texts for {len(line_elems)} lines")

        ranked = []
        for doc_index, (elem, text) in enumerate(zip(line_elems, texts)):
            box = self._clip(elem, img_w, img_h)
            if box is None or not str(text):
                continue
            rank = int(elem.get("ORDER", doc_index))
            ranked.append((rank, doc_index, block_of.get(id(elem)), Line(
                order=0,
                text=str(text),
                box=box,
                vertical=_is_vertical(box[2], box[3]),
                kind=LINE_KINDS.get(elem.get("TYPE", ""), "body"),
                det_conf=float(elem.get("CONF", 0.0)),
                rec_conf=min(getattr(text, "probs", None) or [0.0]),
            )))
        lines = _reading_order(ranked)

        rubies, extras = self._read_blocks(page, img, lines)

        ordered = []
        for line in lines:
            ordered.append(line)
            mine = [r for anchor, r in rubies if anchor is line]
            mine.sort(key=lambda r: r.box[1] if line.vertical else r.box[0])
            ordered.extend(mine)
        # Ruby with no base line, and heads or folios that no line covers, have no
        # place in the reading order, so they go last, top to bottom.
        leftovers = [r for anchor, r in rubies if anchor is None] + extras
        ordered.extend(sorted(leftovers, key=lambda l: (l.box[1], l.box[0])))
        for i, line in enumerate(ordered):
            line.order = i
        return PageResult(image_width=img_w, image_height=img_h, lines=ordered)

    def _read_blocks(self, page, img, lines):
        img_h, img_w = img.shape[:2]
        blocks = []
        for elem in page.findall("BLOCK"):
            kind = BLOCK_KINDS.get(elem.get("TYPE", ""))
            box = self._clip(elem, img_w, img_h)
            if kind and box:
                blocks.append((kind, box, float(elem.get("CONF", 0.0))))

        # The detector often returns a long ruby strip plus single-character
        # boxes inside it, and two overlapping boxes for one running head.
        blocks.sort(key=lambda b: -b[2])
        kept = []
        for kind, box, conf in blocks:
            if any(k == kind and _inside_fraction(box, other) >= 0.5 for k, other, _ in kept):
                continue
            kept.append((kind, box, conf))

        to_read = []
        for kind, box, conf in kept:
            if kind != "ruby":
                # A running head or folio is often also detected as a LINE. Relabel
                # that line instead of reading the same text twice.
                covered = [l for l in lines if _inside_fraction(l.box, box) >= 0.5]
                for line in covered:
                    line.kind = kind
                if covered:
                    continue
            to_read.append((kind, box, conf))

        crops = [img[y:y + h, x:x + w, :] for _, (x, y, w, h), _ in to_read]
        texts = list(self._pool.map(self._read_crop, crops))

        rubies, extras = [], []
        for (kind, box, conf), text in zip(to_read, texts):
            if not str(text):
                continue
            line = Line(
                order=0,
                text=str(text),
                box=box,
                vertical=_is_vertical(box[2], box[3]),
                kind=kind,
                det_conf=conf,
                rec_conf=min(text.probs or [0.0]),
            )
            if kind == "ruby":
                rubies.append((_ruby_anchor(box, [l for l in lines if l.kind != "ruby"]), line))
            else:
                extras.append(line)
        return rubies, extras

    def _read_crop(self, crop):
        # The same length cascade process_cascade uses for lines.
        text = self.rec30.read(crop)
        if len(text) >= 25:
            text = self.rec50.read(crop)
        if len(text) >= 45:
            text = self.rec100.read(crop)
        return text

    @staticmethod
    def _clip(elem, img_w, img_h):
        x = max(0, int(elem.get("X")))
        y = max(0, int(elem.get("Y")))
        x2 = min(img_w, int(elem.get("X")) + int(elem.get("WIDTH")))
        y2 = min(img_h, int(elem.get("Y")) + int(elem.get("HEIGHT")))
        if x2 <= x or y2 <= y:
            return None
        return [x, y, x2 - x, y2 - y]
