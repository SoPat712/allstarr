#!/usr/bin/env python3
"""Render bundled provider marks as square raster covers with librsvg."""

from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


root = Path(__file__).resolve().parents[1] / "allstarr/wwwroot/images/providers"
backgrounds = {
    "qobuz": "white", "applemusic": "white", "tidal": "#08090b",
    "genius": "#ffff64", "paxsenix": "#5b4bc4",
}
for source in sorted(root.glob("*.svg")):
    svg = ET.parse(source).getroot()
    _, _, width, height = map(float, svg.attrib["viewBox"].split())
    for size in (600, 96):
        scale = min(size / width, size / height)
        rendered_width, rendered_height = width * scale, height * scale
        suffix = "-96" if size == 96 else ""
        subprocess.run([
            "rsvg-convert", "--width", str(rendered_width), "--height", str(rendered_height),
            "--page-width", str(size), "--page-height", str(size),
            "--left", str((size - rendered_width) / 2), "--top", str((size - rendered_height) / 2),
            "--background-color", backgrounds.get(source.stem, "black"),
            "--output", str(root / f"{source.stem}-cover{suffix}.png"), str(source),
        ], check=True)
