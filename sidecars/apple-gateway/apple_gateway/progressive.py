from __future__ import annotations

import asyncio
import contextlib
import io
import os
import struct
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urljoin, urlsplit

import httpx
import m3u8

from .runner import ProcessFailure


@dataclass(frozen=True)
class AudioHeader:
    media_type: str
    suffix: str
    duration_seconds: float | None = None
    lyrics: str | None = None


def boxes(data, start=0, end=None):
    end = len(data) if end is None else end
    while start < end:
        if end - start < 8:
            raise ProcessFailure("invalid_audio_fragment")
        size = int.from_bytes(data[start:start + 4], "big")
        header = 8
        if size == 1:
            header = 16
            size = int.from_bytes(data[start + 8:start + 16], "big")
        if size < header or start + size > end:
            raise ProcessFailure("invalid_audio_fragment")
        yield bytes(data[start + 4:start + 8]), start, start + size, header
        start += size


def descendants(data, start=0, end=None):
    for kind, offset, stop, header in boxes(data, start, end):
        yield kind, offset, stop, header
        if kind in {b"moov", b"trak", b"mdia", b"minf", b"stbl", b"mvex", b"moof", b"traf"}:
            yield from descendants(data, offset + header, stop)


def clear_init(data):
    result = bytearray(data)
    facts = None
    for kind, offset, stop, header in descendants(data):
        if kind != b"stsd":
            continue
        for codec, entry, entry_end, entry_header in boxes(data, offset + header + 8, stop):
            if entry_end - entry < 36:
                raise ProcessFailure("invalid_audio_fragment")
            version = int.from_bytes(data[entry + 16:entry + 18], "big")
            children_start = entry + 36 + {0: 0, 1: 16, 2: 36}.get(version, 0)
            depth = int.from_bytes(data[entry + 26:entry + 28], "big")
            rate = int.from_bytes(data[entry + 32:entry + 36], "big") >> 16
            for child, child_offset, child_end, child_header in boxes(data, children_start, entry_end):
                if child == b"sinf":
                    for sub, sub_offset, sub_end, sub_header in boxes(data, child_offset + child_header, child_end):
                        if sub == b"frma":
                            codec = bytes(data[sub_offset + sub_header:sub_end])
                    result[child_offset + 4:child_offset + 8] = b"free"
                elif child == b"alac" and child_end - child_offset >= 36:
                    depth = data[child_offset + 17]
                    rate = int.from_bytes(data[child_offset + 32:child_offset + 36], "big")
            if codec not in {b"alac", b"mp4a"}:
                raise ProcessFailure("audio_artifact_missing")
            result[entry + 4:entry + 8] = codec
            current = ("alac" if codec == b"alac" else "aac", depth, rate)
            if facts is not None and facts != current:
                raise ProcessFailure("incompatible_audio_fragments")
            facts = current
    if facts is None:
        raise ProcessFailure("audio_artifact_missing")
    return bytes(result), facts


async def _segment(client, url, byte_range=None):
    headers = {} if byte_range is None else {"Range": f"bytes={byte_range[0]}-{byte_range[1]}"}
    try:
        async with client.stream("GET", url, headers=headers) as response:
            response.raise_for_status()
            if response.is_redirect:
                raise ProcessFailure("segment_download_failed")
            expected = None
            if byte_range is not None:
                expected = byte_range[1] - byte_range[0] + 1
                content_range = response.headers.get("content-range", "")
                if response.status_code != 206 or not content_range.startswith(
                        f"bytes {byte_range[0]}-{byte_range[1]}/"):
                    raise ProcessFailure("segment_download_failed")
            elif response.headers.get("content-length", "").isdigit():
                expected = int(response.headers["content-length"])
            received = 0
            async for chunk in response.aiter_bytes(64 * 1024):
                received += len(chunk)
                if expected is not None and received > expected:
                    raise ProcessFailure("segment_download_failed")
                yield chunk
            if received == 0 or expected is not None and received != expected:
                raise ProcessFailure("segment_download_failed")
    except httpx.HTTPError as exc:
        if isinstance(exc, httpx.HTTPStatusError) and exc.response.status_code in {401, 403}:
            raise
        raise ProcessFailure("segment_download_failed") from None


