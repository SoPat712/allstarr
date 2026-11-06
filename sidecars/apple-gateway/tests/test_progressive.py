from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from pathlib import Path

import httpx
import pytest
from fastapi.responses import FileResponse, StreamingResponse

from apple_gateway.app import create_app
from apple_gateway.progressive import AudioHeader, audio_chunks, clear_init, media_bytes
from apple_gateway.runner import ProcessFailure
from test_gateway import ACCOUNT, FakeWrapper, settings


class ProgressiveRunner:
    def __init__(self, *, fail=False):
        self.calls = 0
        self.release = asyncio.Event()
        self.closed = asyncio.Event()
        self.waiting = asyncio.Event()
        self.fail = fail
        self.requests = []

    @asynccontextmanager
    async def open_audio(self, *args, **kwargs):
        self.calls += 1
        self.requests.append((args, kwargs))

        async def chunks():
            try:
                yield b"fLaCfirst-audio"
                self.waiting.set()
                await self.release.wait()
                if self.fail:
                    raise ProcessFailure("segment_download_failed")
                yield b"last-audio"
            finally:
                self.closed.set()

        yield AudioHeader("audio/flac", ".flac", 2.5), chunks()

    async def to_flac(self, source, target):
        target.write_bytes(source.read_bytes())
        return target

    async def validate_audio(self, source):
        pass


def endpoint(app, kind="audio"):
    return next(route.endpoint for route in app.routes if getattr(route, "path", "") == f"/api/{kind}/{{song_id}}")


@pytest.mark.asyncio
async def test_first_audio_precedes_download_completion_and_cache_publication(settings):
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert isinstance(response, StreamingResponse)
    assert "content-length" not in response.headers
    assert "accept-ranges" not in response.headers
    assert response.headers["x-allstarr-duration-seconds"] == "2.5"
    assert await asyncio.wait_for(anext(response.body_iterator), 1) == b"fLaCfirst-audio"
    assert not list((settings.data_root / "prepared").glob("*.flac"))
    assert not runner.closed.is_set()
    runner.release.set()
    tail = b"".join([chunk async for chunk in response.body_iterator])
    assert tail == b"last-audio"
    cached = list((settings.data_root / "prepared").glob("*.flac"))
    assert len(cached) == 1
    assert cached[0].read_bytes() == b"fLaCfirst-audiolast-audio"
    second = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert isinstance(second, FileResponse)
    assert second.headers["accept-ranges"] == "bytes"
    assert runner.calls == 1
    assert runner.requests[0][1]["ceiling"].bit_depth == 16
    assert runner.requests[0][1]["ceiling"].sample_rate == 44100


@pytest.mark.asyncio
async def test_midstream_failure_aborts_every_listener_without_publishing(settings, caplog):
    runner = ProgressiveRunner(fail=True)
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert await anext(response.body_iterator) == b"fLaCfirst-audio"
    second = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert await anext(second.body_iterator) == b"fLaCfirst-audio"
    runner.release.set()
    with pytest.raises(ProcessFailure, match="segment_download_failed"):
        await anext(response.body_iterator)
    with pytest.raises(ProcessFailure, match="segment_download_failed"):
        await anext(second.body_iterator)
    assert not list((settings.data_root / "prepared").glob("*"))
    assert "segment_download_failed" in caplog.text
    assert runner.calls == 1


@pytest.mark.asyncio
async def test_late_coalesced_listener_replays_from_start_and_shares_pipeline(settings):
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    first = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert await anext(first.body_iterator) == b"fLaCfirst-audio"
    second = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert await anext(second.body_iterator) == b"fLaCfirst-audio"
    await first.body_iterator.aclose()
    assert not runner.closed.is_set()
    runner.release.set()
    assert b"".join([chunk async for chunk in second.body_iterator]) == b"last-audio"
    assert runner.calls == 1
    assert len(list((settings.data_root / "prepared").glob("*.flac"))) == 1


@pytest.mark.asyncio
async def test_last_disconnect_cancels_pipeline_and_removes_partial(settings):
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    await anext(response.body_iterator)
    await response.body_iterator.aclose()
    assert runner.closed.is_set()
    assert not list((settings.data_root / "prepared").glob("*"))


