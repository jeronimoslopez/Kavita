# Japanese OCR for manga archives

Research notes for extending the fork's OCR from scanned PDFs to manga in CBZ and other archives, so the image reader gets the same selectable text and Yomitan lookups the PDF reader has. Written 2026-11-03 after testing engines on pages from the Manga library. `docs/ocr-research.md` covers the PDF work this builds on.

## Where Kavita stands today

PRs #3 to #8 built OCR for PDFs only. `OcrService` decides which pages need OCR by asking PDFium how much text each page already has, and `BookService.RenderPdfPageForOcr` renders the page for the sidecar. `OcrRepository` filters every query to `MangaFormat.Pdf`, so an archive never gets checked, queued or served. The storage itself doesn't care where a page came from. `OcrPage` rows hold a MangaFile, a page index and the lines as JSON, with each box stored as a fraction of the page image.

The image reader (`manga-reader`) has five renderers. Single and double pages are `<img>` elements, split pages are drawn on a `<canvas>`, and webtoon mode is a column of `<img>` elements in the infinite scroller. Two full-height divs for turning pages (`.pagination-area`) sit on top of the image, so anything drawn under them can't be clicked or hovered. Yomitan isn't wired into this reader at all.

The reader serves archive pages from the chapter cache. `CacheService.Ensure` extracts every file of the chapter into one folder, flattens it and drops non-images, and `GetCachedPagePath` sorts what's left by natural file name to find page N.

## What I tested

I copied volume 1 of three series out of the library and picked pages that vary. *葬送のフリーレン* (2020, 764×1200) has clean modern lettering with furigana on most kanji. *漂流教室* (1970s, scanned at 635×1013) has dense dialogue, hand-lettered shouts and a sign drawn into the art. *DRAGON BALL* (1985 color edition, 1024×1536) has big hand-lettered exclamations, sound effects and colored pages. I transcribed five pages by hand and ran every engine on them. Then I transcribed two more pages that I hadn't looked at while writing the code that groups lines into bubbles, to check that I hadn't tuned it to the first five. That's 793 characters of dialogue, shouts and signs across seven pages, plus ten sound effects.

The CER below matches each transcribed bubble against its closest stretch of the engine's output for the page, after normalizing width, dashes and punctuation. A bubble read with its columns out of order, or with furigana spliced in, scores badly, because that's what a Yomitan lookup would run into. Sound effects aren't in the CER. I count one as caught if the engine read at least half of it right.

| Engine | CER, 7 pages | Bubbles caught | Sound effects caught | Seconds a page (mean) | Model size | License |
|---|---|---|---|---|---|---|
| NDLOCR-Lite, current sidecar | 0.044 | 42/42 | 0/10 | 1.4–2.8 (2.2) in Docker | ~150 MB, 572 MB image | CC BY 4.0 |
| mokuro 0.2.5 (comic-text-detector + manga-ocr) | 0.028 | 42/42 | 2/10 | 3.6–13.5 (9.1), plus 25 s to load | 444 MB + 80 MB, PyTorch 1.4 GB | GPL-3.0 detector, Apache-2.0 recognizer |
| PP-OCRv6_manga v0.2, native | 0.025 | 41/42 | 6/10 | 0.2–0.85 (0.6) | 23 MB | Apache-2.0 |
| PP-OCRv6_manga v0.2, in the sidecar image | 0.028 | 41/42 | 4/10 | 0.3–1.6 (1.0) in Docker | 23 MB | Apache-2.0 |
| PP-OCRv6_manga detector + manga-ocr-nar-small | 0.064, 5 pages | 27/28 | 4/7 | 1.4–4.2 | 23 MB + 213 MB | CC BY-NC-SA 4.0 weights |

All times are on the M2's CPU. The Docker rows ran with `--cpus 4` and four threads, which is how media-stack runs the sidecar, and they're the fair comparison for the server. The native rows ran on macOS with four threads.

**NDLOCR-Lite** did much better than I expected from a book engine. It found every speech bubble and read the clean ones nearly perfectly (no errors at all on the 漂流教室 dialogue page), and it read the framed narration on *DRAGON BALL* page 11 without a mistake, the only engine that did. Anything hand-lettered is where it breaks. It read だまれ!! as 舵時, おかあさん!! as あかあ and ギャッ as ヂヤツ, and it didn't catch one of the ten sound effects. It also labels some furigana as captions, so on page 39 of *フリーレン* the readings がたとも and たたか went into the text between body lines, and it emits bits of the art as text (`3-3`, `◆◆`, `E)`).

