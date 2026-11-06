from __future__ import annotations

import asyncio
import contextlib
import copy
import json
import logging
import struct
import sys
import tempfile
import time
from dataclasses import asdict
from pathlib import Path

from gamdl.interface import AppleMusicSongInterface
from gamdl.interface.enums import SongCodec
from gamdl.interface.exceptions import GamdlInterfaceFormatNotAvailableError
from gamdl.interface.types import AppleMusicMedia

from .account_cli import AccountFailure, STREAM_NAME, lossless_playlist
from .config import Settings
from .progressive import audio_chunks
from .runner import ProcessFailure
from .sessions import AccountSessions, create_session


class _MediaHttp:
    def __init__(self, http):
        self.http = http
        self.manifests = {}

    async def get(self, url):
        if url not in self.manifests:
            response = await self.http.get(url)
            response.raise_for_status()
            if response.is_redirect:
                raise ProcessFailure("manifest_invalid")
            if ".m3u8" in url.split("?", 1)[0]:
                if len(response.content) > 4 * 1024 * 1024:
                    raise ProcessFailure("manifest_invalid")
                self.manifests[url] = response
            return response
        return self.manifests[url]

    def stream(self, *args, **kwargs):
        return self.http.stream(*args, **kwargs)


class _Song(AppleMusicSongInterface):
    def __init__(self, base, quality, ceiling, stream):
        super().__init__(base, codec_priority=[SongCodec(quality)])
        self.ceiling, self.stream = ceiling, stream

    def _get_playlist_from_codec_enhanced(self, data, codec):
        chosen = super()._get_playlist_from_codec_enhanced(data, codec)
        playlists = data.get("playlists", [])
        if codec == SongCodec.ALAC:
            chosen = lossless_playlist(playlists, chosen, self.ceiling)
        names = {playlist.get("stream_info", {}).get("audio") for playlist in playlists}
        self.stream["offered"] = sorted(name for name in names if isinstance(name, str) and STREAM_NAME.fullmatch(name))[:16]
        self.stream["chosen"] = chosen.get("stream_info", {}).get("audio") if chosen else None
        return chosen


async def prepare(sessions, request):
    started = time.monotonic()

    async def operation(session):
        phases = [{"phase": "setup", "started_ms": 0, "elapsed_ms": round((time.monotonic() - started) * 1000, 3)}]
        stream = {}
        base = copy.copy(session.base)
        base.http = _MediaHttp(session.http)
        base.apple_music_api = copy.copy(session.base.apple_music_api)
        base.wrapper_api = copy.copy(session.base.wrapper_api)

        def measure(target, name, phase):
            original = getattr(target, name)

            async def measured(*args, **kwargs):
                before = time.monotonic()
                try:
                    return await original(*args, **kwargs)
                finally:
                    phases.append({"phase": phase, "started_ms": round((before - started) * 1000, 3),
                        "elapsed_ms": round((time.monotonic() - before) * 1000, 3)})

            setattr(target, name, measured)

        async def response(url, valid_responses=(200,)):
            return await base.http.get(url)

        async def cover_bytes(url):
            response = await session.http.get(url)
            if response.status_code == 404:
                return None
            response.raise_for_status()
            if response.is_redirect:
                raise ProcessFailure("download_failed")
            return response.content

        base.get_response = response
        base.get_cover_bytes = cover_bytes
        measure(base.apple_music_api, "get_song", "catalog")
        measure(base.apple_music_api, "get_webplayback", "playback")
        measure(base.wrapper_api, "get_playback", "playback")
        measure(base, "get_tags_from_asset_info", "metadata")
        measure(base, "get_cover_bytes", "artwork")
        cover_task = None
        get_cover = base.get_cover

        async def cover(metadata):
            nonlocal cover_task
            result = await get_cover(metadata)
            cover_task = asyncio.create_task(base.get_cover_bytes(result.url))
            return result

        base.get_cover = cover
        song = _Song(base, request["quality"], request.get("ceiling"), stream)
        measure(song, "get_lyrics", "lyrics")
        measure(song, "get_stream_info", "manifest")
        measure(base, "get_decryption_key", "license")
        media = AppleMusicMedia(request["song_id"])
        try:
            async for _ in song.get_media(media):
                pass
            media.cover_bytes = await cover_task if cover_task else None
            return media, base, {"elapsed_ms": round((time.monotonic() - started) * 1000, 3),
                "phases": phases, "stream": stream}
        finally:
            if cover_task is not None:
                if not cover_task.done():
                    cover_task.cancel()
                await asyncio.gather(cover_task, return_exceptions=True)

    try:
        return await sessions.prepare(request["token"], request["storefront"], operation)
    except GamdlInterfaceFormatNotAvailableError:
        raise ProcessFailure("audio_artifact_missing") from None


async def prepare_lyrics(sessions, request):
    async def operation(session):
        metadata = (await session.base.apple_music_api.get_song(request["song_id"]))["data"][0]
        lyrics = await AppleMusicSongInterface(session.base).get_lyrics(metadata)
        return lyrics.synced if lyrics else None

    return await sessions.prepare(request["token"], request["storefront"], operation)


def send(kind, payload):
    data = payload if isinstance(payload, bytes) else json.dumps(payload).encode()
    sys.stdout.buffer.write(kind + struct.pack(">I", len(data)) + data)
    sys.stdout.buffer.flush()


async def serve():
    settings = Settings.from_env()
    settings.prepare()
    sessions = AccountSessions(settings.data_root / "web-token",
        factory=lambda token, storefront: create_session(settings, token, storefront))
    try:
        while line := await asyncio.to_thread(sys.stdin.readline):
            chunks = None
            try:
                request = json.loads(line)
                if request.get("mode") == "lyrics":
                    send(b"H", {"lyrics": await prepare_lyrics(sessions, request)})
                    send(b"E", {})
                    continue
                with tempfile.TemporaryDirectory(prefix="audio-", dir=settings.data_root / "temporary") as work:
                    for attempt in range(2):
                        try:
                            media, base, preparation = await prepare(sessions, request)
                            chunks = audio_chunks(media, base, settings, Path(work), ceiling=request.get("ceiling"),
                                original_container=request.get("original_container", False),
                                force_flac=request.get("force_flac", False))
                            header = asdict(await anext(chunks))
                            first = await anext(chunks)
                            break
                        except ProcessFailure as exc:
                            if chunks is not None:
                                await chunks.aclose()
                                chunks = None
                            if attempt or request["quality"] == "aac-web" or exc.code not in {
                                "artifact_missing", "audio_artifact_missing", "manifest_invalid"}:
                                raise
                            request = {**request, "quality": "aac-web"}
                    duration = media.media_metadata.get("attributes", {}).get("durationInMillis")
                    header.update(duration_seconds=duration / 1000 if type(duration) in (int, float) else None,
                        lyrics=media.lyrics.synced if media.lyrics else None, preparation=preparation)
                    send(b"H", header)
                    send(b"D", first)
                    async for chunk in chunks:
                        send(b"D", chunk)
                    send(b"E", {})
            except (ProcessFailure, AccountFailure) as exc:
                send(b"F", {"code": exc.code})
            except Exception:
                send(b"F", {"code": "download_failed"})
            finally:
                if chunks is not None:
                    with contextlib.suppress(Exception):
                        await chunks.aclose()
    finally:
        await sessions.close()


if __name__ == "__main__":
    import structlog
    structlog.configure(logger_factory=structlog.WriteLoggerFactory(file=sys.stderr))
    logging.basicConfig(stream=sys.stderr)
    asyncio.run(serve())
