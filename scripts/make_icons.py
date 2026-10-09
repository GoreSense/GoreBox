#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Генерация Assets/app.ico из геометрии XAML-логотипа (MainWindow.xaml, viewbox 24x24).

Тайл: скруглённый квадрат с градиентом #2E313B -> #14161B, обводка #3D424D, глянец сверху.
Метка «Λ»: те же пути, что в XAML (масштаб 256/24), градиент #889AB8 -> #677997
(базовый акцент #758AAC: светлее/темнее на те же доли, что ReapplyAccent в ThemeService).

Запуск: python scripts/make_icons.py   (из корня репозитория; нужен Pillow)
"""
import math
from PIL import Image, ImageChops, ImageDraw

S = 1024            # мастер ×4 для сглаживания
FINAL = 256
K = S / 24.0        # масштаб из viewbox 24


def rounded_mask(size, radius, box):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle(box, radius=radius, fill=255)
    return m


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def v_gradient(size, top, bottom):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        c = lerp(top, bottom, y / (size - 1))
        for x in range(size):
            px[x, y] = c
    return img


def d_gradient(size, c0, c1):
    """Диагональный градиент (0,0)->(1,1), как LogoGradient StartPoint/EndPoint."""
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = lerp(c0, c1, t)
    return img


def stroke(draw, p0, p1, width, fill):
    """Линия с круглыми торцами (StrokeLineCap=Round)."""
    draw.line([p0, p1], fill=fill, width=width)
    r = width / 2.0
    for p in (p0, p1):
        draw.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=fill)


def main():
    pad = 10 * (S / 256.0)          # отступ тайла в масштабе мастера
    tile_box = (pad, pad, S - pad, S - pad)
    radius = 58 * (S / 256.0)

    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 1. тайл: вертикальный градиент #2E313B -> #14161B
    grad = v_gradient(S, (0x2E, 0x31, 0x3B), (0x14, 0x16, 0x1B)).convert("RGBA")
    tile_mask = rounded_mask(S, radius, tile_box)
    base.paste(grad, (0, 0), tile_mask)

    # 2. обводка #3D424D, ширина 4/256 -> по границе тайла
    d = ImageDraw.Draw(base)
    stroke_w = int(4 * (S / 256.0))
    d.rounded_rectangle(tile_box, radius=radius, outline=(0x3D, 0x42, 0x4D, 255), width=stroke_w)

    # 3. глянец сверху: скруглён сверху, белый 0x26 -> прозрачный (наложение ПО АЛЬФЕ)
    gloss_h = pad + 100 * (S / 256.0)
    gloss = Image.new("RGBA", (S, S), (0xFF, 0xFF, 0xFF, 0))
    ga = Image.new("L", (S, S), 0)
    gpx = ga.load()
    for y in range(int(gloss_h)):
        a = int(0x26 * (1.0 - y / gloss_h))
        for x in range(S):
            gpx[x, y] = a
    gloss.putalpha(ga)
    gloss_mask = rounded_mask(S, radius, (pad, pad, S - pad, int(gloss_h)))
    mask = ImageChops.multiply(ga, gloss_mask)
    base.paste(gloss, (0, 0), mask)

    # 4. метка «Λ» — пути из XAML (viewbox 24):
    #    M12.04,5.59 L6.55,18.41  +  M12.04,5.59 L17.46,18.41, S=3.18
    #    M9.75,12.35 L13.69,18.47, S=2.35
    mark = Image.new("L", (S, S), 0)
    md = ImageDraw.Draw(mark)
    apex = (12.04 * K, 5.59 * K)
    stroke(md, apex, (6.55 * K, 18.41 * K), int(round(3.18 * K)), 255)
    stroke(md, apex, (17.46 * K, 18.41 * K), int(round(3.18 * K)), 255)
    stroke(md, (9.75 * K, 12.35 * K), (13.69 * K, 18.47 * K), int(round(2.35 * K)), 255)

    mark_grad = d_gradient(S, (0x88, 0x9A, 0xB8), (0x67, 0x79, 0x97)).convert("RGBA")
    base.paste(mark_grad, (0, 0), mark)

    final = base.resize((FINAL, FINAL), Image.LANCZOS)

    out = "src/GoreBox/Assets/app.ico"
    final.save(out, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("saved", out)


if __name__ == "__main__":
    main()