@pytest.mark.asyncio
async def test_download_reuses_completed_output_without_per_song_setup(settings):
    runner = ProgressiveRunner()
    runner.release.set()
    app = create_app(settings, FakeWrapper(), runner)
    first = await endpoint(app, "download")("101", "alac-16-44", ACCOUNT)
    second = await endpoint(app, "download")("101", "alac-16-44", ACCOUNT)
    assert Path(first.path).read_bytes() == b"fLaCfirst-audiolast-audio"
    assert Path(second.path).read_bytes() == Path(first.path).read_bytes()
    assert runner.calls == 1


@pytest.mark.asyncio
async def test_transport_failure_never_sends_successful_end_of_body(settings):
    runner = ProgressiveRunner(fail=True)
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    messages = []

    async def send(message):
        messages.append(message)
        if message["type"] == "http.response.body":
            runner.release.set()

    async def receive():
        await asyncio.Event().wait()

    with pytest.raises(ProcessFailure, match="segment_download_failed"):
        await response({"type": "http", "method": "GET", "asgi": {"spec_version": "2.4"}}, receive, send)
    bodies = [message for message in messages if message["type"] == "http.response.body"]
    assert bodies and all(message["more_body"] for message in bodies)
    assert not list((settings.data_root / "prepared").glob("*"))


@pytest.mark.asyncio
async def test_send_disconnect_cancels_producer_and_removes_partial(settings):
    from starlette.requests import ClientDisconnect

    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)

    async def send(message):
        if message["type"] == "http.response.body":
            raise OSError("fixture disconnected")

    async def receive():
        await asyncio.Event().wait()

    with pytest.raises(ClientDisconnect):
        await response({"type": "http", "method": "GET", "asgi": {"spec_version": "2.4"}}, receive, send)
    assert runner.closed.is_set()
    assert not list((settings.data_root / "prepared").glob("*"))


@pytest.mark.asyncio
async def test_completed_stream_route_uses_cached_file_and_range_support(settings):
    runner = ProgressiveRunner()
    runner.release.set()
    app = create_app(settings, FakeWrapper(), runner)
    first = await endpoint(app, "stream")("101", "alac-16-44", ACCOUNT)
    data = b"".join([chunk async for chunk in first.body_iterator])
    second = await endpoint(app, "stream")("101", "alac-16-44", ACCOUNT)
    assert isinstance(second, FileResponse)
    assert second.headers["accept-ranges"] == "bytes"
    assert data.endswith(Path(second.path).read_bytes())
    assert runner.calls == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("kind", ["head", "download"])
async def test_materialized_request_returns_structured_error_on_late_failure(settings, kind):
    from fastapi import HTTPException
    from starlette.requests import Request

    runner = ProgressiveRunner(fail=True)
    app = create_app(settings, FakeWrapper(), runner)
    if kind == "head":
        pending = asyncio.create_task(endpoint(app)("101", "alac-16-44", ACCOUNT,
            request=Request({"type": "http", "method": "HEAD"})))
    else:
        pending = asyncio.create_task(endpoint(app, "download")("101", "alac-16-44", ACCOUNT))
    await asyncio.wait_for(runner.waiting.wait(), 1)
    runner.release.set()
    with pytest.raises(HTTPException) as failure:
        await pending
    assert failure.value.status_code == 502
    assert failure.value.detail == "segment_download_failed"
    assert not list((settings.data_root / "prepared").glob("*"))


@pytest.mark.asyncio
async def test_cache_write_failure_after_audio_aborts_and_never_publishes(settings, monkeypatch, caplog):
    runner = ProgressiveRunner()
    app = create_app(settings, FakeWrapper(), runner)
    response = await endpoint(app)("101", "alac-16-44", ACCOUNT)
    assert await anext(response.body_iterator)
    original = Path.replace

    def replace(source, target):
        if source.parent.name == "prepared":
            raise OSError("fixture cache full")
        return original(source, target)

    monkeypatch.setattr(Path, "replace", replace)
    runner.release.set()
    with pytest.raises(ProcessFailure, match="cache_write_failed"):
        _ = [chunk async for chunk in response.body_iterator]
    assert not list((settings.data_root / "prepared").glob("*"))
    assert "cache_write_failed" in caplog.text