async def media_bytes(client, url):
    if not urlsplit(url).path.lower().endswith(".m3u8"):
        async for chunk in _segment(client, url):
            yield chunk
        return
    response = await client.get(url)
    response.raise_for_status()
    if response.is_redirect or len(response.content) > 4 * 1024 * 1024:
        raise ProcessFailure("manifest_invalid")
    playlist = m3u8.loads(response.text, uri=url)
    if not playlist.is_endlist or not playlist.segments or playlist.playlists:
        raise ProcessFailure("manifest_invalid")
    offsets = {}

    def location(uri, raw_range):
        target = urljoin(url, uri)
        byte_range = None
        if raw_range:
            length, separator, offset = raw_range.partition("@")
            if not length.isdigit() or int(length) <= 0 or separator and not offset.isdigit():
                raise ProcessFailure("manifest_invalid")
            if not separator and target not in offsets:
                raise ProcessFailure("manifest_invalid")
            start = int(offset) if separator else offsets[target]
            byte_range = start, start + int(length) - 1
            offsets[target] = byte_range[1] + 1
        return target, byte_range

    previous_init = None
    for segment in playlist.segments:
        if segment.discontinuity:
            raise ProcessFailure("incompatible_audio_fragments")
        init = segment.init_section
        if init is not None:
            current_init = (init.uri, init.byterange)
            if current_init != previous_init:
                if previous_init is not None:
                    raise ProcessFailure("incompatible_audio_fragments")
                async for chunk in _segment(client, *location(*current_init)):
                    yield chunk
                previous_init = current_init
        async for chunk in _segment(client, *location(segment.uri, segment.byterange)):
            yield chunk


class _Atoms:
    def __init__(self, source):
        self.source = source.__aiter__()
        self.pending = bytearray()
        self.offset = 0

    async def read(self):
        while len(self.pending) < 8:
            try:
                self.pending.extend(await anext(self.source))
            except StopAsyncIteration:
                if self.pending:
                    raise ProcessFailure("invalid_audio_fragment") from None
                return None
        size = int.from_bytes(self.pending[:4], "big")
        if size == 1:
            while len(self.pending) < 16:
                try:
                    self.pending.extend(await anext(self.source))
                except StopAsyncIteration:
                    raise ProcessFailure("invalid_audio_fragment") from None
            size = int.from_bytes(self.pending[8:16], "big")
        if size < 8 or size > 64 * 1024 * 1024:
            raise ProcessFailure("invalid_audio_fragment")
        while len(self.pending) < size:
            try:
                self.pending.extend(await anext(self.source))
            except StopAsyncIteration:
                raise ProcessFailure("invalid_audio_fragment") from None
        data = bytes(self.pending[:size])
        del self.pending[:size]
        offset = self.offset
        self.offset += size
        return offset, data[4:8], data


def _relocate(moof, original, target, *, clear=False):
    result = bytearray(moof)
    for kind, offset, end, header in descendants(moof):
        if kind == b"tfhd" and int.from_bytes(moof[offset + header:offset + header + 4], "big") & 1:
            position = offset + header + 8
            base = int.from_bytes(moof[position:position + 8], "big") + target - original
            if base < 0:
                raise ProcessFailure("invalid_audio_fragment")
            result[position:position + 8] = base.to_bytes(8, "big")
        if clear and kind in {b"senc", b"saiz", b"saio", b"sgpd", b"sbgp"}:
            result[offset + 4:offset + 8] = b"free"
    return bytes(result)


def _sample_bytes(init, moof):
    defaults = {}
    for kind, offset, end, header in descendants(init):
        if kind == b"trex":
            if end - offset < header + 24:
                raise ProcessFailure("invalid_audio_fragment")
            defaults[int.from_bytes(init[offset + header + 4:offset + header + 8], "big")] = \
                int.from_bytes(init[offset + header + 16:offset + header + 20], "big")
    total = count = 0
    for kind, offset, end, header in descendants(moof):
        if kind != b"traf":
            continue
        default = 0
        for child, start, stop, size in boxes(moof, offset + header, end):
            payload = moof[start + size:stop]
            if len(payload) < 8:
                raise ProcessFailure("invalid_audio_fragment")
            flags = int.from_bytes(payload[:4], "big") & 0xffffff
            if child == b"tfhd":
                default = defaults.get(int.from_bytes(payload[4:8], "big"), 0)
                position = 8 + (8 if flags & 1 else 0) + (4 if flags & 2 else 0) + (4 if flags & 8 else 0)
                if flags & 16:
                    default = int.from_bytes(payload[position:position + 4], "big")
            elif child == b"trun":
                samples = int.from_bytes(payload[4:8], "big")
                if samples > 100000:
                    raise ProcessFailure("invalid_audio_fragment")
                position = 8 + (4 if flags & 1 else 0) + (4 if flags & 4 else 0)
                for _ in range(samples):
                    if flags & 0x100:
                        position += 4
                    sample_size = int.from_bytes(payload[position:position + 4], "big") if flags & 0x200 else default
                    position += (4 if flags & 0x200 else 0) + (4 if flags & 0x400 else 0) + (4 if flags & 0x800 else 0)
                    if position > len(payload) or sample_size <= 0:
                        raise ProcessFailure("invalid_audio_fragment")
                    total += sample_size
                    count += 1
    if count == 0:
        raise ProcessFailure("invalid_audio_fragment")
    return total


