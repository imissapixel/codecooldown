#!/usr/bin/env python3
"""Build the authored native HUD color fonts. Requires fonttools for regeneration only."""
from pathlib import Path
import unicodedata

from fontTools.colorLib.builder import buildCOLR, buildCPAL
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.unicodedata import script

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "extension/CodeUsageMonitor/Assets"
STEPS = 72
WIDTH, CELL, GAP, HEIGHT, ROW_GAP = 5000, 1000, 75, 925, 75
STRIP_HEIGHT = round(HEIGHT * 0.16)
# The strip overshoots the cells' outer and left edges so their anti-aliased
# fringe cannot show around it.
STRIP_OVERSHOOT = 20
BASE, ZOOM_BASE = 0xE000, 0xEA00
# Allowance fills continuously at the providers' 1% precision: 0..100 per row,
# 20 steps per cell; 101 means unknown.
PER_CELL, UNKNOWN = 20, 101
WEEKLY_MARKS = STEPS * 2
# Script-specific Cyrillic/Hebrew marks can split the native shaping run and lose
# base attachment. Inherited marks stay with our private-use base in one run.
INHERITED = [c for c in range(0x10000)
             if unicodedata.category(chr(c)) in ("Mn", "Me") and script(chr(c)) == "Zinh"
             and "VARIATION SELECTOR" not in unicodedata.name(chr(c), "") and c != 0x034F]
# Shapers reorder adjacent marks by canonical combining class. Fills use lower
# classes (1, 220) than strips (230), so a fill always paints before, and under,
# the strips whatever order the shaper settles on.
STRIP_MARKS = [c for c in INHERITED if unicodedata.combining(chr(c)) == 230][:STEPS * 2]
FILL_MARKS = [c for c in INHERITED if unicodedata.combining(chr(c)) in (1, 220)][:UNKNOWN + 1]
MARKS = STRIP_MARKS + FILL_MARKS
assert len(MARKS) == STEPS * 2 + UNKNOWN + 1
# Empty, weekly white, red, orange, yellow, lime, green, reset/cooldown blue.
COLORS = ["393641", "E5E2EB", "F15C70", "F2A254", "F1D56A", "BCD969", "6DCF9B", "6CB8FF"]
COOLDOWN = 7
BANDS = 8


def rectangle(pen, x, y, width, height):
    pen.moveTo((x, y)); pen.lineTo((x + width, y))
    pen.lineTo((x + width, y + height)); pen.lineTo((x, y + height)); pen.closePath()


def shapes(rectangles):
    pen = TTGlyphPen(None)
    for args in rectangles:
        rectangle(pen, *args)
    return pen.glyph()


def cells(count, y):
    return [(i * CELL, y, CELL - GAP, HEIGHT) for i in range(count)]


