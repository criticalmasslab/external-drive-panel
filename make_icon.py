# -*- coding: utf-8 -*-
"""exe/설치파일에 사용할 icon.ico 를 생성한다. (Pillow 필요)
사용법: python make_icon.py
"""
from PIL import Image, ImageDraw


def make_icon_image(size=256):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    s = size / 64
    draw.rounded_rectangle((6 * s, 14 * s, 58 * s, 50 * s), radius=8 * s, fill=(58, 142, 230, 255))
    draw.rectangle((14 * s, 22 * s, 50 * s, 30 * s), fill=(255, 255, 255, 255))
    draw.ellipse((42 * s, 38 * s, 50 * s, 46 * s), fill=(255, 255, 255, 255))
    return img


if __name__ == "__main__":
    img = make_icon_image(256)
    img.save(
        "icon.ico",
        sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("icon.ico 생성 완료")