async def _decrypt(init, moof, mdat, directory, media, base):
    from gamdl.downloader.ammuxer import decrypt_and_mux_hex, decrypt_and_mux_wrapper

    encrypted, decrypted = directory / "fragment.encrypted", directory / "fragment.m4a"
    encrypted.write_bytes(init + moof + mdat)
    track = media.stream_info.audio_track
    try:
        if media.decryption_key:
            await decrypt_and_mux_hex(media.decryption_key.audio_track.key, str(encrypted), str(decrypted),
                use_cenc=track.use_cenc, use_single_content_key=track.use_single_content_key)
        else:
            await decrypt_and_mux_wrapper(base.wrapper_api, media.media_id, str(encrypted), str(decrypted),
                track.fairplay_key, use_single_content_key=track.use_single_content_key)
        data = decrypted.read_bytes()
        payloads = [data[start + header:end] for kind, start, end, header in boxes(data) if kind == b"mdat"]
        header_size = 16 if mdat[:4] == b"\x00\x00\x00\x01" else 8
        payload = b"".join(payloads)
        if len(payload) != len(mdat) - header_size:
            raise ProcessFailure("decrypt_failed")
        return mdat[:header_size] + payload
    except ProcessFailure:
        raise
    except Exception:
        raise ProcessFailure("decrypt_failed") from None
    finally:
        encrypted.unlink(missing_ok=True)
        decrypted.unlink(missing_ok=True)


async def _clear_fragments(atoms, init, clear, media, base, directory, decrypt):
    yield clear
    target_offset = len(clear)
    count = 0
    while atom := await atoms.read():
        offset, kind, moof = atom
        if kind in {b"styp", b"sidx", b"emsg", b"free", b"mfra"}:
            continue
        if kind != b"moof":
            raise ProcessFailure("invalid_audio_fragment")
        following = await atoms.read()
        if following is None or following[1] != b"mdat":
            raise ProcessFailure("invalid_audio_fragment")
        mdat = following[2]
        header = 16 if mdat[:4] == b"\x00\x00\x00\x01" else 8
        if _sample_bytes(init, moof) != len(mdat) - header:
            raise ProcessFailure("invalid_audio_fragment")
        if not media.stream_info.audio_track.drm_free:
            mdat = await decrypt(init, _relocate(moof, offset, len(init)), mdat, directory, media, base)
        yield _relocate(moof, offset, target_offset, clear=True) + mdat
        target_offset += len(moof) + len(mdat)
        count += 1
    if count == 0:
        raise ProcessFailure("audio_artifact_missing")


def _flac_comments(tags):
    fields = {"title": "TITLE", "artist": "ARTIST", "album": "ALBUM", "album_artist": "ALBUMARTIST",
        "composer": "COMPOSER", "genre": "GENRE", "date": "DATE", "copyright": "COPYRIGHT",
        "lyrics": "LYRICS", "comment": "COMMENT", "track": "TRACKNUMBER", "track_total": "TRACKTOTAL",
        "disc": "DISCNUMBER", "disc_total": "DISCTOTAL"}
    values = [f"{name}={getattr(tags, field)}".encode("utf-8") for field, name in fields.items()
        if getattr(tags, field, None) is not None]
    return struct.pack("<II", 0, len(values)) + b"".join(struct.pack("<I", len(value)) + value for value in values)


async def _tagged_output(source, header, media):
    pending = bytearray()
    ready = False
    audio_bytes = 0
    async for chunk in source:
        if ready:
            audio_bytes += len(chunk)
            yield chunk
            continue
        pending.extend(chunk)
        if len(pending) > 16 * 1024 * 1024:
            raise ProcessFailure("tagging_failed")
        if header.suffix == ".flac":
            if len(pending) < 8:
                continue
            if pending[:4] != b"fLaC":
                raise ProcessFailure("transcode_failed")
            offset = 4
            blocks = []
            while offset + 4 <= len(pending):
                kind = pending[offset] & 127
                stop = offset + 4 + int.from_bytes(pending[offset + 1:offset + 4], "big")
                if stop > len(pending):
                    break
                if kind not in {1, 4, 6}:
                    blocks.append((kind, bytes(pending[offset + 4:stop])))
                last = bool(pending[offset] & 128)
                offset = stop
                if last:
                    blocks.append((4, _flac_comments(media.tags)))
                    if media.cover_bytes:
                        from mutagen.flac import Picture
                        picture = Picture()
                        picture.type, picture.mime, picture.data = 3, "image/jpeg", media.cover_bytes
                        blocks.append((6, picture.write()))
                    prefix = b"fLaC" + b"".join(bytes([kind | (128 if index == len(blocks) - 1 else 0)])
                        + len(data).to_bytes(3, "big") + data for index, (kind, data) in enumerate(blocks))
                    audio_bytes += len(pending) - offset
                    yield prefix + pending[offset:]
                    pending.clear()
                    ready = True
                    break
        else:
            offset = 0
            while offset + 8 <= len(pending):
                length = int.from_bytes(pending[offset:offset + 4], "big")
                if length < 8:
                    raise ProcessFailure("tagging_failed")
                if offset + length > len(pending):
                    break
                kind = pending[offset + 4:offset + 8]
                offset += length
                if kind == b"moov":
                    from mutagen.mp4 import MP4, MP4Cover
                    output = io.BytesIO(pending[:offset])
                    tags = MP4(output)
                    tags.update(media.tags.as_mp4_tags())
                    if media.cover_bytes:
                        tags["covr"] = [MP4Cover(media.cover_bytes, imageformat=MP4Cover.FORMAT_JPEG)]
                    tags.save(output, padding=lambda _: 0)
                    audio_bytes += len(pending) - offset
                    yield output.getvalue() + pending[offset:]
                    pending.clear()
                    ready = True
                    break
    if not ready or not audio_bytes:
        raise ProcessFailure("audio_artifact_missing")


