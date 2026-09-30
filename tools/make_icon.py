"""Генерирует иконку приложения: один COM-порт расходится на три логических канала (DLC).
Запуск: python3 tools/make_icon.py  (нужен Pillow). Результат: src/MuxTerminal.App/Assets/app.ico и app.png."""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "MuxTerminal.App" / "Assets"
CHANNELS = [(80, 250, 123), (79, 193, 255), (255, 184, 108)]  # DLC1..3: зелёный, голубой, оранжевый


def draw(size: int) -> Image.Image:
    s = 8  # суперсэмплинг для сглаживания
    n = size * s
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = n * 0.04
    radius = n * 0.2
    d.rounded_rectangle([pad, pad, n - pad, n - pad], radius=radius, fill=(27, 43, 68, 255),
                        outline=(74, 106, 150, 255), width=max(s, int(n * 0.025)))
    # Толщина линий растёт на маленьких размерах, чтобы схема читалась в 16×16.
    w = int(n * (0.085 if size <= 24 else 0.065))
    hub = (n * 0.40, n * 0.50)
    # Порт (вход) слева
    d.line([(n * 0.14, hub[1]), hub], fill=(220, 220, 220, 255), width=w)
    ys = [0.27, 0.50, 0.73]
    for y, color in zip(ys, CHANNELS):
        end = (n * 0.80, n * y)
        mid = (n * 0.58, n * y)
        d.line([hub, mid, end], fill=color + (255,), width=w, joint="curve")
        r = w * 0.95
        d.ellipse([end[0] - r, end[1] - r, end[0] + r, end[1] + r], fill=color + (255,))
    r = w * 1.25
    d.ellipse([hub[0] - r, hub[1] - r, hub[0] + r, hub[1] + r], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw(sz) for sz in sizes]
    images[-1].save(OUT / "app.ico", format="ICO", sizes=[(sz, sz) for sz in sizes], append_images=images[:-1])
    images[-1].save(OUT / "app.png")
    print("written", OUT / "app.ico")


if __name__ == "__main__":
    main()
