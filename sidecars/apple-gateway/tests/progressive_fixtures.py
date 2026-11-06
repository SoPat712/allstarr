from __future__ import annotations

import asyncio
import io
import json
import random
import shutil
import subprocess
import tempfile
from pathlib import Path
from types import SimpleNamespace

from gamdl.interface.types import MediaTags
from PIL import Image

from apple_gateway.progressive import audio_chunks


FIXTURES = Path(__file__).resolve().parents[3] / "allstarr.Tests" / "Fixtures" / "AppleProgressive"
CASES = [
    ("alac-16-44100", "alac-16-44100", None, False, False, "flac", 16, 44100),
    ("alac-16-44100-ceiling", "alac-16-44100", (16, 44100), False, False, "flac", 16, 44100),
    ("alac-24-48000", "alac-24-48000", None, False, False, "flac", 24, 48000),
    ("alac-24-48000-ceiling", "alac-24-48000", (16, 44100), False, False, "flac", 16, 44100),
    ("alac-16-44100-original", "alac-16-44100", None, True, False, "alac", 16, 44100),
    ("alac-24-48000-original", "alac-24-48000", None, True, False, "alac", 24, 48000),
    ("aac-44100", "aac-44100", None, False, False, "aac", None, 44100),
    ("aac-44100-ceiling", "aac-44100", (16, 44100), False, False, "aac", None, 44100),
    ("aac-48000", "aac-48000", None, False, False, "aac", None, 48000),
    ("aac-48000-ceiling", "aac-48000", (16, 44100), False, False, "aac", None, 48000),
    ("alac-artwork", "alac-24-48000", (16, 44100), False, True, "flac", 16, 44100),
    ("aac-artwork", "aac-48000", (16, 44100), False, True, "aac", None, 48000),
]


def artwork():
    image = Image.frombytes("RGB", (640, 640), random.Random(1).randbytes(640 * 640 * 3))
    output = io.BytesIO()
    image.save(output, format="JPEG", quality=95)
    assert len(output.getvalue()) > 256 * 1024
    return output.getvalue()


async def encode(source, directory, ffmpeg, *, ceiling=None, original=False, cover=False):
    async def fragments():
        raw = source.read_bytes()
        for offset in range(0, len(raw), 8192):
            yield raw[offset:offset + 8192]

    media = SimpleNamespace(tags=MediaTags(title="Fixture title", artist="Fixture artist"),
        cover_bytes=artwork() if cover else None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, SimpleNamespace(ffmpeg_path=ffmpeg), directory,
        source=fragments(), ceiling=ceiling, original_container=original)
    header = await anext(chunks)
    return header, b"".join([chunk async for chunk in chunks])


async def generate():
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        raise RuntimeError("ffmpeg is required")
    FIXTURES.mkdir(parents=True, exist_ok=True)
    for name, codec, depth, rate in [("alac-16-44100", "alac", 16, 44100),
            ("alac-24-48000", "alac", 24, 48000), ("aac-44100", "aac", 16, 44100),
            ("aac-48000", "aac", 16, 48000)]:
        precision = ["-sample_fmt", "s16p" if depth == 16 else "s32p"] if codec == "alac" else []
        subprocess.run([ffmpeg, "-y", "-v", "error", "-filter_threads", "1", "-f", "lavfi", "-i",
            f"sine=frequency=997:sample_rate={rate}:duration=3", "-ac", "2", "-c:a", codec,
            "-threads", "1", *precision, "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", "500000", str(FIXTURES / ("input-" + name + ".m4a"))],
            check=True, capture_output=True)
    entries = []
    with tempfile.TemporaryDirectory() as temporary:
        for name, source, ceiling, original, cover, codec, depth, rate in CASES:
            header, data = await encode(FIXTURES / ("input-" + source + ".m4a"), Path(temporary), ffmpeg,
                ceiling=ceiling, original=original, cover=cover)
            filename = name + header.suffix
            (FIXTURES / filename).write_bytes(data)
            entries.append(dict(file=filename, mime=header.media_type, codec=codec,
                bit_depth=depth, sample_rate=rate, channels=2, duration_seconds=3))
    (FIXTURES / "manifest.json").write_text(json.dumps(entries, indent=2) + "\n")


if __name__ == "__main__":
    asyncio.run(generate())
