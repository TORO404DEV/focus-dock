from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "src" / "FocusDock.App" / "Assets"
assets.mkdir(parents=True, exist_ok=True)

def icon(size: int) -> Image.Image:
    scale = 4
    n = size * scale
    image = Image.new("RGBA", (n, n), "#171916")
    draw = ImageDraw.Draw(image)

    def rect(x0, y0, x1, y1, fill, outline=None, width=1):
        draw.rectangle((x0 * scale, y0 * scale, x1 * scale, y1 * scale), fill=fill, outline=outline, width=width * scale)

    rect(18, 18, 238, 238, "#F1F0E9", "#171916", 10)
    rect(38, 38, 218, 218, "#D7D9D1", "#171916", 10)
    draw.ellipse((71 * scale, 71 * scale, 185 * scale, 185 * scale), fill="#171916")
    draw.line([(96 * scale, 128 * scale), (118 * scale, 150 * scale), (162 * scale, 100 * scale)], fill="#F1F0E9", width=15 * scale, joint="curve")
    rect(38, 38, 96, 56, "#B84B4B")
    rect(160, 200, 218, 218, "#B84B4B")
    return image.resize((size, size), Image.Resampling.LANCZOS)

frames = [icon(size) for size in (16, 24, 32, 48, 64, 128, 256)]
frames[-1].save(assets / "FocusDock.ico", format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[:-1])
print(assets / "FocusDock.ico")
