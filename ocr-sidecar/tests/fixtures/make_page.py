"""Render the slow test's page, the opening of Natsume Soseki's 吾輩は猫である.

Soseki died in 1916, so the text is in the public domain. Run on macOS, which
ships the Hiragino Mincho font:

    python tests/fixtures/make_page.py tests/fixtures/neko-vertical-ruby.png
"""

import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONT = "/System/Library/Fonts/ヒラギノ明朝 ProN.ttc"
W, H = 1100, 1500
SIZE = 40
RUBY_SIZE = 18
TOP, BOTTOM = 110, H - 110
STEP = int(SIZE * 1.05)
COLUMN_GAP = int(SIZE * 2.1)

# (base, ruby) pairs. A plain string has no ruby.
TEXT = [
    ("吾輩", "わがはい"), "は", ("猫", "ねこ"), "である。", ("名前", "なまえ"), "はまだ無い。",
    "どこで生れたかとんと", ("見当", "けんとう"), "がつかぬ。何でも",
    ("薄暗", "うすぐら"), "いじめじめした所でニャーニャー泣いていた事だけは",
    ("記憶", "きおく"), "している。", ("吾輩", "わがはい"), "はここで始めて",
    ("人間", "にんげん"), "というものを見た。しかもあとで聞くとそれは",
    ("書生", "しょせい"), "という人間中で一番",
    ("獰悪", "どうあく"), "な種族であったそうだ。",
]
ROTATE = set("ー")
PUNCT = set("、。")


def main(out):
    img = Image.new("L", (W, H), 255)
    draw = ImageDraw.Draw(img)
    font = ImageFont.truetype(FONT, SIZE)
    ruby_font = ImageFont.truetype(FONT, RUBY_SIZE)
    x, y = W - 140, TOP

    for item in TEXT:
        base, ruby = item if isinstance(item, tuple) else (item, None)
        # Keep a ruby group in one column.
        if ruby and y + STEP * len(base) > BOTTOM:
            x, y = x - COLUMN_GAP, TOP
        start_y = y
        for ch in base:
            if y + STEP > BOTTOM:
                x, y = x - COLUMN_GAP, TOP
                start_y = y
            if ch in ROTATE:
                glyph = Image.new("L", (SIZE, SIZE), 255)
                ImageDraw.Draw(glyph).text((0, 0), ch, font=font, fill=0)
                img.paste(glyph.rotate(-90), (x, y))
            elif ch in PUNCT:
                draw.text((x + SIZE // 2, y - SIZE // 3), ch, font=font, fill=0)
            else:
                draw.text((x, y), ch, font=font, fill=0)
            y += STEP
        if ruby:
            span = y - start_y
            ruby_step = min(RUBY_SIZE + 2, span // len(ruby))
            ry = start_y + (span - ruby_step * len(ruby)) // 2
            for ch in ruby:
                draw.text((x + SIZE + 3, ry), ch, font=ruby_font, fill=0)
                ry += ruby_step

    # A slight tilt and blur so the page reads like a scan.
    img = img.rotate(0.4, resample=Image.BICUBIC, fillcolor=255)
    img = img.filter(ImageFilter.GaussianBlur(0.5))
    img.save(out, optimize=True)


if __name__ == "__main__":
    main(sys.argv[1])
