"""Render the model-and-graph application icon at Windows icon sizes."""

from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent
SIZES = (16, 24, 32, 48, 64, 128, 256)
BACKGROUND = "#182334"
BORDER = "#354B68"
LINE = "#E7F0FB"
NODE = "#60A5FA"


def render(size: int) -> Image.Image:
    canvas_size = size * 8
    scale = canvas_size / 256
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def box(values: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
        return tuple(round(value * scale) for value in values)

    def line(points: tuple[tuple[int, int], ...], color: str, width: int) -> None:
        draw.line(
            [(round(x * scale), round(y * scale)) for x, y in points],
            fill=color,
            width=round(width * scale),
            joint="curve",
        )
        radius = width / 2
        for x, y in (points[0], points[-1]):
            draw.ellipse(
                box((x - radius, y - radius, x + radius, y + radius)),
                fill=color,
            )

    draw.rounded_rectangle(
        box((8, 8, 248, 248)),
        radius=round(51 * scale),
        fill=BACKGROUND,
        outline=BORDER,
        width=max(1, round(4 * scale)),
    )

    # Wireframe model with a bright graph connection through its center.
    line(((128, 48), (195, 88), (195, 167), (128, 207),
          (61, 167), (61, 88), (128, 48)), LINE, 18)
    line(((61, 88), (128, 129), (195, 88)), LINE, 17)
    line(((128, 129), (128, 207)), NODE, 19)
    draw.ellipse(box((116, 117, 140, 141)), fill=NODE)

    return image.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(path: Path) -> None:
    images: list[tuple[int, bytes]] = []
    for size in SIZES:
        data = BytesIO()
        render(size).save(data, format="PNG")
        images.append((size, data.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = bytearray()
    payload = bytearray()
    for size, png in images:
        entries.extend(struct.pack("<BBBBHHII", size if size < 256 else 0,
                                   size if size < 256 else 0, 0, 0, 1, 32,
                                   len(png), offset))
        payload.extend(png)
        offset += len(png)
    path.write_bytes(header + entries + payload)


def write_preview(path: Path) -> None:
    preview = Image.new("RGBA", (960, 384), "#0E1219")
    preview.alpha_composite(render(256), (32, 32))
    for size, x in ((64, 320), (32, 610), (24, 760), (16, 880)):
        icon = render(size)
        preview.alpha_composite(icon.resize((size * 4, size * 4), Image.Resampling.NEAREST), (x, 96))
    path.parent.mkdir(parents=True, exist_ok=True)
    preview.save(path)


if __name__ == "__main__":
    render(512).save(ROOT / "AppIcon.png")
    write_ico(ROOT / "AppIcon.ico")
    write_preview(ROOT / "obj" / "app-icon-preview.png")
