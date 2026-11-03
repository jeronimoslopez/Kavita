"""PP-OCRv6_manga, a PaddleOCR v6 detector and recognizer fine-tuned on manga.

The published ONNX files find and read single lines. The model card's demo app
also filters furigana and orders the text, but that code isn't released, so
`layout` does both here: it groups the columns of a speech bubble into a block,
orders the blocks and the columns, and marks furigana as ruby.
"""

import os
import re
from pathlib import Path

import cv2
import numpy as np
import onnxruntime
import pyclipper

from page import Line, PageResult, vertical_order

MODEL_DIR = Path(os.environ.get("MANGA_MODEL_DIR", "/opt/pp-ocrv6-manga"))
MODEL_REVISION = os.environ.get("MANGA_MODEL_REVISION", "02af751")
# Bump when the adapter changes what it returns for the same page, so Kavita
# can tell old stored lines from new ones.
ADAPTER_VERSION = "1"

# Detector settings the model card recommends (examples/onnx_openvino.py).
DET_LONG_SIDE = 960
DET_THRESHOLD = 0.15
BOX_THRESHOLD = 0.25
UNCLIP_RATIO = 1.4
REC_HEIGHT = 48

# A kana-only column this much narrower than the column beside it is furigana.
RUBY_WIDTH_RATIO = 0.62
# Page numbers sit in this fraction of the page at the top or bottom.
FOLIO_BAND = 0.08
# Blocks whose tops fall in one band of this fraction of the page height share a row.
ROW_BAND = 0.1

KANA = re.compile(r"[぀-ヿー]+")
DIGITS = re.compile(r"\d{1,4}")


class MangaEngine:
    name = "pp-ocrv6-manga"
    version = f"v0.2-{MODEL_REVISION}+adapter.{ADAPTER_VERSION}"

    def __init__(self, threads, model_dir=MODEL_DIR):
        onnxruntime.disable_telemetry_events()
        cv2.setNumThreads(threads)
        options = onnxruntime.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        providers = ["CPUExecutionProvider"]
        self.detector = onnxruntime.InferenceSession(str(model_dir / "manga_det_v0.2.onnx"), options, providers=providers)
        self.recognizer = onnxruntime.InferenceSession(str(model_dir / "manga_rec_v0.2.onnx"), options, providers=providers)
        with open(model_dir / "ppocrv6_dict.txt", encoding="utf-8") as f:
            # Index 0 is the CTC blank, and the model's last class is a space.
            self.vocab = [""] + [line.rstrip("\r\n") for line in f] + [" "]
        self.ready = True

    def recognize(self, img):
        """OCR one RGB page and return its lines in reading order."""
        img_h, img_w = img.shape[:2]
        # The model was trained on OpenCV's BGR channel order.
        bgr = np.ascontiguousarray(img[:, :, ::-1])
        lines = []
        for box, det_conf in self._detect(bgr):
            x, y, w, h = box
            text, rec_conf = self._read(bgr[y:y + h, x:x + w])
            if text.strip():
                lines.append(Line(order=0, text=text, box=box, vertical=_is_vertical(w, h, text),
                                  kind="body", det_conf=det_conf, rec_conf=rec_conf))
        return PageResult(image_width=img_w, image_height=img_h, lines=layout(lines, img_w, img_h))

    def _detect(self, bgr):
        """Return ([x, y, w, h], score) for each text line the detector finds."""
        img_h, img_w = bgr.shape[:2]
        scale = DET_LONG_SIDE / max(img_h, img_w)
        det_h = max(32, int(round(img_h * scale / 32)) * 32)
        det_w = max(32, int(round(img_w * scale / 32)) * 32)
        inp = cv2.resize(bgr, (det_w, det_h)).astype(np.float32) / 255.0
        inp = (inp - np.array([0.485, 0.456, 0.406], np.float32)) / np.array([0.229, 0.224, 0.225], np.float32)
        pred = self.detector.run(None, {self.detector.get_inputs()[0].name: inp.transpose(2, 0, 1)[None]})[0][0, 0]

        contours, _ = cv2.findContours(((pred > DET_THRESHOLD) * 255).astype(np.uint8), cv2.RETR_LIST,
                                       cv2.CHAIN_APPROX_SIMPLE)
        boxes = []
        for contour in contours:
            pts = contour.squeeze(1)
            if pts.ndim != 2 or len(pts) < 4:
                continue
            score = _box_score(pred, pts)
            if score < BOX_THRESHOLD:
                continue
            expanded = _unclip(pts.astype(np.float32), UNCLIP_RATIO)
            if expanded is None:
                continue
            rect = cv2.minAreaRect(expanded)
            if min(rect[1]) < 3:
                continue
            corners = cv2.boxPoints(rect)
            x1 = int(np.clip(round(corners[:, 0].min() / det_w * img_w), 0, img_w))
            x2 = int(np.clip(round(corners[:, 0].max() / det_w * img_w), 0, img_w))
            y1 = int(np.clip(round(corners[:, 1].min() / det_h * img_h), 0, img_h))
            y2 = int(np.clip(round(corners[:, 1].max() / det_h * img_h), 0, img_h))
            if x2 > x1 and y2 > y1:
                boxes.append(([x1, y1, x2 - x1, y2 - y1], score))
        return boxes

    def _read(self, crop):
        """Read one line crop and return (text, lowest probability among its characters)."""
        if crop.shape[0] > crop.shape[1]:
            crop = cv2.rotate(crop, cv2.ROTATE_90_COUNTERCLOCKWISE)
        h, w = crop.shape[:2]
        natural_w = int(round(REC_HEIGHT * w / max(1, h)))
        # The model card's rule: very long lines get a wider canvas instead of being squeezed.
        target_w = max(16, min(2400 if natural_w > 2000 else 640, natural_w))
        inp = cv2.resize(crop, (target_w, REC_HEIGHT)).astype(np.float32) / 255.0
        inp = ((inp - 0.5) / 0.5).transpose(2, 0, 1)[None]
        # The model's output is already softmaxed.
        probs = self.recognizer.run(None, {self.recognizer.get_inputs()[0].name: inp})[0][0]
        indices = probs.argmax(axis=1)

        chars, scores = [], []
        for t, index in enumerate(indices):
            if index == 0 or (t > 0 and index == indices[t - 1]):
                continue
            chars.append(self.vocab[index])
            scores.append(float(probs[t, index]))
        return "".join(chars), min(scores, default=0.0)