@pytest.mark.asyncio
async def test_encoder_pipe_failure_after_first_chunk_reports_transcode_failure(settings, tmp_path, monkeypatch):
    from types import SimpleNamespace
    from unittest.mock import AsyncMock
    from gamdl.interface.types import MediaTags

    def box(kind, data):
        return (len(data) + 8).to_bytes(4, "big") + kind + data

    entry = bytearray(28)
    entry[18:20] = (16).to_bytes(2, "big")
    entry[24:28] = (44100 << 16).to_bytes(4, "big")
    init = box(b"moov", box(b"trak", box(b"mdia", box(b"minf", box(b"stbl",
        box(b"stsd", bytes(4) + (1).to_bytes(4, "big") + box(b"alac", entry)))))))
    moof = box(b"moof", box(b"traf", box(b"tfhd", (16).to_bytes(4, "big")
        + (1).to_bytes(4, "big") + (6).to_bytes(4, "big")) + box(b"trun", bytes(4) + (1).to_bytes(4, "big"))))
    release = asyncio.Event()

    async def source():
        yield init
        await release.wait()
        yield moof + box(b"mdat", b"sample")

    writes = []

    def write(data):
        if writes:
            raise BrokenPipeError("fixture encoder stopped")
        writes.append(data)

    async def output(_):
        if not getattr(output, "started", False):
            output.started = True
            return b"fLaC\x80\x00\x00\x22" + bytes(34) + b"audio"
        await asyncio.Event().wait()

    process = SimpleNamespace(returncode=None,
        stdin=SimpleNamespace(write=write, drain=AsyncMock(), close=lambda: None),
        stdout=SimpleNamespace(read=output),
        stderr=SimpleNamespace(read=AsyncMock(side_effect=[b"decoder stopped at later frame", b""])),
        wait=AsyncMock(return_value=1), kill=lambda: None)
    monkeypatch.setattr(asyncio, "create_subprocess_exec", AsyncMock(return_value=process))
    media = SimpleNamespace(tags=MediaTags(), cover_bytes=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, settings, tmp_path, source=source())
    await anext(chunks)
    assert await anext(chunks)
    release.set()
    with pytest.raises(ProcessFailure, match="transcode_failed") as failure:
        await anext(chunks)
    assert failure.value.stderr == "decoder stopped at later frame"


def test_encrypted_initialization_keeps_alac_configuration_and_removes_protection():
    def box(kind, data):
        return (len(data) + 8).to_bytes(4, "big") + kind + data

    config = bytearray(28)
    config[9] = 24
    config[24:28] = (96000).to_bytes(4, "big")
    entry = bytearray(28)
    entry[18:20] = (16).to_bytes(2, "big")
    entry[24:28] = (44100 << 16).to_bytes(4, "big")
    encrypted = box(b"enca", entry + box(b"alac", config) + box(b"sinf", box(b"frma", b"alac")))
    init = box(b"moov", box(b"trak", box(b"mdia", box(b"minf", box(b"stbl",
        box(b"stsd", bytes(4) + (1).to_bytes(4, "big") + encrypted))))))
    cleared, facts = clear_init(init)
    assert facts == ("alac", 24, 96000)
    assert len(cleared) == len(init)
    assert b"enca" not in cleared and b"sinf" not in cleared
    assert b"free" in cleared


@pytest.mark.asyncio
@pytest.mark.parametrize("force_flac", [False, True])
async def test_aac_fallback_keeps_source_rate_when_lossless_ceiling_is_requested(settings, tmp_path, force_flac):
    import json
    import shutil
    import subprocess
    from dataclasses import replace
    from types import SimpleNamespace
    from gamdl.interface.types import MediaTags

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    source = tmp_path / "aac.mp4"
    subprocess.run([ffmpeg, "-v", "error", "-filter_threads", "1", "-f", "lavfi", "-i",
        "anullsrc=r=48000:cl=stereo", "-t", "0.2", "-threads", "1", "-c:a", "aac",
        "-movflags", "+empty_moov+default_base_moof", "-frag_duration", "100000", str(source)],
        check=True, capture_output=True)

    async def fragments():
        yield source.read_bytes()

    media = SimpleNamespace(tags=MediaTags(), cover_bytes=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, replace(settings, ffmpeg_path=ffmpeg), tmp_path,
        source=fragments(), ceiling=(16, 44100), force_flac=force_flac)
    header = await anext(chunks)
    output = tmp_path / ("result" + header.suffix)
    output.write_bytes(b"".join([chunk async for chunk in chunks]))
    facts = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        "-show_entries", "stream=codec_name,sample_rate", "-of", "json", str(output)],
        check=True, capture_output=True).stdout)["streams"][0]
    assert facts["codec_name"] == ("flac" if force_flac else "aac")
    assert int(facts["sample_rate"]) == 48000


