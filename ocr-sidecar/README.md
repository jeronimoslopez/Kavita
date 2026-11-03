# OCR sidecar

Kavita can't look up words on a scanned PDF page because the page has no text layer. This sidecar reads the text off a page image with [NDLOCR-Lite](https://github.com/ndl-lab/ndlocr-lite), the National Diet Library's Japanese OCR, and returns each line with its box so Kavita can lay an invisible, selectable text layer over the page for the Yomitan pop-up dictionary.

It's a small FastAPI service that takes one page image per request. Kavita renders the PDF page itself and stores what comes back, so the sidecar holds no state and never sees a PDF.

## Build and run

The image pins NDLOCR-Lite to commit `e57f7c6`, and its repo includes the models, so the build fetches about 150 MB from GitHub. Only linux/arm64 has been tested so far.

```bash
docker build -t kavita-ocr-sidecar ocr-sidecar
```

```bash
docker run --rm -p 8000:8000 -e OCR_THREADS=4 --cpus 4 kavita-ocr-sidecar
```

Models load before the server starts listening, which takes a few seconds. Then send it a page:

```bash
curl -s -F image=@page.png http://localhost:8000/ocr
```

The sidecar has no authentication. Don't publish its port outside a trusted network. In media-stack it runs with no published port, and Kavita reaches it at `http://ocr:8000` over the compose network.

### Settings

| Variable | Default | Meaning |
|---|---|---|
| `OCR_THREADS` | `2` | Threads for the detector and for the pool that runs line recognition. Set it to the container's CPU limit. |
| `OCR_MAX_BYTES` | `26214400` | Uploads over this many bytes get 413. |
| `OCR_MAX_PIXELS` | `40000000` | Images whose width times height is over this get 413. |

You have to set `OCR_THREADS` by hand, because Python's `os.cpu_count()` reports every host core inside Docker, whatever `--cpus` says. Left to their defaults, NDLOCR-Lite's thread pool and onnxruntime would start that many threads and compete for the few cores the container gets.

The sidecar runs one page at a time. A second request waits until the first one's done, since one page already keeps `OCR_THREADS` cores busy.

## API

`GET /health` returns `{"engine": "ndlocr-lite", "engineVersion": "e57f7c6+adapter.1", "ready": true}`. It answers once the models are loaded.

`POST /ocr` takes a multipart form with the page in a field named `image`, in any format Pillow reads (PNG and JPEG in practice). It answers with these codes:

| Code | When |
|---|---|
| 200 | The page was read. A blank page gives an empty `lines` array. |
| 400 | The bytes aren't an image Pillow can decode. |
| 413 | The upload is over `OCR_MAX_BYTES`, or the image is over `OCR_MAX_PIXELS`. |
| 422 | The request has no `image` field. |

## Contract

[`contract/ocr-response.schema.json`](contract/ocr-response.schema.json) is the JSON Schema for a 200 response, and [`contract/sample-response.json`](contract/sample-response.json) is what the real sidecar returned for the test page in `tests/fixtures`. Kavita's tests read the sample, so it's worth regenerating whenever the response changes.

```json
{
  "engine": "ndlocr-lite",
  "engineVersion": "e57f7c6+adapter.1",
  "imageWidth": 1100,
  "imageHeight": 1500,
  "lines": [
    { "order": 0, "text": "吾輩は猫である。名前はまだ無い。…", "box": [957, 107, 46, 1251],
      "vertical": true, "kind": "body", "detConf": 0.947, "recConf": 0.9988 }
  ]
}
```

| Field | Meaning |
|---|---|
| `order` | Reading order, 0-based, equal to the line's index. A ruby line comes right after the line it annotates. |
| `text` | The recognized text. It's never empty, because lines the recognizer reads as nothing are dropped. |
| `box` | `[x, y, width, height]` in pixels of the posted image, origin top left. Kavita divides by `imageWidth` and `imageHeight` to store fractions. |
| `vertical` | True when the box's height is over 0.8 times its width, the rule NDLOCR-Lite's recognizer uses to decide whether to rotate a crop. |
| `kind` | One of `body`, `caption`, `title`, `ruby`, `header` (a running head) or `folio` (a page number). |
| `detConf` | The layout detector's score for the box, from 0 to 1. |
| `recConf` | The lowest per-character probability the recognizer gave the line, from 0 to 1. One doubtful character pulls the whole line down. |

`engineVersion` changes when NDLOCR-Lite's commit changes or when the adapter changes what it returns for the same page. Kavita can compare it with what it stored to decide whether a book needs another pass.

## How it uses NDLOCR-Lite

`engine.py` calls NDLOCR-Lite's private `_run_ocr_on_image_array` from `src/ocr.py` and reads lines from the layout XML it returns, sorted by NDLOCR-Lite's own reading-order pass.

NDLOCR-Lite never reads furigana. Ruby, running heads and page numbers come back as boxes with no text. The adapter crops each ruby box and runs the recognizer on it, then places it after the nearest line on the left of the ruby (vertical text) or below it (horizontal text). When a running-head or page-number box already contains a detected line, the adapter relabels that line. Otherwise it reads the box itself.

The pipeline's per-line `CONF` is the detector's box score, and its recognizer keeps only the argmax at each position. The adapter subclasses the recognizer to softmax the logits and keep each chosen character's probability, which becomes `recConf`.

The JSON NDLOCR-Lite writes sets `class_index` to 1 for every line, so the adapter can't use it and maps the XML `TYPE` attribute to `kind` instead.

Because `_run_ocr_on_image_array` is private, an NDLOCR-Lite upgrade could break the adapter without warning. The slow test runs the real models on the fixture page and checks vertical lines, a read ruby line and reading order, so run it after changing the pinned commit.

## Tests

The test image adds pytest and runs everything, including the slow test against the real models:

```bash
docker build --target test -t kavita-ocr-sidecar:test ocr-sidecar
```

```bash
docker run --rm kavita-ocr-sidecar:test
```

Add `-m "not slow"` after the image name to skip the real-model test. The other tests don't load any models. They use a fake engine through FastAPI's `TestClient`.

The fixture page `tests/fixtures/neko-vertical-ruby.png` sets the opening of Natsume Sōseki's 吾輩は猫である, which is in the public domain, in vertical type with ruby. `tests/fixtures/make_page.py` renders it on macOS.

To regenerate the sample response, run the sidecar and post the fixture:

```bash
curl -s -F image=@ocr-sidecar/tests/fixtures/neko-vertical-ruby.png http://localhost:8000/ocr | python3 -m json.tool --no-ensure-ascii > ocr-sidecar/contract/sample-response.json
```

## Attribution

This sidecar uses NDLOCR-Lite by the National Diet Library, Japan ([ndl-lab/ndlocr-lite](https://github.com/ndl-lab/ndlocr-lite), commit `e57f7c6`), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The image fetches its code and models unmodified at build time and copies its `LICENCE` and `LICENCE_DEPENDENCEIES` to `/opt/ndlocr-lite/`. `engine.py` subclasses NDLOCR-Lite's PARSEQ recognizer to keep per-character probabilities and calls its page pipeline from outside. It changes no NDLOCR-Lite file.