async def audio_chunks(media, base, settings, directory, *, ceiling=None, original_container=False,
        force_flac=False, source=None, decrypt=None):
    source = media_bytes(base.http, media.stream_info.audio_track.stream_url) if source is None else source
    atoms = _Atoms(source)
    init = bytearray()
    while atom := await atoms.read():
        if atom[1] not in {b"ftyp", b"moov", b"free", b"sidx"}:
            raise ProcessFailure("invalid_audio_fragment")
        if atom[1] in {b"ftyp", b"moov"}:
            init.extend(atom[2])
        if len(init) > 8 * 1024 * 1024:
            raise ProcessFailure("invalid_audio_fragment")
        if atom[1] == b"moov":
            break
    clear, (codec, depth, rate) = clear_init(init)
    reduce = codec == "alac" and ceiling is not None and (depth > ceiling[0] or rate > ceiling[1])
    copy = not force_flac and (codec == "aac" or original_container and codec == "alac" and not reduce)
    header = AudioHeader("audio/mp4", ".m4a") if copy else AudioHeader("audio/flac", ".flac")
    conversion = [] if copy or not reduce else ["-ar", str(ceiling[1]), "-sample_fmt", "s16" if ceiling[0] <= 16 else "s32"]
    output = ["-c:a", "copy", "-movflags", "+empty_moov+frag_keyframe+default_base_moof",
        "-frag_duration", "200000", "-f", "mp4"] if copy else [
        *conversion, "-c:a", "flac", "-compression_level", "0", "-f", "flac"]
    validation = ["-map", "0:a:0", "-threads", "1", "-f", "null", os.devnull] if copy else []
    process = await asyncio.create_subprocess_exec(settings.ffmpeg_path, "-nostdin", "-v", "error", "-xerror",
        "-err_detect", "explode", "-probesize", "32768", "-analyzeduration", "0", "-threads", "1",
        "-filter_threads", "1", "-f", "mp4", "-i", "pipe:0", "-map", "0:a:0", "-map_metadata", "-1",
        "-threads", "1", *output, "-flush_packets", "1", "pipe:1", *validation,
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    stderr = bytearray()

    async def diagnostics():
        while chunk := await process.stderr.read(8192):
            stderr.extend(chunk)
            del stderr[:-32768]

    async def feed():
        try:
            async for chunk in _clear_fragments(atoms, bytes(init), clear, media, base, directory, decrypt or _decrypt):
                process.stdin.write(chunk)
                await process.stdin.drain()
        except (BrokenPipeError, ConnectionResetError):
            raise ProcessFailure("transcode_failed") from None
        finally:
            process.stdin.close()

    producer = asyncio.create_task(feed())
    errors = asyncio.create_task(diagnostics())

    async def encoded():
        while True:
            reading = asyncio.create_task(process.stdout.read(64 * 1024))
            try:
                await asyncio.wait([reading, producer], return_when=asyncio.FIRST_COMPLETED)
                if producer.done():
                    await producer
                chunk = await reading
            finally:
                if not reading.done():
                    reading.cancel()
                    await asyncio.gather(reading, return_exceptions=True)
            if not chunk:
                break
            yield chunk
        await producer
        if await process.wait() != 0:
            await errors
            raise ProcessFailure("transcode_failed", stderr=stderr.decode("utf-8", errors="replace"))

    try:
        yield header
        async for chunk in _tagged_output(encoded(), header, media):
            yield bytes(chunk)
    finally:
        producer.cancel()
        if process.returncode is None:
            with contextlib.suppress(ProcessLookupError):
                process.kill()
        await process.wait()
        await asyncio.gather(producer, errors, return_exceptions=True)
        if close := getattr(source, "aclose", None):
            await close()