@pytest.mark.asyncio
@pytest.mark.parametrize("codec", ["alac", "aac"])
async def test_copied_audio_with_corrupt_final_samples_fails_full_decode_validation(settings, tmp_path, codec):
    import shutil
    import subprocess
    from dataclasses import replace
    from types import SimpleNamespace
    from gamdl.interface.types import MediaTags
    from apple_gateway.progressive import boxes

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    source = tmp_path / "source.mp4"
    subprocess.run([ffmpeg, "-v", "error", "-filter_threads", "1", "-f", "lavfi", "-i",
        "anoisesrc=sample_rate=44100:duration=0.6:seed=1", "-ac", "2", "-c:a", codec, "-threads", "1",
        "-movflags", "+empty_moov+default_base_moof", "-frag_duration", "200000", str(source)],
        check=True, capture_output=True)
    raw = source.read_bytes()
    parts = [raw[start:end] for _, start, end, _ in boxes(raw)]
    last = max(index for index, part in enumerate(parts) if part[4:8] == b"mdat")
    invalid = b"\x80" if codec == "alac" else b"\xff"
    parts[last] = parts[last][:8] + invalid * (len(parts[last]) - 8)
    release = asyncio.Event()

    async def fragments():
        for index, part in enumerate(parts):
            if index == last - 1:
                await release.wait()
            yield part

    media = SimpleNamespace(tags=MediaTags(), cover_bytes=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, replace(settings, ffmpeg_path=ffmpeg), tmp_path,
        source=fragments(), original_container=True)
    assert (await anext(chunks)).suffix == ".m4a"
    first = await asyncio.wait_for(anext(chunks), 3)
    assert first
    release.set()
    artifact = tmp_path / "copied.m4a"
    artifact.write_bytes(first + b"".join([chunk async for chunk in chunks]))
    from apple_gateway.runner import BoundedProcessRunner
    runner = BoundedProcessRunner(replace(settings, ffmpeg_path=ffmpeg))
    with pytest.raises(ProcessFailure, match="transcode_failed"):
        await runner.validate_audio(artifact)


@pytest.mark.asyncio
async def test_segment_ranges_are_ordered_and_truncated_ranges_fail():
    requests = []

    async def send(request):
        requests.append((request.url.path, request.headers.get("range")))
        if request.url.path == "/media.m3u8":
            return httpx.Response(200, text=(
                '#EXTM3U\n#EXT-X-MAP:URI="track.mp4",BYTERANGE="4@0"\n'
                '#EXTINF:1,\n#EXT-X-BYTERANGE:4@4\ntrack.mp4\n'
                '#EXTINF:1,\n#EXT-X-BYTERANGE:4\ntrack.mp4\n#EXT-X-ENDLIST\n'
            ))
        start, end = map(int, request.headers["range"][6:].split("-"))
        return httpx.Response(206, content=b"0123456789ab"[start:end + 1],
            headers={"Content-Range": f"bytes {start}-{end}/12"})

    async with httpx.AsyncClient(transport=httpx.MockTransport(send)) as client:
        assert b"".join([chunk async for chunk in media_bytes(client, "https://fixture.invalid/media.m3u8")]) == b"0123456789ab"
    assert requests == [("/media.m3u8", None), ("/track.mp4", "bytes=0-3"),
        ("/track.mp4", "bytes=4-7"), ("/track.mp4", "bytes=8-11")]

    async def truncated(request):
        if request.url.path == "/media.m3u8":
            return await send(request)
        return httpx.Response(206, content=b"short", headers={"Content-Range": "bytes 0-3/12"})

    async with httpx.AsyncClient(transport=httpx.MockTransport(truncated)) as client:
        with pytest.raises(ProcessFailure, match="segment_download_failed"):
            _ = [chunk async for chunk in media_bytes(client, "https://fixture.invalid/media.m3u8")]


