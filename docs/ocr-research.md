# Japanese OCR for imported PDFs

Research notes for adding opt-in OCR to the fork, written 2026-11-02 and updated 2026-11-03 after testing on real books. The focus is Japanese books, both vertical (tategaki) novels and mixed Japanese/English learner textbooks.

## Where Kavita stands today

Kavita never reads text out of a PDF. Docnet (PDFium) only counts pages and rasterizes them for covers and OPDS, and the word-count job skips everything that isn't an EPUB (`Kavita.Services/Metadata/WordCountAnalyzerService.cs:64`). Search covers metadata only, so there's nothing for OCR text to plug into on that side yet.

The reader side is ready for it. The PDF reader is ngx-extended-pdf-viewer (pdf.js) with `[textLayer]="true"` already set (`UI/Web/src/app/pdf-reader/_components/pdf-reader/pdf-reader.component.html:23`), and it streams the original file from `GET reader/pdf`. A PDF that carries an invisible OCR text layer gets selectable text and working find in the current reader with no UI changes. That makes a searchable PDF the cheapest way to get OCR text into the reader, because anything else needs a custom overlay. The plan below builds that overlay anyway, for reasons covered there.

Since the first draft, PR #2 added Yomitan to the PDF reader. It scans `ngx-extended-pdf-viewer .textLayer` with layout-aware scanning turned off, because pdf.js positions every text run absolutely, and a toolbar button turns lookups on and off. So the dictionary pop-up already works on any PDF with a text layer, and OCR is the missing piece for scanned books.

The scanner already runs per-series follow-up work right after a series is processed. Covers and word counts are both generated at `Kavita.Services/Scanner/ScannerService.cs:364-365`, and an OCR job would be enqueued at the same point.

## What I tested

I ran every engine I could get working on this Mac against seven pages from two real books, both image-only PDFs with no existing text. *Quartet: Intermediate Japanese* is a learner textbook (1360×1920 scans) with horizontal readings, furigana printed under the kanji, numbered margin markers, and one vertical essay. *子供の科学* (August 2024) is a children's science magazine (2048×2937) with furigana over every kanji and busy magazine layouts. I hand-transcribed the body text of three of those pages to score against. That's 1,710 characters across page 200 of Quartet (vertical, two tiers of columns), page 30 of Quartet (horizontal, furigana below) and page 8 of the magazine (two horizontal columns, ruby over every kanji).

I report two numbers, because they answer different questions. The paragraph CER matches each transcribed paragraph against its closest stretch of the engine's output, so misreads, furigana spliced into the sentence, and lines read out of order all count. That's close to what a reader sees when Yomitan scans across a line break. The line CER ignores order and checks only how well each line the engine output was read, which is the fairer number for a text layer where every line sits at its own position.

| Engine | Vertical essay | Horizontal reading | Magazine, ruby | Mean paragraph CER | Line CER (range) | Time per page |
|---|---|---|---|---|---|---|
| NDLOCR-Lite (2026-10-28), CPU | 0.004 | 0.012 | 0.006 | **0.007** | 0.002–0.062 | 1–2.5 s |
| YomiToku lite, CPU, `--ignore_ruby` | 0.051 | 0.016 | 0.004 | 0.024 | 0.017–0.036 | 5–8 s |
| YomiToku full, Apple GPU, `--ignore_ruby` | 0.051 | 0.020 | 0.004 | 0.025 | 0.017–0.036 | 3.5–7 s |
| YomiToku full, ruby kept | 0.055 | 0.072 | 0.462 | 0.196 | 0.021–0.357 | 4–12 s |
| Tesseract 5 via OCRmyPDF (`jpn+jpn_vert+eng`) | 0.400 | 0.170 | 0.361 | 0.310 | 0.080–0.090 | 7–14 s |
| Apple Vision (macOS only, reference) | 1.000 | 0.025 | 0.055 | 0.360 | 0.017–0.098 | 0.2–3 s |
| PaddleOCR 3.7 (PP-OCRv6 medium), CPU | 0.612 | 0.084 | 0.729 | 0.475 | 0.018–0.052 | 28–71 s |

The times are from an Apple Silicon Mac and will differ on the media-stack server.

**NDLOCR-Lite** came out best on all three pages, at a third of YomiToku's error rate and three to five times its speed. It's the National Diet Library's own engine, CC BY 4.0, and runs on ONNX Runtime with no PyTorch, so the whole install was about 550 MB here against more than 1.2 GB for YomiToku. It handled the vertical essay almost perfectly and put the textbook's margin numbers on lines of their own, where YomiToku spliced them into the sentence. It also tags every line with a layout type, and the 67 furigana lines on the magazine page came back typed `ルビ`. Its text and JSON output leave those lines out entirely, so there's no ruby flag to emulate. Rendering the PDF at its default 150 dpi scored the same as 300 dpi. I tested the 2026-10-28 commit.

