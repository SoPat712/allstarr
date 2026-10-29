from __future__ import annotations

import asyncio
import importlib
import socket
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from apple_gateway.account_cli import song_preparation


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("preparation fixtures must not use the network")
    monkeypatch.setattr(socket.socket, "connect", forbidden)
    monkeypatch.setattr(socket, "create_connection", forbidden)


@pytest.fixture
def song(monkeypatch, tmp_path):
    cli = importlib.import_module("gamdl.cli.cli")
    from gamdl.interface.enums import CoverFormat
    interface = cli.AppleMusicBaseInterface.__new__(cli.AppleMusicBaseInterface)
    interface.cover_format = CoverFormat.JPG
    cover = AsyncMock(return_value=b"complete-cover")

    async def get_cover_bytes(self, url):
        return await cover(url)

    monkeypatch.setattr(cli.AppleMusicBaseInterface, "get_cover_bytes", get_cover_bytes)
    base = SimpleNamespace(
        interface=SimpleNamespace(base=interface),
        get_temp_path=lambda *args: str(tmp_path / "encrypted.m4a"),
        download_stream=AsyncMock(),
        apply_tags=AsyncMock(),
    )
    downloader = cli.AppleMusicSongDownloader(base)
    monkeypatch.setattr(cli.AppleMusicSongDownloader, "stage", AsyncMock())
    track = SimpleNamespace(
        drm_free=False, stream_url="https://audio.invalid/private", fairplay_key="private-key",
        use_cenc=False, use_single_content_key=False,
    )
    item = SimpleNamespace(
        media=SimpleNamespace(
            stream_info=SimpleNamespace(audio_track=track), media_metadata={"id": "101"},
            media_id="101", decryption_key=None, cover=SimpleNamespace(url="https://cover.invalid/private"),
            tags=object(),
        ),
        staged_path=str(tmp_path / "staged.m4a"), uuid_="fixture",
    )
    return cli, downloader, item, cover


@pytest.mark.asyncio
@pytest.mark.parametrize("drm_free", [False, True])
async def test_artwork_overlaps_audio_and_preserves_complete_tagging(song, drm_free):
    cli, downloader, item, cover = song
    item.media.stream_info.audio_track.drm_free = drm_free
    audio_started, artwork_started = asyncio.Event(), asyncio.Event()

    async def artwork(url):
        artwork_started.set()
        await audio_started.wait()
        return b"complete-cover"

    async def audio(*args):
        audio_started.set()
        await artwork_started.wait()

    cover.side_effect = artwork
    downloader.base.download_stream.side_effect = audio
    original_download = cli.AppleMusicSongDownloader.download
    original_cover = cli.AppleMusicBaseInterface.get_cover_bytes
    with song_preparation(cli) as phases:
        await asyncio.wait_for(downloader.download(item), timeout=1)
    cover.assert_awaited_once_with(item.media.cover.url)
    downloader.base.apply_tags.assert_awaited_once_with(item.staged_path, item.media.tags, b"complete-cover")
    assert downloader.stage.await_count == (0 if drm_free else 1)
    assert cli.AppleMusicSongDownloader.download is original_download
    assert cli.AppleMusicBaseInterface.get_cover_bytes is original_cover
    assert any(phase["phase"] == "artwork" for phase in phases)
    assert all(set(phase) == {"phase", "started_ms", "elapsed_ms"} for phase in phases)
    assert "private" not in str(phases)


@pytest.mark.asyncio
async def test_early_artwork_failure_is_observed_once_before_tagging(song):
    cli, downloader, item, cover = song
    cover.side_effect = RuntimeError("cover unavailable")
    audio_finished = False

    async def audio(*args):
        nonlocal audio_finished
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        audio_finished = True

    downloader.base.download_stream.side_effect = audio
    with song_preparation(cli), pytest.raises(RuntimeError, match="cover unavailable"):
        await downloader.download(item)
    assert audio_finished
    downloader.stage.assert_awaited_once()
    cover.assert_awaited_once()
    downloader.base.apply_tags.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize("cancel", [False, True])
async def test_audio_failure_or_cancellation_joins_artwork(song, cancel):
    cli, downloader, item, cover = song
    artwork_started, artwork_closed = asyncio.Event(), asyncio.Event()

    async def artwork(url):
        artwork_started.set()
        try:
            await asyncio.Event().wait()
        finally:
            artwork_closed.set()

    async def audio(*args):
        await artwork_started.wait()
        if cancel:
            raise asyncio.CancelledError()
        raise RuntimeError("audio unavailable")

    cover.side_effect = artwork
    downloader.base.download_stream.side_effect = audio
    expected = asyncio.CancelledError if cancel else RuntimeError
    with song_preparation(cli), pytest.raises(expected):
        await asyncio.wait_for(downloader.download(item), timeout=1)
    assert artwork_closed.is_set()
    downloader.base.apply_tags.assert_not_awaited()