I ran **mokuro** because it's what people who read manga with Yomitan use today. Its detector, comic-text-detector, groups lines into one block per bubble, and manga-ocr reads each line. Only one furigana reading (こと on *フリーレン* page 39) got into its text across the seven pages. Accuracy was close to PP-OCRv6_manga, and it read the hand-lettered おかあさん!! and ほれ!! that NDLOCR-Lite missed. On this CPU it's also four to ten times slower than the other two, and it needs PyTorch, which would roughly triple the sidecar image. The detector is GPL-3.0. On the narration box it got four characters wrong (`おかしむかしゃ` for むかしむかし, `彼女` for 彼方).

**PP-OCRv6_manga** is a PaddleOCR v6 detector and recognizer that Kellenok fine-tuned on manga and published as ONNX files in September 2026. It tied for the lowest error rate, caught the most sound effects (ダダダ, ドキドキ, ゴロンゴロン, キキッ) and was the fastest by a wide margin. The whole model is 23 MB and runs on onnxruntime and OpenCV, which the sidecar already has, so adding it costs one small pip package (`pyclipper`). The catch is that the published files only detect and read lines. The demo app's furigana filter and reading order aren't released, so I wrote both for the test. My grouping joins vertical lines that sit side by side into one block and orders the block right to left. My furigana filter drops a kana-only line that's under 62% of the width of the line it hugs. With those in place both held-out pages scored about the same as the first five. The one bubble it lost (きゃーっ on *DRAGON BALL* page 24) was a detection miss, read as a horizontal line. On page 39 it split one long bubble in two where the bubble narrows, and mokuro split the same bubble at the same place.

**manga-ocr-nar-small** tops the published JMangaBench_Read numbers and reads a whole bubble at once, so I fed it the blocks PP-OCRv6_manga found. Short bubbles came back perfect. The long bubble on *漂流教室* page 19 fell apart, because the model stops at 65 output characters. It also returns one string per bubble with no line boxes, so the reader couldn't place the text over the printed lines, and its weights are non-commercial. I stopped there.

I didn't run the rest of what I looked at. YomiToku came second to NDLOCR-Lite on books and needs PyTorch as well, Apple Vision can't read vertical text, PaddleOCR-VL needs a GPU, and cloud vision models send every page to a third party.

## What detection catches

- **Speech bubbles:** NDLOCR-Lite and mokuro found all 42 and PP-OCRv6_manga found 41. Reading order inside a bubble was right everywhere except where a bubble got split, which happened once for mokuro and PP-OCRv6_manga and never for NDLOCR-Lite.
- **Narration boxes:** all three found the one box I tested, with 0, 2 and 4 characters wrong for NDLOCR-Lite, PP-OCRv6_manga and mokuro.
- **Sound effects:** PP-OCRv6_manga caught 4 of 10 in the sidecar image and 6 natively, where the two runs used different OpenCV builds. mokuro caught 2 and NDLOCR-Lite none. Nobody read the stylized モワン or トントントン.
- **Hand-lettered shouts outside bubbles:** PP-OCRv6_manga and mokuro got 7 of 8. NDLOCR-Lite got 5 and turned the rest into wrong kanji, which is worse than missing them, since Yomitan would look up an unrelated word.
- **Signs drawn into the art:** I tested one, and every engine read it.
- **Furigana:** NDLOCR-Lite marks most of it `ruby` and lets some through. PP-OCRv6_manga needs Kavita's own filter, and mine let two fragments through across seven pages. The readings on *フリーレン* are about 9 px wide at this resolution, so reading them well enough for a later "show readings" option isn't realistic.
- **Page furniture:** every engine reads page numbers and running heads. NDLOCR-Lite labels them `folio` and `header`, and the other two don't.

## What I'd build

I'd add PP-OCRv6_manga to the existing sidecar as a second engine and pick the engine per library. One container keeps media-stack simple, and the new engine adds 23 MB and one dependency to a 572 MB image. The scanned 学研まんが PDFs in the same Manga library are manga too, so the engine should follow the library and not the file format. Manga and Comic libraries would default to the `manga` engine and Book libraries to `books` (NDLOCR-Lite), with a library setting to override it.

