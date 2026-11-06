"""Process-local account binding around the pinned GAMDL command."""
from __future__ import annotations

import asyncio
import base64
import contextlib
import importlib
import io
import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any

from gamdl.api import AppleMusicApi

from .security import diagnostic_tail

ACCOUNT_MISMATCH_EXIT = 78
ACCOUNT_UNAVAILABLE_EXIT = 79
# Apple names each lossless stream by sample rate and bit depth, e.g. audio-alac-stereo-96000-24.
ALAC_STREAM = re.compile(r"audio-alac-stereo-(\d+)-(\d+)")
STREAM_NAME = re.compile(r"audio-[A-Za-z0-9-]{1,48}")


def lossless_playlist(playlists, chosen, ceiling):
    if ceiling is None:
        return chosen
    lossless = [(playlist, int(match.group(1)), int(match.group(2))) for playlist in playlists
        if (match := ALAC_STREAM.fullmatch(str(playlist.get("stream_info", {}).get("audio") or "")))]
    fitting = [playlist for playlist, rate, depth in lossless if depth <= ceiling[0] and rate <= ceiling[1]]
    if fitting:
        return max(fitting, key=lambda playlist: playlist["stream_info"].get("average_bandwidth") or 0)
    if lossless:
        return min(lossless, key=lambda item: (item[1] * item[2], item[0]["stream_info"].get("average_bandwidth") or 0))[0]
    return chosen


class AccountFailure(RuntimeError):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def _account_id(api: Any) -> str | None:
    payload = getattr(api, "account_info", None)
    if not isinstance(payload, dict):
        return None
    data = payload.get("data")
    if not isinstance(data, list) or not data or not isinstance(data[0], dict):
        return None
    value = data[0].get("id")
    return value if isinstance(value, str) and value.strip() else None


def cached_web_token(path: Path) -> str | None:
    """Apple's web player token is public and lasts for months; reuse it until an hour before it expires."""
    try:
        token = path.read_text().strip()
        claims = token.split(".")[1]
        expires = json.loads(base64.urlsafe_b64decode(claims + "=" * (-len(claims) % 4))).get("exp")
        return token if type(expires) in (int, float) and expires > time.time() + 3600 else None
    except (OSError, ValueError, IndexError, AttributeError):
        return None


def save_web_token(path: Path, token: str) -> None:
    partial = path.with_name(f"{path.name}.{os.getpid()}.partial")
    with contextlib.suppress(OSError):
        partial.write_text(token)
        os.chmod(partial, 0o600)
        partial.replace(path)
    with contextlib.suppress(OSError):
        partial.unlink(missing_ok=True)


def account_api_type(cookie_path: Path, failures: list[str], token_path: Path | None = None) -> type[AppleMusicApi]:
    async def selected_api(*args, **kwargs):
        create = AppleMusicApi.create_from_netscape_cookies
        if token_path is None or "token" in kwargs:
            return await create(cookies_path=str(cookie_path), *args, **kwargs)
        # Without a token GAMDL reads Apple's whole web player script for one, on every song.
        if cached := cached_web_token(token_path):
            with contextlib.suppress(Exception):
                return await create(cookies_path=str(cookie_path), *args, token=cached, **kwargs)
        token = await AppleMusicApi.get_token()
        save_web_token(token_path, token)
        return await create(cookies_path=str(cookie_path), *args, token=token, **kwargs)

    class AccountAppleMusicApi(AppleMusicApi):
        @classmethod
        async def create_from_wrapper(cls, wrapper_api, *args, **kwargs):
            # The selected account and the wrapper's account are looked up together; neither waits on the other.
            tasks = [
                asyncio.ensure_future(selected_api(*args, **kwargs)),
                asyncio.ensure_future(AppleMusicApi.create_from_wrapper(wrapper_api, *args, **kwargs)),
            ]
            accepted = False
            try:
                await asyncio.wait(tasks)
                selected, comparison = tasks[0].result(), tasks[1].result()
                selected_id = _account_id(selected)
                wrapper_id = _account_id(comparison)
                if selected_id is None or wrapper_id is None or selected_id != wrapper_id:
                    raise AccountFailure("account_mismatch")
                accepted = True
                return selected
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                code = "account_mismatch" if isinstance(exc, AccountFailure) else "account_unavailable"
                failures.append(code)
                raise AccountFailure(code) from None
            finally:
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
                for index, task in enumerate(tasks):
                    if task.cancelled() or task.exception() is not None or index == 0 and accepted:
                        continue
                    with contextlib.suppress(Exception):
                        await task.result().client.aclose()

    return AccountAppleMusicApi