@pytest.mark.asyncio
async def test_raw_cover_does_not_start_an_extra_fetch(song):
    cli, downloader, item, cover = song
    from gamdl.interface.enums import CoverFormat
    downloader.base.interface.base.cover_format = CoverFormat.RAW
    with song_preparation(cli):
        await downloader.download(item)
    cover.assert_not_awaited()
    downloader.base.apply_tags.assert_awaited_once_with(item.staged_path, item.media.tags, None)


@pytest.mark.asyncio
async def test_pinned_cached_methods_keep_instance_binding_and_reuse_results(monkeypatch):
    cli = importlib.import_module("gamdl.cli.cli")
    interface = cli.AppleMusicBaseInterface.__new__(cli.AppleMusicBaseInterface)
    interface.itunes_api = SimpleNamespace(get_lookup_result=AsyncMock(return_value={"results": []}))
    fetch = AsyncMock(return_value=SimpleNamespace(
        status_code=200, content=b"complete-cover", raise_for_status=lambda: None,
    ))

    class Client:
        def __init__(self, **kwargs):
            self.get = fetch

        async def __aenter__(self):
            return self

        async def __aexit__(self, *args):
            pass

    monkeypatch.setattr("gamdl.interface.base.httpx.AsyncClient", Client)
    original_cover = vars(cli.AppleMusicBaseInterface)["get_cover_bytes"]
    with song_preparation(cli):
        for _ in range(2):
            assert await interface.get_cover_bytes("https://cover.invalid") == b"complete-cover"
            assert await interface.get_itunes_lookup_metadata("101") == (None, None)
    fetch.assert_awaited_once()
    interface.itunes_api.get_lookup_result.assert_awaited_once_with("101")
    assert vars(cli.AppleMusicBaseInterface)["get_cover_bytes"] is original_cover


@pytest.mark.asyncio
async def test_overlapped_preparation_retains_identical_decodable_tagged_m4a(song, tmp_path):
    import io
    import shutil
    import statistics
    import subprocess
    import time
    from contextlib import nullcontext
    from pathlib import Path

    from gamdl.interface.types import MediaTags
    from mutagen.mp4 import MP4
    from PIL import Image

    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        pytest.skip("ffmpeg is required")
    fixture = tmp_path / "complete.m4a"
    subprocess.run([
        ffmpeg, "-v", "error", "-f", "lavfi", "-i",
        "sine=frequency=440:sample_rate=44100:duration=0.1", "-c:a", "alac", str(fixture),
    ], check=True, capture_output=True)
    audio_bytes = fixture.read_bytes()
    image = io.BytesIO()
    Image.new("RGB", (2, 2), color="blue").save(image, format="JPEG")
    cover_bytes = image.getvalue()
    cli, downloader, item, cover = song
    base = cli.AppleMusicBaseDownloader.__new__(cli.AppleMusicBaseDownloader)
    base.interface = downloader.base.interface
    base.exclude_tags = None
    base.date_tag_template = None
    downloader.base = base
    item.media.stream_info.audio_track.drm_free = True
    item.media.tags = MediaTags(title="Fixture title", artist="Fixture artist", lyrics="Fixture lyrics")

    async def audio(url, path):
        await asyncio.sleep(0.12)
        Path(path).write_bytes(audio_bytes)

    async def artwork(url):
        await asyncio.sleep(0.08)
        return cover_bytes

    base.download_stream = audio
    cover.side_effect = artwork
    completed = []
    medians = []
    for overlap in (False, True):
        durations = []
        for _ in range(3):
            started = time.monotonic()
            with song_preparation(cli) if overlap else nullcontext():
                await downloader.download(item)
            durations.append((time.monotonic() - started) * 1000)
            completed.append(Path(item.staged_path).read_bytes())
        medians.append(statistics.median(durations))

    assert all(data == completed[0] for data in completed)
    assert cover.await_count == 6
    tagged = MP4(item.staged_path)
    assert tagged["\xa9nam"] == ["Fixture title"]
    assert tagged["\xa9lyr"] == ["Fixture lyrics"]
    assert bytes(tagged["covr"][0]) == cover_bytes
    subprocess.run([
        ffmpeg, "-v", "error", "-i", item.staged_path, "-f", "null", "-",
    ], check=True, capture_output=True)
    print(f"controlled preparation median: serial={medians[0]:.1f}ms overlap={medians[1]:.1f}ms")