@pytest.mark.asyncio
@pytest.mark.parametrize("decryption", ["clear", "wrapper", "local-key"])
@pytest.mark.parametrize("original,depth,rate,expected", [
    (False, 16, 44100, "flac"), (False, 24, 96000, "flac"),
    (True, 16, 44100, "alac"), (True, 24, 96000, "flac"),
])
async def test_real_fragment_pipeline_emits_tagged_audio_before_last_segment_and_applies_ceiling(
    settings, tmp_path, monkeypatch, original, depth, rate, expected, decryption
):
    import io
    import json
    import shutil
    import subprocess
    from dataclasses import replace
    from types import SimpleNamespace

    from gamdl.interface.types import MediaTags
    from mutagen.flac import FLAC
    from mutagen.mp4 import MP4
    from PIL import Image

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    source = tmp_path / "input.mp4"
    subprocess.run([ffmpeg, "-v", "error", "-filter_threads", "1", "-f", "lavfi", "-i",
        f"anoisesrc=sample_rate={rate}:duration=0.6:seed=1", "-ac", "2", "-c:a", "alac",
        "-threads", "1", "-sample_fmt", "s16p" if depth == 16 else "s32p", "-movflags", "+empty_moov+default_base_moof",
        "-frag_duration", "200000", str(source)], check=True, capture_output=True)
    raw = source.read_bytes()
    key = bytes(range(16))
    if decryption == "local-key":
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
        packets = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
            "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", str(source)],
            check=True, capture_output=True).stdout)["packets"]
        encrypted = bytearray(raw)
        for packet in packets:
            start, size = int(packet["pos"]), int(packet["size"]) // 16 * 16
            encryptor = Cipher(algorithms.AES(key), modes.CBC(bytes(16))).encryptor()
            encrypted[start:start + size] = encryptor.update(raw[start:start + size]) + encryptor.finalize()
        raw = bytes(encrypted)
    parts = []
    while raw:
        length = int.from_bytes(raw[:4], "big")
        assert length >= 8
        parts.append(raw[:length])
        raw = raw[length:]
    image = io.BytesIO()
    Image.new("RGB", (2, 2), color="blue").save(image, format="JPEG")
    cover = image.getvalue()
    release = asyncio.Event()
    stopped = asyncio.Event()

    async def fragments():
        for index, part in enumerate(parts):
            if index == len(parts) - 3:
                stopped.set()
                await release.wait()
            yield part

    media = SimpleNamespace(
        media_id="101", tags=MediaTags(title="Fixture title", artist="Fixture artist", lyrics="Fixture lyrics"),
        cover_bytes=cover, decryption_key=SimpleNamespace(audio_track=SimpleNamespace(key=key.hex()))
            if decryption == "local-key" else None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=decryption == "clear",
            use_cenc=False, use_single_content_key=True, fairplay_key="skd://fixture")),
    )
    wrapper_calls = []

    async def wrapper(api, song_id, encrypted, decrypted, fairplay_key, **options):
        assert song_id == "101" and fairplay_key == "skd://fixture"
        assert options == {"use_single_content_key": True}
        fragment = Path(encrypted).read_bytes()
        assert len(fragment) < source.stat().st_size
        wrapper_calls.append(fragment)
        Path(decrypted).write_bytes(fragment)

    monkeypatch.setattr("gamdl.downloader.ammuxer.decrypt_and_mux_wrapper", wrapper)
    configured = replace(settings, ffmpeg_path=ffmpeg)
    chunks = audio_chunks(media, SimpleNamespace(wrapper_api=object()), configured, tmp_path, ceiling=(16, 44100),
        original_container=original, source=fragments())
    header = await anext(chunks)
    assert header == AudioHeader("audio/mp4" if expected == "alac" else "audio/flac",
        ".m4a" if expected == "alac" else ".flac")
    first = await asyncio.wait_for(anext(chunks), 3)
    assert first and not release.is_set()
    release.set()
    data = first + b"".join([chunk async for chunk in chunks])
    assert bool(wrapper_calls) == (decryption == "wrapper")
    artifact = tmp_path / ("result" + header.suffix)
    artifact.write_bytes(data)
    facts = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,bits_per_raw_sample",
        "-of", "json", str(artifact)], check=True, capture_output=True).stdout)["streams"][0]
    assert facts["codec_name"] == expected
    assert int(facts["sample_rate"]) == 44100
    assert int(facts["bits_per_raw_sample"]) == 16
    subprocess.run([ffmpeg, "-v", "error", "-xerror", "-threads", "1", "-i", str(artifact),
        "-threads", "1", "-f", "null", "-"],
        check=True, capture_output=True)
    if expected == "flac":
        tags = FLAC(artifact)
        assert tags["title"] == ["Fixture title"]
        assert tags["lyrics"] == ["Fixture lyrics"]
        assert tags.pictures[0].data == cover
    else:
        tags = MP4(artifact)
        assert tags["\xa9nam"] == ["Fixture title"]
        assert tags["\xa9lyr"] == ["Fixture lyrics"]
        assert bytes(tags["covr"][0]) == cover