class _DiagnosticBuffer(io.TextIOBase):
    def __init__(self):
        self.text = ""
        self.truncated = False

    def write(self, value: str) -> int:
        text = self.text + value
        self.truncated = self.truncated or len(text) > 32768
        self.text = text[-32768:]
        return len(value)

    def tail(self) -> str:
        return self.text.partition("\n")[2] if self.truncated else self.text


@contextlib.contextmanager
def song_preparation(cli, started: float | None = None, ceiling: tuple[int, int] | None = None,
        stream: dict | None = None):
    from gamdl.interface.enums import CoverFormat
    started = time.monotonic() if started is None else started
    phases = []
    originals = []
    select = vars(cli.AppleMusicSongInterface).get("_get_playlist_from_codec_enhanced")
    if select is not None and stream is not None:
        # GAMDL takes the largest lossless stream Apple offers. Within a quality limit the largest stream that fits is
        # enough, and a smaller file downloads and decrypts sooner. When none fits, conversion applies the limit anyway,
        # so the smallest lossless stream gives the same result: 48 kHz instead of 96 kHz halves the download.
        def choose(self, m3u8_data, codec):
            chosen = select(self, m3u8_data, codec)
            playlists = m3u8_data.get("playlists", [])
            if getattr(codec, "value", None) == "alac" and ceiling is not None:
                chosen = lossless_playlist(playlists, chosen, ceiling)
            names = {playlist.get("stream_info", {}).get("audio") for playlist in playlists}
            stream["offered"] = sorted(name for name in names if isinstance(name, str) and STREAM_NAME.fullmatch(name))[:16]
            stream["chosen"] = chosen.get("stream_info", {}).get("audio") if chosen else None
            return chosen

        originals.append((cli.AppleMusicSongInterface, "_get_playlist_from_codec_enhanced", select))
        cli.AppleMusicSongInterface._get_playlist_from_codec_enhanced = choose

    def measure(target, name, phase):
        original = vars(target)[name]

        async def measured(self, *args, **kwargs):
            before = time.monotonic()
            if not phases:
                phases.append({"phase": "setup", "started_ms": 0,
                    "elapsed_ms": round((before - started) * 1000, 3)})
            try:
                return await original(self, *args, **kwargs)
            finally:
                if len(phases) < 64:
                    phases.append({"phase": phase, "started_ms": round((before - started) * 1000, 3),
                        "elapsed_ms": round((time.monotonic() - before) * 1000, 3)})

        originals.append((target, name, original))
        setattr(target, name, measured)
        return measured

    measure(AppleMusicApi, "get_song", "catalog")
    measure(cli.AppleMusicSongInterface, "get_lyrics", "lyrics")
    measure(cli.WrapperApi, "get_playback", "playback")
    measure(AppleMusicApi, "get_webplayback", "playback")
    measure(cli.AppleMusicBaseInterface, "get_tags_from_asset_info", "metadata")
    measure(cli.AppleMusicBaseInterface, "get_itunes_lookup_metadata", "itunes")
    measure(cli.AppleMusicSongInterface, "get_stream_info", "manifest")
    measure(cli.AppleMusicBaseInterface, "get_decryption_key", "license")
    measure(cli.AppleMusicBaseDownloader, "download_stream", "download")
    measure(cli.AppleMusicSongDownloader, "stage", "decrypt_mux")
    cover_bytes = measure(cli.AppleMusicBaseInterface, "get_cover_bytes", "artwork")
    measure(cli.AppleMusicBaseDownloader, "apply_tags", "tagging")
    pending_covers = {}
    original_download = cli.AppleMusicSongDownloader.download

    async def get_cover_bytes(self, url):
        if (task := pending_covers.get((self, url))) is not None:
            return await task
        return await cover_bytes(self, url)

    async def download(self, item):
        interface = self.base.interface.base
        if interface.cover_format == CoverFormat.RAW:
            return await original_download(self, item)
        key = (interface, item.media.cover.url)
        task = asyncio.create_task(cover_bytes(*key))
        pending_covers[key] = task
        try:
            # The original downloader still awaits artwork before tagging. Keep
            # the task's result, including failures, until it reaches that point.
            return await original_download(self, item)
        finally:
            if not task.done():
                task.cancel()
            await asyncio.gather(task, return_exceptions=True)
            pending_covers.pop(key, None)

    cli.AppleMusicBaseInterface.get_cover_bytes = get_cover_bytes
    originals.append((cli.AppleMusicSongDownloader, "download", original_download))
    cli.AppleMusicSongDownloader.download = download
    try:
        yield phases
    finally:
        for target, name, original in reversed(originals):
            setattr(target, name, original)


