"""What an engine returns for one page. app.py turns it into the JSON contract."""

from dataclasses import dataclass


@dataclass
class Line:
    order: int
    text: str
    box: list[int]
    vertical: bool
    kind: str
    det_conf: float
    rec_conf: float
    # Lines with the same number are one speech bubble or caption. Only the
    # manga engine groups lines.
    block: int | None = None


@dataclass
class PageResult:
    image_width: int
    image_height: int
    lines: list[Line]


def vertical_order(lines):
    """Order vertical columns: tiers top to bottom, columns right to left within a tier.

    Columns whose vertical spans overlap share a tier, so a two-tier page keeps
    reading the whole upper tier before the lower one.
    """
    tiers = []
    for line in sorted(lines, key=lambda l: l.box[1]):
        top, bottom = line.box[1], line.box[1] + line.box[3]
        if tiers and top < tiers[-1][1]:
            tiers[-1][1] = max(tiers[-1][1], bottom)
            tiers[-1][2].append(line)
        else:
            tiers.append([top, bottom, [line]])
    return [line for _, _, members in tiers for line in sorted(members, key=lambda l: -(l.box[0] + l.box[2]))]