def layout(lines, img_w, img_h):
    """Mark ruby and folios, group lines into blocks, and number them in reading order.

    A block is the columns of one speech bubble or caption. Blocks read in rows
    from the top of the page, right to left within a row. A block's vertical
    columns read right to left and its horizontal lines top to bottom, and each
    ruby line comes right after the line it annotates.
    """
    rubies = {}
    for line in lines:
        anchor = _ruby_anchor(line, lines)
        if anchor is not None:
            line.kind = "ruby"
            line.vertical = True
            rubies.setdefault(id(anchor), []).append(line)

    for line in lines:
        y, h = line.box[1], line.box[3]
        if line.kind == "body" and DIGITS.fullmatch(line.text.strip()) and \
                (y + h < FOLIO_BAND * img_h or y > (1 - FOLIO_BAND) * img_h):
            line.kind = "folio"

    body = [line for line in lines if line.kind == "body"]
    blocks = _blocks(body)
    blocks.sort(key=lambda b: (int(_bounds(b)[1] / (ROW_BAND * img_h)), -_bounds(b)[2]))

    ordered = []
    for block_index, block in enumerate(blocks):
        for line in block:
            line.block = block_index
            ordered.append(line)
            for ruby in sorted(rubies.get(id(line), []), key=lambda r: r.box[1]):
                ruby.block = block_index
                ordered.append(ruby)
    ordered.extend(sorted((l for l in lines if l.kind == "folio"), key=lambda l: (l.box[1], l.box[0])))

    for i, line in enumerate(ordered):
        line.order = i
    return ordered


def _is_vertical(w, h, text):
    # A lone character in a square box is far more often a vertical bubble's
    # last column than a horizontal line.
    return h > 1.2 * w or (len(text) <= 1 and h >= w)