**YomiToku** was a close second, and its lite model on CPU matched the full model. Most of its remaining errors on the vertical page are the textbook's small margin numbers (the "3" marking a vocabulary note) read into the sentence, which a real novel probably wouldn't have. Its `--ignore_ruby` flag is what makes it work on learner material. With ruby kept, the furigana on the magazine page went straight into the sentences and the paragraph CER jumped from 0.004 to 0.462. It's CC BY-NC-SA 4.0, which is fine for a personal server. It's also Python with PyTorch, which is likely more than a gigabyte added to Kavita's image.

**Tesseract** did much worse on real pages than on the synthetic pages I made for the first draft (0.036 there). On the vertical essay it read some columns well and turned others into Latin noise (`SKYY MMU BR RE wh Ct eK ee`), and on both furigana pages it merged ruby into the lines and dropped 14–25% of the text altogether. Its one strength is that it installs from apt.

**Apple Vision** is excellent on horizontal Japanese and doesn't read vertical text at all. On the essay it returned 6 lines from a full page. It also only runs on macOS, so it's a reference point and not a candidate.

**PaddleOCR** reads individual lines about as well as YomiToku (line CER 1.8–5.2%), but it returns lines in detection order with no reading order, and it lists every furigana reading as its own line. It was also the slowest here by far on CPU. I didn't try PaddleOCR-VL, the model that topped the vertical-Japanese benchmark in the paper below, because it's a 0.9B-parameter vision model and impractical without a GPU.

I left **cloud vision LLMs** out. A 2025 paper (Sasagawa et al., arXiv 2511.15059) found GPT-5 scored a CER of 70.1 on real vertical Japanese documents, against PaddleOCR-VL's 20.1, and every page would go to a third party. **mokuro**, the manga tool learners use with Yomitan, targets speech bubbles and isn't a fit for books.

## What the text layer looks like to Yomitan

I wrote YomiToku's and OCRmyPDF's searchable PDFs, loaded them in pdf.js 4.10 (the engine ngx-extended-pdf-viewer wraps), and read back the text layer the way Yomitan's non-layout-aware scan walks it, in DOM order with whitespace collapsed.

On the vertical essay, YomiToku's PDF becomes one span per character (752 spans). The spans sit on top of the printed glyphs and follow reading order with no spaces between them, so a scan starting at 早 reads `早期英語教育…` straight through. That's the case PR #2 turned off layout-aware scanning for, and it should look up words correctly. Tesseract's word spaces mostly disappear in pdf.js too (3 left on that page), so the space problem I raised in the first draft turned out smaller than I expected. Its text is just too inaccurate to be worth looking up.

Furigana is the real problem. YomiToku's searchable PDF keeps the ruby even with `--ignore_ruby`, because that flag only affects its text and Markdown output. pdf.js puts the ruby spans in the DOM between the lines of body text, so the magazine page reads `…起伏がある地げつめん にけい かんが げつ形は…`. A lookup on 地 at the end of a line scans into `げつめん` instead of continuing to 形. So Kavita shouldn't use YomiToku's PDF as is.

NDLOCR-Lite can also write a text-layer PDF itself (`--sourcepdf` in, `--pdf-output` out). I checked it in pdf.js 4.10 and in 6.5, which is newer than the 6.2 build that Kavita's viewer bundles. It leaves the furigana out, and on the magazine page each line becomes one span that reads straight through with no stray spaces. It still isn't usable as is, for three reasons.

- **It misplaces the text on PDFs with a crop box.** Every Quartet page has a CropBox inside a larger MediaBox, which is common in scanned books. PDFium renders the cropped area for OCR, but the writer maps coordinates onto the full MediaBox (`ocr.py:1022`). On Quartet the layer came out about 1.35 times too large and shifted, so a hover would hit the wrong characters. pdf.js also drops text that falls outside the crop box, which cost the vertical page its first column. A three-line patch to use the CropBox put the text right on the printed glyphs.
- **pdf.js puts a space between every character on vertical lines.** It sizes the font from the column's width (9.1 pt on the essay), while the characters are spaced 10.2 pt apart. pdf.js reads that gap as a word space, so with the crop box fixed the vertical page had 493 spaces inside spans across 672 characters (`る こ と に 気 づ く`). Yomitan collapses those runs to one space without removing them, so lookups across them would fail. Horizontal lines don't have the problem.
- **Vertical punctuation comes out as presentation forms.** Its vertical font maps 。 and 、 to `︒` and `︑`. Lookups stop at punctuation anyway, but find in the reader won't match a typed 。.

