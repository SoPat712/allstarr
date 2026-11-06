from __future__ import annotations

import asyncio
import hashlib
import json
import shutil
import subprocess
from contextlib import asynccontextmanager
from dataclasses import replace
from pathlib import Path

import pytest
from fastapi.responses import FileResponse
from mutagen.flac import FLAC
from mutagen.mp4 import MP4

from apple_gateway.app import create_app
from apple_gateway.progressive import AudioHeader, boxes
from progressive_fixtures import FIXTURES
from test_gateway import ACCOUNT, FakeWrapper, settings
from test_progressive import endpoint
from test_progressive import ProgressiveRunner


SAMPLES = json.loads((FIXTURES / "manifest.json").read_text())


def probe(ffmpeg, path, *arguments):
    return json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        *arguments, "-of", "json", str(path)], check=True, capture_output=True).stdout)


class FixtureRunner:
    def __init__(self, sample):
        self.sample = sample

    @asynccontextmanager
    async def open_audio(self, *args, **kwargs):
        data = (FIXTURES / self.sample["file"]).read_bytes()

        async def chunks():
            for offset in range(0, len(data), 8192):
                yield data[offset:offset + 8192]
                await asyncio.sleep(0)

        yield AudioHeader(self.sample["mime"], Path(self.sample["file"]).suffix), chunks()


@pytest.mark.asyncio
@pytest.mark.parametrize("sample", SAMPLES, ids=lambda sample: sample["file"])
async def test_cached_progressive_audio_has_complete_metadata_and_identical_packets(settings, sample):
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    app = create_app(replace(settings, ffmpeg_path=ffmpeg), FakeWrapper(), FixtureRunner(sample))
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    streamed = b"".join([chunk async for chunk in response.body_iterator])
    source = FIXTURES / sample["file"]
    assert streamed == source.read_bytes()
    cached = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert isinstance(cached, FileResponse)
    path = Path(cached.path)
    facts = probe(ffmpeg, path, "-show_entries", "format=duration")
    original = probe(ffmpeg, source, "-select_streams", "a:0", "-show_packets",
        "-show_data_hash", "sha256", "-show_entries", "packet=data_hash,size,duration")
    packets = probe(ffmpeg, path, "-select_streams", "a:0", "-show_packets",
        "-show_data_hash", "sha256", "-show_entries", "packet=data_hash,size,duration")
    assert [{key: packet[key] for key in ("data_hash", "size", "duration")} for packet in packets["packets"]] == [
        {key: packet[key] for key in ("data_hash", "size", "duration")} for packet in original["packets"]]
    assert float(facts["format"]["duration"]) == pytest.approx(
        sum(int(packet["duration"]) for packet in packets["packets"]) / sample["sample_rate"], abs=0.001)
    if sample["codec"] == "flac":
        tags = FLAC(path)
        pcm = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-xerror", "-threads", "1",
            "-i", str(source), "-map", "0:a:0", "-threads", "1", "-f",
            "s16le" if sample["bit_depth"] == 16 else "s24le", "-"], check=True, capture_output=True).stdout
        assert tags.info.total_samples == len(pcm) // (sample["channels"] * sample["bit_depth"] // 8)
        assert tags.info.md5_signature == int.from_bytes(hashlib.md5(pcm).digest(), "big")
        table = next(block for block in tags.metadata_blocks if block.code == 3)
        assert table.seekpoints and table.seekpoints[0].first_sample == 0
        raw = path.read_bytes()
        offset = 4
        while True:
            last = raw[offset] & 128
            offset += 4 + int.from_bytes(raw[offset + 1:offset + 4], "big")
            if last:
                break
        frames = probe(ffmpeg, path, "-select_streams", "a:0", "-show_packets",
            "-show_entries", "packet=pts,pos,duration")["packets"]
        for point in table.seekpoints:
            assert any(int(frame["pts"]) == point.first_sample and int(frame["pos"]) == offset + point.byte_offset
                and int(frame["duration"]) == point.num_samples for frame in frames)
        assert tags["title"] == FLAC(source)["title"]
        assert [picture.data for picture in tags.pictures] == [picture.data for picture in FLAC(source).pictures]
    else:
        raw = path.read_bytes()
        assert b"moof" not in [kind for kind, *_ in boxes(raw)]
        assert MP4(path)["\xa9nam"] == MP4(source)["\xa9nam"]
        assert MP4(path).get("covr") == MP4(source).get("covr")
    assert not list(path.parent.glob("*.partial"))


@pytest.mark.asyncio
@pytest.mark.parametrize("fail", [False, True])
async def test_finalization_is_private_until_complete_and_preserves_active_listener_bytes(settings, monkeypatch, caplog, fail):
    entered, release = asyncio.Event(), asyncio.Event()

    async def finalize(source, target, ffmpeg):
        target.write_bytes(b"finalized-cache")
        entered.set()
        await release.wait()
        if fail:
            raise OSError("fixture finalization failed")

    monkeypatch.setattr("apple_gateway.finalization.finalize_audio", finalize)
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    first = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    prefix = await anext(first.body_iterator)
    runner.release.set()
    await entered.wait()
    assert not list((settings.data_root / "prepared").glob("*.flac"))
    second = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    replay = await anext(second.body_iterator)
    release.set()
    assert prefix + b"".join([chunk async for chunk in first.body_iterator]) == b"fLaCfirst-audiolast-audio"
    assert replay + b"".join([chunk async for chunk in second.body_iterator]) == b"fLaCfirst-audiolast-audio"
    cached = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert Path(cached.path).read_bytes() == (b"fLaCfirst-audiolast-audio" if fail else b"finalized-cache")
    assert not list((settings.data_root / "prepared").glob("*.partial"))
    assert ("retaining validated output" in caplog.text) == fail


@pytest.mark.asyncio
async def test_disconnect_during_finalization_cancels_and_cleans_both_private_files(settings, monkeypatch):
    entered = asyncio.Event()

    async def finalize(source, target, ffmpeg):
        target.write_bytes(b"unfinished-cache")
        entered.set()
        await asyncio.Event().wait()

    monkeypatch.setattr("apple_gateway.finalization.finalize_audio", finalize)
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    await anext(response.body_iterator)
    runner.release.set()
    await entered.wait()
    await response.body_iterator.aclose()
    assert not list((settings.data_root / "prepared").glob("*"))