def _ruby_anchor(line, lines):
    """The column a kana-only line annotates, or None if it isn't furigana.

    Furigana sits just right of its column, much narrower, and overlaps it along
    the column.
    """
    # A two-kana fragment can come out as a square box, so this checks the shape
    # loosely and doesn't rely on line.vertical.
    if line.box[3] < line.box[2] or not KANA.fullmatch(line.text):
        return None
    x, y, w, h = line.box
    best, best_gap = None, None
    for other in lines:
        if other is line or not other.vertical:
            continue
        ox, oy, ow, oh = other.box
        if w >= RUBY_WIDTH_RATIO * ow:
            continue
        gap = abs(x - (ox + ow))
        overlap = min(y + h, oy + oh) - max(y, oy)
        if gap < 0.6 * ow and overlap > 0.3 * h and (best_gap is None or gap < best_gap):
            best, best_gap = other, gap
    return best


def _blocks(lines):
    """Group lines of one orientation that sit next to each other, as the columns of a bubble do."""
    parent = list(range(len(lines)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i, a in enumerate(lines):
        for j in range(i + 1, len(lines)):
            b = lines[j]
            if a.vertical == b.vertical and _neighbours(a, b):
                parent[find(i)] = find(j)

    groups = {}
    for i, line in enumerate(lines):
        groups.setdefault(find(i), []).append(line)
    blocks = []
    for members in groups.values():
        if members[0].vertical:
            members = vertical_order(members)
        else:
            members.sort(key=lambda l: l.box[1])
        blocks.append(members)
    return blocks


def _neighbours(a, b):
    ax, ay, aw, ah = a.box
    bx, by, bw, bh = b.box
    if a.vertical:
        thickness = max(aw, bw)
        gap = max(ax, bx) - min(ax + aw, bx + bw)
        overlap = min(ay + ah, by + bh) - max(ay, by)
        # Columns of one bubble start near the same height.
        if gap < 0.9 * thickness and overlap > 0 and abs(ay - by) < 2.5 * thickness:
            return True
        # A bubble that narrows can set its text in two tiers, the lower tier
        # starting just under the upper one.
        below = max(ay, by) - min(ay + ah, by + bh)
        across = min(ax + aw, bx + bw) - max(ax, bx)
        return 0 <= below < 2.5 * thickness and across > 0
    thickness = max(ah, bh)
    gap = max(ay, by) - min(ay + ah, by + bh)
    overlap = min(ax + aw, bx + bw) - max(ax, bx)
    return gap < 0.9 * thickness and overlap > 0


def _bounds(block):
    return (min(l.box[0] for l in block), min(l.box[1] for l in block),
            max(l.box[0] + l.box[2] for l in block), max(l.box[1] + l.box[3] for l in block))


def _box_score(pred, pts):
    """Mean detector probability inside a contour, as PaddleOCR's DB post-processing scores it."""
    h, w = pred.shape
    x1, x2 = int(np.clip(pts[:, 0].min(), 0, w - 1)), int(np.clip(pts[:, 0].max(), 0, w - 1))
    y1, y2 = int(np.clip(pts[:, 1].min(), 0, h - 1)), int(np.clip(pts[:, 1].max(), 0, h - 1))
    mask = np.zeros((y2 - y1 + 1, x2 - x1 + 1), dtype=np.uint8)
    cv2.fillPoly(mask, [(pts - [x1, y1]).astype(np.int32).reshape(-1, 2)], 1)
    return cv2.mean(pred[y1:y2 + 1, x1:x2 + 1], mask)[0]


def _unclip(pts, ratio):
    """Grow a contour by the margin the detector's shrunk text kernels lost."""
    x, y = pts[:, 0], pts[:, 1]
    area = 0.5 * abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))
    perimeter = np.linalg.norm(np.roll(pts, -1, axis=0) - pts, axis=1).sum()
    if area <= 0 or perimeter <= 0:
        return None
    offset = pyclipper.PyclipperOffset()
    offset.AddPath(pts.tolist(), pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
    paths = offset.Execute(area * ratio / perimeter)
    return np.asarray(paths[0], dtype=np.float32) if len(paths) == 1 else None