So whichever engine runs, Kavita should build the text layer itself from line boxes and text, with no gaps between the characters of a vertical line. In the reader overlay planned below, that means one span per character or per line, laid out by Kavita. For a searchable PDF generated on download, it means sizing each vertical line's font to the character pitch, or placing glyphs one by one as YomiToku does. With NDLOCR-Lite's JSON there's no furigana to filter. With YomiToku's JSON Kavita would have to drop small lines that sit against a larger one, the way `--ignore_ruby` does. Either way it has to map coordinates onto the CropBox.

## What I'd build

I'd use NDLOCR-Lite as the engine and run it as a sidecar container next to Kavita, with Kavita sending page images over HTTP and getting line JSON back. NDLOCR-Lite ships no server of its own, so the sidecar is a small HTTP wrapper around its Python code. A sidecar keeps Python and the model weights out of Kavita's image, and the media-stack compose file can give it its own CPU limits. YomiToku lite would fit the same interface as a second engine. Kavita already rasterizes PDF pages for OPDS (`BookService.ExtractPdfImages`), so it can render the images to send, as long as it renders the CropBox and records which box it used.

Kavita stores what the engine returns and doesn't write a new PDF. Each page's lines go into the database with their text, box (in CropBox coordinates), direction, confidence and type, with furigana lines kept but marked. That stored text is the source for everything else. The PDF reader draws it as an overlay on each rendered page, a new endpoint serves it per page, and a searchable PDF is generated from it only when someone downloads one. Tesseract stays as a fallback engine for anyone who doesn't want to run a sidecar, with a clear note that it's poor on furigana and vertical text.

I first planned to write an OCR'd copy of each PDF and serve that, which works in today's reader with no UI changes. Storing the lines costs more up front, and I think it's worth it.

- **Text appears while OCR runs.** Each page's text shows up as soon as that page is done. A PDF copy only shows up once the whole book is finished.
- **Kavita controls what Yomitan scans.** The spacing and punctuation problems above came from fitting OCR output into PDF font metrics for pdf.js. With its own spans, Kavita puts exactly the right characters in reading order. Spans in vertical layout might also let Yomitan's layout-aware scanning work again, which PR #2 turned off for pdf.js. That would give Anki cards whole sentences across line breaks. I haven't tested that.
- **Text can be corrected and re-run cheaply.** Fixing a misread kanji updates one line. Re-running a book with a newer engine, or trying YomiToku on it, replaces its lines and leaves the file alone.
- **The same pipeline works for any page image.** Scanned books in CBZ and manga in the image reader could get text with the same storage and overlay, and stored text could later feed search.
- **The library's files stay as they are.** Kavita keeps serving the original PDF, so there's no second copy on disk and no cached file to swap. Rewriting files in the library was never an option, because Kavita treats them as the user's and a new modified time would set off a rescan.

It costs three things. The overlay has to follow pdf.js through zoom, rotation, spread modes and the CropBox, using ngx-extended-pdf-viewer's page-render events. pdf.js's find only searches its own text layer, so OCR'd text isn't searchable in the reader until Kavita adds its own find, and that's the biggest visible gap in a first version. OPDS clients, Mihon and KOReader read the file itself, so they only see OCR text through the generated PDF on download.

At 1–2.5 seconds a page, the 352-page Quartet would take roughly 6–15 minutes on one process on this Mac, against 30–45 minutes with YomiToku lite. A library's first pass will still take hours. That's why the UX below has a time window and a queue.

## Which PDFs get OCR'd, and when

**Detection.** Only libraries with OCR turned on are considered. Within those, Kavita already opens every PDF with PDFium (Docnet) to count pages, and the same reader can pull each page's text without rendering anything, so the check is cheap. A page with next to no text is a scan. A file where every page has text gets skipped, because a digital book's own text beats anything OCR would produce. A file with no text on any page, like both test books, gets queued whole. A mixed file, such as a digital book with scanned appendix pages, gets only its textless pages OCR'd. The one case this misses is a PDF with a bad text layer already in it, for example a Japanese scan someone ran through English-only OCR. Kavita could warn when a Japanese library's text layer is nearly all Latin characters, but the dependable fix is a "Re-run OCR and replace existing text" action on the series.

**Timing.** OCR can't run inline in the scan the way covers and word counts do (`ScannerService.cs:364-365`). At 1–2.5 s a page, one 352-page textbook would hold up the rest of a library scan for 6–15 minutes. So the scan only records that a file needs OCR, and a separate job works through those files afterwards. The book shows up and is readable right away with its page images, and each page gets selectable text and Yomitan lookups as soon as that page is done.

