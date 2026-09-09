from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "src" / "PomoDock.App" / "Assets"
assets.mkdir(parents=True, exist_ok=True)

def icon(size: int) -> Image.Image:
    scale = 4
    n = 256 * scale
    image = Image.new("RGBA", (n, n), "#F15B4E")
    draw = ImageDraw.Draw(image)

    def rect(x0, y0, x1, y1, fill, outline=None, width=1):
        draw.rectangle((x0 * scale, y0 * scale, x1 * scale, y1 * scale), fill=fill, outline=outline, width=width * scale)

    rect(14, 14, 242, 242, "#F1F0E9", "#171916", 12)
    draw.ellipse((54 * scale, 54 * scale, 202 * scale, 202 * scale), fill="#171916")
    draw.line([(91 * scale, 128 * scale), (116 * scale, 153 * scale), (168 * scale, 94 * scale)], fill="#F15B4E", width=19 * scale, joint="curve")
    rect(26, 26, 84, 41, "#171916")
    rect(172, 215, 230, 230, "#171916")
    return image.resize((size, size), Image.Resampling.LANCZOS)

frames = [icon(size) for size in (16, 24, 32, 48, 64, 128, 256)]
frames[-1].save(assets / "PomoDock.ico", format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[:-1])
print(assets / "PomoDock.ico")