def _take_option(argv: list[str], name: str) -> str | None:
    if name not in argv:
        return None
    index = argv.index(name)
    value = argv[index + 1]
    del argv[index:index + 2]
    return value


def run(argv: list[str]) -> int:
    started = time.monotonic()
    failures: list[str] = []
    try:
        argv = list(argv)
        # Gateway options are read here and never reach GAMDL.
        limit = _take_option(argv, "--allstarr-alac-ceiling")
        ceiling = None if limit is None else tuple(int(part) for part in limit.split("-", 1))
        if ceiling is not None and len(ceiling) != 2:
            raise ValueError()
        token_option = _take_option(argv, "--allstarr-web-token")
        token_path = None if token_option is None else Path(token_option)
        if argv.count("--cookies-path") != 1 or "--use-wrapper" not in argv or "--no-config-file" not in argv:
            raise ValueError()
        cookie_path = Path(argv[argv.index("--cookies-path") + 1])
        if cookie_path.is_symlink() or not cookie_path.is_file():
            raise ValueError()
    except (ValueError, IndexError):
        return ACCOUNT_UNAVAILABLE_EXIT

    cli = importlib.import_module("gamdl.cli.cli")
    original = cli.AppleMusicApi
    original_writer = cli.CustomOutputWriter
    # This replacement happens only in the short-lived command process, never
    # when this module is imported by the ASGI gateway.
    cli.AppleMusicApi = account_api_type(cookie_path, failures, token_path)
    diagnostics = _DiagnosticBuffer()
    phases = []
    stream: dict = {}
    result = 0
    try:
        with song_preparation(cli, started, ceiling, stream) as phases, \
                contextlib.redirect_stdout(diagnostics), contextlib.redirect_stderr(diagnostics):
            # Supply the bounded stream to the writer's captured stdout as well.
            cli.CustomOutputWriter = lambda: original_writer(streams=[diagnostics])
            cli.main.main(args=argv, standalone_mode=False)
    except Exception:
        result = ACCOUNT_UNAVAILABLE_EXIT
    finally:
        cli.AppleMusicApi = original
        cli.CustomOutputWriter = original_writer
    if failures:
        return ACCOUNT_MISMATCH_EXIT if "account_mismatch" in failures else ACCOUNT_UNAVAILABLE_EXIT
    if phases:
        report = {"elapsed_ms": round((time.monotonic() - started) * 1000, 3), "phases": phases}
        if stream:
            report["stream"] = stream
        sys.stdout.write(json.dumps(report) + "\n")
    token = next((line.split("\t")[-1] for line in cookie_path.read_text().splitlines()
        if "\tmedia-user-token\t" in line), "")
    tail = diagnostic_tail(diagnostics.tail(), token)
    if tail:
        sys.stderr.write(tail + "\n")
    return result


def wait_for_arguments() -> int:
    """Load GAMDL first, then wait for one song's arguments, so the song doesn't pay for loading it."""
    importlib.import_module("gamdl.cli.cli")
    try:
        request = json.loads(sys.stdin.readline())
        arguments, cwd = request["argv"], request["cwd"]
        if not isinstance(cwd, str) or not isinstance(arguments, list) or not all(isinstance(item, str) for item in arguments):
            raise ValueError()
        os.chdir(cwd)
    except (ValueError, KeyError, TypeError, OSError):
        return ACCOUNT_UNAVAILABLE_EXIT
    return run(arguments)


if __name__ == "__main__":
    raise SystemExit(wait_for_arguments() if sys.argv[1:] == ["--wait-for-arguments"] else run(sys.argv[1:]))
