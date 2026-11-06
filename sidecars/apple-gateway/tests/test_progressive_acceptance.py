from __future__ import annotations

import asyncio
import json
import shutil
import subprocess
from pathlib import Path
from types import SimpleNamespace

import pytest
from gamdl.interface.types import MediaTags

from apple_gateway.progressive import audio_chunks, boxes

from progressive_fixtures import CASES, FIXTURES, encode


@pytest.mark.asyncio
@pytest.mark.parametrize("name,source,ceiling,original,cover,codec,depth,rate", CASES)
async def test_fragment_pipeline_preserves_or_limits_real_output_format(
    tmp_path, name, source, ceiling, original, cover, codec, depth, rate
):
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    header, data = await encode(FIXTURES / ("input-" + source + ".m4a"), tmp_path, ffmpeg,
        ceiling=ceiling, original=original, cover=cover)
    expected_mime = "audio/flac" if codec == "flac" else "audio/mp4"
    assert header.media_type == expected_mime
    output = tmp_path / (name + header.suffix)
    output.write_bytes(data)
    facts = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,bits_per_raw_sample,channels",
        "-of", "json", str(output)], check=True, capture_output=True).stdout)["streams"][0]
    assert facts["codec_name"] == codec
    assert int(facts["sample_rate"]) == rate
    assert int(facts["channels"]) == 2
    assert (int(facts["bits_per_raw_sample"]) if "bits_per_raw_sample" in facts else None) == depth
    if codec == "flac":
        assert int.from_bytes(data[18:26], "big") & ((1 << 36) - 1) == 0
    subprocess.run([ffmpeg, "-v", "error", "-xerror", "-threads", "1", "-i", str(output),
        "-threads", "1", "-f", "null", "-"], check=True, capture_output=True)


@pytest.mark.asyncio
@pytest.mark.parametrize("source", ["alac-16-44100", "aac-48000"])
async def test_copied_fragment_pipeline_emits_decodable_audio_before_the_last_input_segment(tmp_path, source):
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    raw = (FIXTURES / ("input-" + source + ".m4a")).read_bytes()
    parts = [raw[start:stop] for _, start, stop, _ in boxes(raw)]
    last = max(index for index, part in enumerate(parts) if part[4:8] == b"moof")
    release = asyncio.Event()

    async def fragments():
        for index, part in enumerate(parts):
            if index == last:
                await release.wait()
            yield part

    media = SimpleNamespace(tags=MediaTags(), cover_bytes=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, SimpleNamespace(ffmpeg_path=ffmpeg), tmp_path,
        source=fragments(), original_container=True)
    header = await anext(chunks)

    async def prefix():
        data = bytearray()
        async for chunk in chunks:
            data.extend(chunk)
            offset = 0
            while offset <= len(data) - 8:
                size = int.from_bytes(data[offset:offset + 4], "big")
                if size < 8 or offset + size > len(data):
                    break
                if data[offset + 4:offset + 8] == b"mdat":
                    return bytes(data)
                offset += size
        raise AssertionError("No audio fragment was produced")

    try:
        data = await asyncio.wait_for(prefix(), 3)
        output = tmp_path / ("prefix" + header.suffix)
        output.write_bytes(data)
        decoded = subprocess.run([ffmpeg, "-v", "error", "-threads", "1", "-i", str(output),
            "-t", "0.05", "-threads", "1", "-f", "s16le", "-"], check=True, capture_output=True)
        assert decoded.stdout
        assert not release.is_set()
    finally:
        release.set()
        await chunks.aclose()
