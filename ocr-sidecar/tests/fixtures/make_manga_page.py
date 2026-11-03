"""Render the manga engine's slow-test page: two speech bubbles of vertical text
with ruby, and a page number.

The text opens Akutagawa Ryunosuke's 羅生門. Akutagawa died in 1927, so it is in
the public domain. Run on macOS, which ships the Hiragino fonts:

    python tests/fixtures/make_manga_page.py tests/fixtures/rashomon-bubbles.png
"""

import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONT = "/System/Library/Fonts/ヒラギノ角ゴシック W6.ttc"
W, H = 900, 1300
SIZE = 34
RUBY_SIZE = 14
STEP = int(SIZE * 1.05)
COLUMN_GAP = int(SIZE * 1.9)

# Each bubble: the right edge of its first column, its top, and its columns.
# A column is a list of (base, ruby) pairs, and a plain string has no ruby.
BUBBLES = [
    (700, 140, [
        ["或日の"],
        [("暮方", "くれがた"), "の事"],
        ["である。"],
    ]),
    (470, 680, [
        ["一人の", ("下人", "げにん"), "が、"],
        [("羅生門", "らしょうもん"), "の下で"],
        ["雨やみを待って"],
        ["いた。"],
    ]),
]
FOLIO = "12"
PUNCT = set("、。")


def draw_bubble(img, draw, font, ruby_font, right, top, columns):
    x = right - SIZE
    bottom = top
    for column in columns:
        y = top
        for item in column:
            base, ruby = item if isinstance(item, tuple) else (item, None)
            start_y = y
            for ch in base:
                if ch in PUNCT:
                    draw.text((x + SIZE // 2, y - SIZE // 3), ch, font=font, fill=0)
                else:
                    draw.text((x, y), ch, font=font, fill=0)
                y += STEP
            if ruby:
                span = y - start_y
                ruby_step = min(RUBY_SIZE + 1, span // len(ruby))
                ry = start_y + (span - ruby_step * len(ruby)) // 2
                for ch in ruby:
                    draw.text((x + SIZE + 2, ry), ch, font=ruby_font, fill=0)
                    ry += ruby_step
        bottom = max(bottom, y)
        x -= COLUMN_GAP
    left = x + COLUMN_GAP
    # An ellipse needs a wide margin to clear the text at its corners.
    draw.ellipse([left - 110, top - 80, right + RUBY_SIZE + 110, bottom + 80], outline=0, width=4)


def main(out):
    img = Image.new("L", (W, H), 255)
    draw = ImageDraw.Draw(img)
    font = ImageFont.truetype(FONT, SIZE)
    ruby_font = ImageFont.truetype(FONT, RUBY_SIZE)
    # Panel borders, so the page looks like manga and not a book.
    draw.rectangle([30, 30, W - 30, 500], outline=0, width=5)
    draw.rectangle([30, 520, W - 30, H - 90], outline=0, width=5)
    for right, top, columns in BUBBLES:
        draw_bubble(img, draw, font, ruby_font, right, top, columns)
    draw.text((W // 2 - 12, H - 60), FOLIO, font=ImageFont.truetype(FONT, 22), fill=0)

    img = img.filter(ImageFilter.GaussianBlur(0.4))
    img.save(out, optimize=True)


if __name__ == "__main__":
    main(sys.argv[1])
