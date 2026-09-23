#!/usr/bin/env python3
"""Opt-in full-song parity and seekability qualification for Jellyfin clients."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path

MAX_BYTES = 160 * 1024 * 1024
RANGE_BYTES = 65_536
IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]+$")


@dataclass(frozen=True)
class Received:
    status: int
    headers: dict[str, str]
    size: int

    def header(self, name: str) -> str:
        return self.headers.get(name.lower(), "")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise ValueError("unexpected media redirect")


def audio_url(base: str, song_id: str, user_id: str) -> str:
    parsed = urllib.parse.urlsplit(base)
    if (parsed.scheme not in {"http", "https"} or not parsed.netloc or
            parsed.username or parsed.password or parsed.query or parsed.fragment):
        raise ValueError("invalid server URL")
    if not IDENTIFIER.fullmatch(song_id) or not IDENTIFIER.fullmatch(user_id):
        raise ValueError("invalid song or user ID")
    query = urllib.parse.urlencode({"static": "true", "UserId": user_id})
    path = f"{parsed.path.rstrip('/')}/Audio/{urllib.parse.quote(song_id)}/stream"
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, path, query, ""))


def receive(url: str, token: str, path: Path, byte_range: str | None = None) -> Received:
    authorization = (
        'MediaBrowser Client="AllstarrLiveAudio", Device="Qualification", '
        'DeviceId="allstarr-live-audio", Version="1", Token="' + token + '"'
    )
    headers = {"Authorization": authorization}
    if byte_range:
        headers["Range"] = f"bytes={byte_range}"
    request = urllib.request.Request(url, headers=headers)
    opener = urllib.request.build_opener(NoRedirect())
    try:
        response = opener.open(request, timeout=180)
    except urllib.error.HTTPError as error:
        raise AssertionError(f"audio request returned HTTP {error.code}") from None
    limit = RANGE_BYTES if byte_range else MAX_BYTES
    with response, path.open("wb") as output:
        expected = response.headers.get("Content-Length")
        if expected and int(expected) > limit:
            raise AssertionError("audio response exceeds the bounded test size")
        size = 0
        while chunk := response.read(64 * 1024):
            size += len(chunk)
            if size > limit:
                raise AssertionError("audio response exceeded the bounded test size")
            output.write(chunk)
        if expected and int(expected) != size:
            raise AssertionError(f"Content-Length {expected} did not match {size} delivered bytes")
        return Received(response.status, {key.lower(): value for key, value in response.headers.items()}, size)


def flac_samples(path: Path) -> tuple[int, int, int]:
    with path.open("rb") as audio:
        prefix = audio.read(10)
        offset = 0
        if prefix.startswith(b"ID3"):
            if len(prefix) != 10 or any(value & 0x80 for value in prefix[6:10]):
                raise AssertionError("invalid FLAC guidance tag")
            offset = 10 + (prefix[6] << 21) + (prefix[7] << 14) + (prefix[8] << 7) + prefix[9]
            if prefix[3] == 4 and prefix[5] & 0x10:
                offset += 10
        audio.seek(offset)
        header = audio.read(26)
    if len(header) < 26 or header[:4] != b"fLaC" or header[4] & 0x7F or int.from_bytes(header[5:8], "big") < 34:
        raise AssertionError("audio is not a FLAC stream with STREAMINFO")
    packed = int.from_bytes(header[18:26], "big")
    return packed >> 44, packed & ((1 << 36) - 1), offset


def playable_duration(path: Path) -> float:
    if not shutil.which("ffprobe") or not shutil.which("ffmpeg"):
        raise RuntimeError("full-song qualification requires ffprobe and ffmpeg")
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, timeout=30, check=True,
    )
    duration = float(json.loads(probe.stdout)["format"]["duration"])
    if not 0 < duration < 24 * 60 * 60:
        raise AssertionError("audio has no finite, plausible duration")
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-xerror", "-i", str(path), "-f", "null", "-"],
        capture_output=True, timeout=180, check=True,
    )
    return duration


def verify_range(url: str, token: str, complete: Path, scratch: Path, suffix: bool) -> None:
    size = complete.stat().st_size
    length = min(RANGE_BYTES, size)
    start = size - length if suffix else 0
    value = f"-{length}" if suffix else f"0-{length - 1}"
    received = receive(url, token, scratch, value)
    expected_range = f"bytes {start}-{start + length - 1}/{size}"
    if received.status != 206 or received.size != length or received.header("content-range") != expected_range:
        raise AssertionError(f"range response did not match {expected_range}")
    with complete.open("rb") as audio:
        audio.seek(start)
        expected = audio.read(length)
    if scratch.read_bytes() != expected:
        raise AssertionError("ranged bytes differ from the complete song")


def qualify_native(direct_base: str, allstarr_base: str, token: str, user_id: str, song_id: str, work: Path) -> None:
    direct = work / "native-direct"
    proxied = work / "native-proxied"
    direct_result = receive(audio_url(direct_base, song_id, user_id), token, direct)
    proxy_url = audio_url(allstarr_base, song_id, user_id)
    proxy_result = receive(proxy_url, token, proxied)
    if direct_result.status != 200 or proxy_result.status != 200 or direct_result.size == 0:
        raise AssertionError("native full-song responses were not successful")
    with direct.open("rb") as original, proxied.open("rb") as replay:
        if hashlib.file_digest(original, "sha256").digest() != hashlib.file_digest(replay, "sha256").digest():
            raise AssertionError("native song bytes changed through Allstarr")
    duration = playable_duration(proxied)
    verify_range(proxy_url, token, proxied, work / "range", False)
    verify_range(proxy_url, token, proxied, work / "range", True)
    print(f"PASS native full-song parity bytes={proxy_result.size} duration_s={duration:.2f} ranges=exact")


def qualify_external(allstarr_base: str, token: str, user_id: str, song_id: str, work: Path) -> None:
    url = audio_url(allstarr_base, song_id, user_id)
    first = work / "external-first"
    cached = work / "external-cached"
    first_result = receive(url, token, first)
    if first_result.status != 200 or first_result.size == 0 or not first_result.header("x-allstarr-provider"):
        raise AssertionError("external song did not return audio from an identified provider")
    first_duration = playable_duration(first)
    first_offset = 0
    if first_result.header("content-type").startswith("audio/flac"):
        _, samples, first_offset = flac_samples(first)
        if samples == 0:
            raise AssertionError("progressive FLAC has no duration in STREAMINFO")
    cached_result = receive(url, token, cached)
    if cached_result.status != 200 or cached_result.size == 0 or cached_result.header("accept-ranges") != "bytes":
        raise AssertionError("completed external song is not a seekable cache response")
    if int(cached_result.header("content-length") or 0) != cached_result.size:
        raise AssertionError("cached song lacks an exact Content-Length")
    if cached_result.size != first_result.size - first_offset:
        raise AssertionError("cached song size differs from the progressive audio after its metadata prefix")
    if first_result.header("content-type").startswith("audio/flac"):
        _, samples, cached_offset = flac_samples(cached)
        if samples == 0 or cached_offset != 0:
            raise AssertionError("cached FLAC is not a clean, duration-stamped file")
    with first.open("rb") as original, cached.open("rb") as replay:
        original.seek(first_offset)
        if hashlib.file_digest(original, "sha256").digest() != hashlib.file_digest(replay, "sha256").digest():
            raise AssertionError("cached song audio differs from the first stream")
    cached_duration = playable_duration(cached)
    if abs(cached_duration - first_duration) > 1:
        raise AssertionError("cached song duration differs from the first stream")
    verify_range(url, token, cached, work / "range", False)
    verify_range(url, token, cached, work / "range", True)
    print(f"PASS external full-song bytes={cached_result.size} duration_s={cached_duration:.2f} ranges=exact")


def main() -> int:
    token = os.environ.get("JELLYFIN_TOKEN", "")
    user_id = os.environ.get("JELLYFIN_USER_ID", "")
    direct_base = os.environ.get("DIRECT_BASE", "")
    allstarr_base = os.environ.get("ALLSTARR_BASE", "")
    native_id = os.environ.get("NATIVE_SONG_ID", "")
    external_id = os.environ.get("EXTERNAL_SONG_ID", "")
    if not token or not user_id or not direct_base or not allstarr_base or not (native_id or external_id):
        print("Set Jellyfin token, user, direct/Allstarr URLs, and at least one song ID", file=sys.stderr)
        return 2
    try:
        with tempfile.TemporaryDirectory(prefix="allstarr-live-audio-") as directory:
            work = Path(directory)
            if native_id:
                qualify_native(direct_base, allstarr_base, token, user_id, native_id, work)
            if external_id:
                qualify_external(allstarr_base, token, user_id, external_id, work)
    except (AssertionError, ValueError, RuntimeError, OSError, subprocess.SubprocessError) as error:
        print(f"FAIL full-song qualification: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