**Restarts.** Kavita's Hangfire runs on in-memory storage (`Kavita.Server/Startup.cs:205`), so a restart forgets every queued job. That's harmless for jobs that take seconds and not for an OCR backlog that might take a day. The "needs OCR" state should live in the database on `MangaFile`, next to `LastFileAnalysis`, with a recurring job that picks up whatever is still pending. The job should also save results page by page, so a restart at page 300 of 352 resumes at page 300.

**CPU.** OCR uses all the CPU it's given, and Kavita serves pages and thumbnails from the same machine. That's the main argument for the sidecar, since Docker can cap its CPU without touching Kavita. I'd also run one book at a time by default, with the optional time window letting big imports catch up overnight.

**Queue order.** Without a priority, a book added today waits behind a 5,000-page backlog. A book someone opens should jump to the front of the queue, and so should one queued from the series "Run OCR" action.

**Showing results.** Since the job saves each page's lines as it goes, the reader can fetch a page's text when that page renders, and someone with the book open sees text appear on pages OCR has reached. A SignalR event when a page or book finishes would let the open reader refetch without a reload. If the source PDF's modified time has changed since the job started, its stored lines belong to the old file and get thrown away.

**Disk space.** Stored lines take a few kilobytes a page, so OCR adds almost nothing to the library's size. The searchable PDF for downloads is generated on request and can be cached briefly, the way `GET reader/pdf` caches files today.

## How the UX would work

**Turning it on.** OCR is a per-library switch in the library settings modal, next to the existing file-type options, called "Add a text layer to PDFs (OCR)". Under it sit a language preset ("Japanese + English" by default, or "English only") and a "Skip furigana" checkbox that's on by default for Japanese. It's per library because a Japanese-textbooks library and an English-comics library generally want different settings, and OCRing comics is wasted CPU. Server settings (Media page, beside the existing PDF render resolution) hold the engine choice and sidecar URL, a cap on concurrent OCR jobs, and an "only run between these hours" window.

**What happens on import.** After a scan, every PDF in an OCR library with pages that lack text gets queued, as described above. Progress shows in the events widget the same way cover generation does, as "OCR: Quartet 1, page 42 of 352". The book stays readable the whole time with its page images, and text appears page by page as the job reaches it.

**Per-series control.** The series action menu gets "Run OCR", "Re-run OCR" (after changing a setting, or for one bad book in a good library) and "Remove OCR text". The series info tab shows the text-layer state, which is one of "Original text", "OCR'd with NDLOCR-Lite on 2026-11-03", "Queued" or "None". That line is how someone debugging a bad lookup finds out whether the text was ever real.

**In the reader.** The OCR overlay sits where pdf.js's text layer would, and Yomitan's scanning in PR #2 gets pointed at it too. PDFs that already have text keep using pdf.js's own layer. A reader toggle outlines the OCR'd lines, tinted by confidence. The original page image always shows underneath, but a learner can't see a misread kanji in an invisible layer, and the outline is how they'd notice that a lookup failed because the OCR got the character wrong and not because the dictionary lacks the word. Clicking an outlined line could open a small editor to correct it. Since furigana is stored, a later "show readings" option could also display it on demand.

**Downloads.** Downloading a book offers the original file and, for OCR'd books, a searchable PDF that Kavita generates on the spot, so someone can take the text to another reader.

## Open questions

- What hardware does the media-stack host have? NDLOCR-Lite ran at 1–2.5 s a page on an Apple Silicon CPU, and an older x86 server could easily be several times slower.
- Three pages of ground truth is a small sample. Before building on NDLOCR-Lite I'd transcribe a few more pages, including a vertical novel page with furigana, which none of the three has.
- Are manga and CBZ files in scope, or only PDFs? Stored lines make the overlay reusable in the image reader, but manga text sits in speech bubbles, which would need mokuro's approach to detection and not these book engines.
- Is losing pdf.js's find on OCR'd books acceptable for a first version, or does the overlay need its own find from the start?
- Should OCR text also feed Kavita's search? Stored lines make that possible, but search across book contents is still a larger feature.

## Sources

- [Evaluating Multimodal Large Language Models on Vertically Written Japanese Text (arXiv 2511.15059)](https://arxiv.org/html/2511.15059)
- [NDLOCR-Lite repository](https://github.com/ndl-lab/ndlocr-lite) and [NDL's announcement](https://current.ndl.go.jp/e2891)
- [YomiToku repository](https://github.com/kotaro-kinoshita/yomitoku)
- [PP-OCRv5 introduction](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv5/PP-OCRv5.html) and [the Japanese model license question](https://github.com/PaddlePaddle/PaddleOCR/discussions/18405)
- [Tesseract vertical Japanese report in makeacopy #88](https://github.com/egdels/makeacopy/issues/88) and [Arch bug on jpn_vert](https://bugs.archlinux.org/task/60951)