**Sidecar.** `POST /ocr` gains an optional `engine` field, `ndlocr` by default so Kavita's current requests don't change. The manga engine returns the same contract (lines with `box`, `vertical`, `kind`, `detConf`, `recConf`) plus a `block` number, so the reader can tell which lines belong to one bubble. It labels furigana `ruby` as NDLOCR-Lite does. The grouping, ordering and furigana code from this test moves into `engine_manga.py` with tests on fixture pages, and `/health` reports both engines with their versions. The fixtures can't be pages from the library, since the repo is public, so I'd render them the way the PDF fixture was made.

**Detection.** An archive has no text layer to check, so every image page of an archive chapter in an OCR library needs OCR. I'd replace `OcrRepository`'s PDF-only filter with the set of formats OCR handles, and `DetectFileAsync` would queue every page of an archive without asking PDFium. Rows stay keyed by MangaFile and page index, and for an archive that index is the reader's page number within the chapter. At first I'd only handle chapters made of one archive, which covers every file in this library. Chapters built from several archives or from loose images need a page map across files, and that can come later.

**Rendering.** The page the worker reads has to be the page the reader shows, so the worker shouldn't open the archive its own way. It calls `CacheService.Ensure` and `GetCachedPagePath` for the chapter, exactly as the image endpoint does, and sends those bytes. JPEG, PNG and WebP go as they are, and anything Pillow can't decode (AVIF, JXL) gets converted to PNG with NetVips first. The sidecar's 25 MB and 40 MP limits are far above these pages. At about a second a page in Docker, the 13,000-page Manga library would take around four hours of one sidecar's time, against eight with NDLOCR-Lite and over 30 with mokuro.

**Reader overlay.** The single and double renderers would get a layer per `<img>`, sized to the image's drawn box and filled with the same `kavita-ocr-layer` spans the PDF reader uses. `ocr-overlay.ts` moves to a shared folder so both readers draw lines the same way, cqw and cqh units and vertical-rl included. The layer has to sit above `.pagination-area`, with `pointer-events` only on the spans, or Yomitan can't reach the text. A click on text with Yomitan on does a lookup, and any other click falls through to the page turn. Split pages on the canvas and webtoon mode each need their own geometry, so they'd come in a later PR. The reader asks `ocr/chapter` which pages have text, fetches `ocr/page` as each page shows, and refetches on the `OcrPagesReady` event, all of which already exists for PDFs.

**Yomitan.** The image reader gets the same toolbar toggle and reading-profile setting as the PDF reader, and calls `enableReaderScanning('app-manga-reader .kavita-ocr-layer', {layoutAwareScan: false})`. The lines of one bubble go into the DOM next to each other in reading order, so a lookup that runs off the bottom of one column carries on into the next column of the same bubble. Bubbles follow each other in page order, which only matters for a word split across two bubbles.

**Find.** The PDF reader's find goes through pdf.js, and the image reader has nothing like it. It would need its own search over the chapter's stored lines that jumps to the page and highlights the match with `paintOcrMatches`. I'd give it its own PR.

I'd ship this as four PRs, in this order:

1. The sidecar's manga engine, with its tests and contract changes, and nothing in Kavita calling it yet.
2. Archive detection, the per-library engine setting, the worker sending cached pages with the engine name, and the migration. After this one, Run OCR on a manga series fills `OcrPage` rows.
3. The overlay and Yomitan in the single and double renderers.
4. Find in the image reader, and the overlay for split pages and webtoon mode.

## Open questions

- Seven pages is a small sample. A run over one whole volume with spot checks would show how often the furigana filter and the bubble grouping get it wrong.
- Should the engine follow the library type, or is a per-library override needed from the start? The 学研まんが PDFs suggest manga in PDFs is common enough to want one.
- Should sound effects be selectable at all? PP-OCRv6_manga reads some of them right, and a misread one sends Yomitan to an unrelated word. Hiding lines below a confidence threshold is one option.
- Is a chapter made of one archive enough for a first version? It covers this library, and other libraries might bundle chapters differently.

## Sources

- [PP-OCRv6_manga on Hugging Face](https://huggingface.co/Kellenok/PP-OCRv6_manga)
- [mokuro](https://github.com/kha-white/mokuro), [manga-ocr](https://github.com/kha-white/manga-ocr) and [comic-text-detector](https://github.com/dmMaze/comic-text-detector)
- [manga-ocr-nar-small on Hugging Face](https://huggingface.co/muscgab/manga-ocr-nar-small) and [JMangaBench_Read](https://github.com/muscgab/JMangaBench_Read)
- [NDLOCR-Lite](https://github.com/ndl-lab/ndlocr-lite)