def rounded_cells(count, y, width=CELL - GAP, x0=0):
    pen = TTGlyphPen(None)
    radius = min(65, width // 2)
    for i in range(count):
        x = x0 + i * CELL
        pen.moveTo((x + radius, y)); pen.lineTo((x + width - radius, y))
        pen.qCurveTo((x + width, y), (x + width, y + radius)); pen.lineTo((x + width, y + HEIGHT - radius))
        pen.qCurveTo((x + width, y + HEIGHT), (x + width - radius, y + HEIGHT)); pen.lineTo((x + radius, y + HEIGHT))
        pen.qCurveTo((x, y + HEIGHT), (x, y + HEIGHT - radius)); pen.lineTo((x, y + radius))
        pen.qCurveTo((x, y), (x + radius, y)); pen.closePath()
    return pen.glyph()


def cell_band(count, y, band, width=CELL - GAP, x0=0):
    pen = TTGlyphPen(None)
    radius = min(65, width // 2)
    low, high = round(band * HEIGHT / BANDS), round((band + 1) * HEIGHT / BANDS)
    for i in range(count):
        x = x0 + i * CELL
        if band == 0:
            pen.moveTo((x + radius, y)); pen.lineTo((x + width - radius, y))
            pen.qCurveTo((x + width, y), (x + width, y + radius))
            pen.lineTo((x + width, y + high)); pen.lineTo((x, y + high)); pen.lineTo((x, y + radius))
            pen.qCurveTo((x, y), (x + radius, y)); pen.closePath()
        elif band == BANDS - 1:
            pen.moveTo((x, y + low)); pen.lineTo((x + width, y + low)); pen.lineTo((x + width, y + HEIGHT - radius))
            pen.qCurveTo((x + width, y + HEIGHT), (x + width - radius, y + HEIGHT))
            pen.lineTo((x + radius, y + HEIGHT)); pen.qCurveTo((x, y + HEIGHT), (x, y + HEIGHT - radius)); pen.closePath()
        else:
            rectangle(pen, x, y + low, width, high - low)
    return pen.glyph()


def unknown(y):
    # Authored question marks. No system-font outlines are copied or embedded.
    rects = []
    for i in range(5):
        x = i * CELL + 300
        rects += [(x, y + 660, 360, 95), (x + 265, y + 400, 95, 300),
                  (x + 115, y + 400, 210, 95), (x + 115, y + 255, 95, 200),
                  (x + 115, y + 80, 95, 95)]
    return rects


def build(single):
    family = "CodeUsageMeterSingleV4" if single else "CodeUsageMeterV4"
    glyphs = {".notdef": shapes([])}
    metrics = {".notdef": (1000, 0)}
    cmap, layers = {}, {}
    ascent = HEIGHT if single else HEIGHT * 2 + ROW_GAP

    def add(name, rects, advance=0):
        glyphs[name] = shapes(rects)
        metrics[name] = (advance, min((r[0] for r in rects), default=0))
        return name

    def base(name):
        # Base outlines are only a non-color fallback; components keep thousands
        # of meter states small.
        pen = TTGlyphPen(glyphs)
        pen.addComponent("weekly_unknown", (1, 0, 0, 1, 0, 0))
        if not single:
            pen.addComponent("short_unknown", (1, 0, 0, 1, 0, 0))
        glyphs[name] = pen.glyph()
        metrics[name] = (1000, 300)
        return name

    for row, dx in (("short", 0), ("weekly", 0), ("wmark", -1000)):
        y = HEIGHT + ROW_GAP if row == "short" else 0
        for count in range(6):
            glyphs[f"{row}_{count}_full"] = rounded_cells(count, y, x0=dx)
            metrics[f"{row}_{count}_full"] = (0, dx if count else 0)
            for band in range(BANDS):
                name = f"{row}_{count}_{band}"
                glyphs[name] = cell_band(count, y, band, x0=dx)
                metrics[name] = (0, dx if count else 0)
        add(f"{row}_unknown", [(x + dx, ry, w, h) for x, ry, w, h in unknown(y)])
        for cell in range(5):
            for part in range(1, PER_CELL):
                width = round((CELL - GAP) * part / PER_CELL)
                for band in range(-1, BANDS):
                    name = f"{row}_q_{cell}_{part}_{band}"
                    glyphs[name] = rounded_cells(1, y, width, cell * CELL + dx) if band == -1 else cell_band(1, y, band, width, cell * CELL + dx)
                    metrics[name] = (0, cell * CELL + dx)
        # One continuous track occupies exactly the five-cell footprint. Empty
        # background remains visible, so this never resizes or clips the meter.
        for percent in range(101):
            width = round((WIDTH - GAP) * percent / 100)
            for band in range(-1, BANDS):
                name = f"{row}_zoom_{percent}_{band}"
                glyphs[name] = (rounded_cells(1, y, width) if band == -1 else cell_band(1, y, band, width)) if width else shapes([])
                metrics[name] = (0, 0)

    if single:
        # Partial final cells let an exhausted weekly countdown fill to the hour.
        for cell in range(5):
            for hour in range(1, 24):
                width = round((CELL - GAP) * hour / 24)
                for band in range(-1, BANDS):
                    name = f"weekly_part_{cell}_{hour}_{band}"
                    glyphs[name] = rounded_cells(1, 0, width, cell * CELL) if band == -1 else cell_band(1, 0, band, width, cell * CELL)
                    metrics[name] = (0, cell * CELL)

    def painted(row, count, color):
        # A solid underlay prevents anti-aliasing seams between gradient layers.
        return [(f"{row}_{count}_full", color * BANDS + BANDS // 2)] + [
            (f"{row}_{count}_{band}", color * BANDS + band) for band in range(BANDS)]

    def stepped(row, steps, color):
        full, part = divmod(steps, PER_CELL)
        return painted(row, full, color) + ([(f"{row}_q_{full}_{part}_{band}", color * BANDS + (BANDS // 2 if band == -1 else band))
                                              for band in range(-1, BANDS)] if part else [])

    def weekly_layers(steps):
        return [("weekly_unknown", BANDS)] if steps == UNKNOWN else painted("weekly", 5, 0) + stepped("weekly", steps, 1)

    def continuous(row, percent, color):
        return [(f"{row}_zoom_100_-1", BANDS // 2),
                (f"{row}_zoom_{percent}_-1", color * BANDS + BANDS // 2)] + [
            (f"{row}_zoom_{percent}_{band}", color * BANDS + band) for band in range(BANDS)]

    if single:
        for weekly in range(UNKNOWN + 1):
            name = base(f"base_{weekly}")
            cmap[BASE + weekly] = name
            layers[name] = weekly_layers(weekly)
        for percent in range(101):
            name = base(f"base_reset_{percent}")
            cmap[0xE800 + percent] = name
            layers[name] = continuous("weekly", percent, COOLDOWN)
        # Cooldown countdowns: weekly days (0xE900) and 5h hours (0xEA00), each
        # cell split into 24 steps.
        for start, color, prefix in ((0xE900, COOLDOWN, "hours"), (0xEA00, COOLDOWN, "short_hours")):
            for steps in range(121):
                name = base(f"base_{prefix}_{steps}")
                cmap[start + steps] = name
                full, part = divmod(steps, 24)
                layers[name] = painted("weekly", 5, 0) + painted("weekly", full, color) + ([
                    (f"weekly_part_{full}_{part}_{band}", color * BANDS + (BANDS // 2 if band == -1 else band)) for band in range(-1, BANDS)] if part else [])
    else:
        # Bases paint the short row and the empty weekly track; the weekly fill is
        # a combining mark, keeping layer records within COLR v0 limits.
        for color in range(5):
            for short in range(UNKNOWN + 1):
                name = base(f"base_{color}_{short}")
                cmap[BASE + color * (UNKNOWN + 1) + short] = name
                top = [("short_unknown", BANDS)] if short == UNKNOWN else painted("short", 5, 0) + stepped("short", short, color + 2)
                layers[name] = top + painted("weekly", 5, 0)
        for percent in range(101):
            name = base(f"base_zoom_{percent}")
            cmap[ZOOM_BASE + percent] = name
            layers[name] = continuous("short", percent, 2) + painted("weekly", 5, 0)

    mark_classes = [[], []]
    for row in range(2):
        # Outer edges: along the top of the 5h row, along the bottom of the weekly row.
        y = HEIGHT + ROW_GAP + HEIGHT - STRIP_HEIGHT if row == 0 else -STRIP_OVERSHOOT
        track = WIDTH - GAP
        for length in range(1, STEPS + 1):
            # Continuous cooldown strip spanning the gaps; each cell is
            # roughly one time unit. Natural zero-advance placement and GPOS
            # attachment agree, so overlays stay correct even if a host skips marks.
            rects = [(-STRIP_OVERSHOOT - 1000, y, round(track * length / STEPS) + STRIP_OVERSHOOT, STRIP_HEIGHT + STRIP_OVERSHOOT)]
            index = row * STEPS + length - 1
            name = f"strip_{index}"
            add(name, rects)
            cmap[MARKS[index]] = name
            layers[name] = [(add(f"strip_ink_{index}", rects), COOLDOWN * BANDS + BANDS // 2)]
            mark_classes[row].append(name)
    for steps in range(UNKNOWN + 1):
        name = add(f"wfill_{steps}", [])
        cmap[MARKS[WEEKLY_MARKS + steps]] = name
        layers[name] = [("wmark_unknown", BANDS)] if steps == UNKNOWN else stepped("wmark", steps, 1)
        mark_classes[1].append(name)

    font = FontBuilder(1000, isTTF=True)
    font.setupGlyphOrder(list(glyphs)); font.setupCharacterMap(cmap)
    font.setupGlyf(glyphs); font.setupHorizontalMetrics(metrics)
    font.setupHorizontalHeader(ascent=ascent, descent=0)
    font.setupNameTable({"familyName": family, "styleName": "Regular", "uniqueFontIdentifier": family,
                        "fullName": family, "psName": family, "copyright": "Code Cooldown authors. MIT License."})
    font.setupOS2(sTypoAscender=ascent, sTypoDescender=0, usWinAscent=ascent, usWinDescent=0)
    font.setupPost(); font.setupMaxp()
    font.font["COLR"] = buildCOLR(layers, version=0)
    palette = []
    for color in COLORS:
        for band in range(BANDS):
            shade = 0.86 + band / (BANDS - 1) * 0.20
            palette.append(tuple(min(1, int(color[i:i + 2], 16) / 255 * shade) for i in (0, 2, 4)) + (1,))
    font.font["CPAL"] = buildCPAL([palette])
    features = ["languagesystem DFLT dflt;"]
    for row, names in enumerate(mark_classes):
        for name in names:
            features.append(f"markClass {name} <anchor -1000 0> @ROW{row};")
    features.append("feature mark {")
    for name in layers:
        if name.startswith("base_"):
            features.append(f"pos base {name} <anchor 0 0> mark @ROW0 <anchor 0 0> mark @ROW1;")
    features.append("} mark;")
    addOpenTypeFeaturesFromString(font.font, "\n".join(features))
    stem = "UsageMeter.Single" if single else "UsageMeter"
    output = ASSETS / (stem + ".ttf")
    font.save(str(output))
    print(f"{output.relative_to(ROOT)}: {output.stat().st_size} bytes")


if __name__ == "__main__":
    ASSETS.mkdir(exist_ok=True)
    build(False); build(True)
    chars = "".join(f"\\u{c:04X}" for c in MARKS)
    # Marks: short strips, weekly strips, then weekly fills (1% steps; last = unknown).
    (ROOT / "extension/CodeUsageMonitor/MeterFontMap.cs").write_text(
        "// Generated by scripts/generate-meter-font.py.\nnamespace CodeUsageMonitor;\n\n"
        f"internal static class MeterFontMap\n{{\n    internal const int Steps = {STEPS};\n"
        f"    internal const string Marks = \"{chars}\";\n}}\n")
