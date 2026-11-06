from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import math
import os
import re
import signal
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import AsyncIterator

from .config import Settings
from .security import safe_files, valid_media_token

logger = logging.getLogger("uvicorn.error")


def _log_preparation(stdout: str, quality: str, elapsed_ms: float) -> None:
    try:
        payload = json.loads(stdout)
    except (ValueError, TypeError):
        return
    if not isinstance(payload, dict) or not isinstance(payload.get("phases"), list):
        return

    def numeric(value):
        return type(value) in (int, float) and 0 <= value <= 86400000

    if not numeric(payload.get("elapsed_ms")):
        return
    phases = []
    for phase in payload["phases"][:64]:
        if not isinstance(phase, dict) or not isinstance(phase.get("phase"), str) or phase["phase"] not in {
            "setup", "catalog", "lyrics", "playback", "metadata", "itunes", "manifest", "license",
            "download", "decrypt_mux", "artwork", "tagging",
        }:
            continue
        if numeric(phase.get("started_ms")) and numeric(phase.get("elapsed_ms")):
            phases.append({key: phase[key] for key in ("phase", "started_ms", "elapsed_ms")})
    logger.info("Song preparation quality=%s wait_process_ms=%.3f entry_ms=%s phases=%s",
        quality, elapsed_ms, payload["elapsed_ms"], json.dumps(phases))
    stream = payload.get("stream") if isinstance(payload.get("stream"), dict) else {}
    name = re.compile(r"audio-[A-Za-z0-9-]{1,48}")
    offered = [item for item in stream.get("offered", []) if isinstance(item, str) and name.fullmatch(item)][:16] \
        if isinstance(stream.get("offered"), list) else []
    chosen = stream.get("chosen") if isinstance(stream.get("chosen"), str) and name.fullmatch(stream["chosen"]) else None
    if chosen or offered:
        logger.info("Song stream chosen=%s offered=%s", chosen, ",".join(offered))


@dataclass(frozen=True, slots=True)
class ProcessResult:
    return_code: int
    stdout: str
    stderr: str


@dataclass(frozen=True)
class AudioCeiling:
    bit_depth: int
    sample_rate: int


class ProcessFailure(RuntimeError):
    def __init__(self, code: str, *, stderr: str = ""):
        super().__init__(code)
        self.code = code
        self.stderr = stderr
        self.quality: str | None = None


class BoundedProcessRunner:
    # Python and GAMDL take over a second to load, which every song paid. One helper is started ahead of time and
    # waits for its song's arguments; each song still runs alone in its own process.
    WARM_COMMAND: tuple[str, ...] = (sys.executable, "-m", "apple_gateway.account_cli")

    def __init__(self, settings: Settings):
        self._settings = settings
        self._semaphore = asyncio.Semaphore(settings.max_concurrency)
        self._warm: asyncio.subprocess.Process | None = None
        self._warming: asyncio.Task | None = None
        self._probed: dict[str, dict[str, str]] = {}
        self._audio_warming: asyncio.Task | None = None
        from .workers import WorkerPool
        self._audio_workers = WorkerPool(settings, semaphore=self._semaphore)

    async def _start_warm(self) -> None:
        if self._warm is None or self._warm.returncode is not None:
            self._warm = await asyncio.create_subprocess_exec(
                *self.WARM_COMMAND, "--wait-for-arguments",
                cwd=self._settings.data_root,
                stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                start_new_session=True,
            )

    def _prepare_warm(self) -> None:
        if self._warming is None or self._warming.done():
            self._warming = asyncio.create_task(self._start_warm())

    def warm_up(self) -> None:
        """Start the first helper with the gateway, so the first song after a restart is as quick as the rest."""
        self._prepare_warm()

    def warm_up_audio(self) -> None:
        if self._audio_warming is None or self._audio_warming.done():
            self._audio_warming = asyncio.create_task(self._audio_workers.warm_up())

    @contextlib.asynccontextmanager
    async def open_audio(self, song_id: str, quality: str, *, media_user_token: str, storefront: str,
            ceiling: AudioCeiling | None = None, original_container: bool = False, force_flac: bool = False):
        from .progressive import AudioHeader

        if not valid_media_token(media_user_token):
            raise ValueError("account_context_required")
        request = {"song_id": song_id, "quality": quality, "token": media_user_token, "storefront": storefront,
            "ceiling": None if ceiling is None else [ceiling.bit_depth, ceiling.sample_rate],
            "original_container": original_container, "force_flac": force_flac}
        started = time.monotonic()
        async with self._audio_workers.open(request) as (payload, chunks):
            media_type, suffix = payload.get("media_type"), payload.get("suffix")
            if (media_type, suffix) not in {("audio/flac", ".flac"), ("audio/mp4", ".m4a")}:
                raise ProcessFailure("worker_failed")
            _log_preparation(json.dumps(payload.get("preparation")), quality, (time.monotonic() - started) * 1000)
            duration = payload.get("duration_seconds")
            if type(duration) not in (float, int) or not math.isfinite(duration) or duration <= 0:
                duration = None
            yield AudioHeader(media_type, suffix, duration, payload.get("lyrics")), chunks

    async def lyrics(self, song_id: str, *, media_user_token: str, storefront: str) -> str | None:
        if not valid_media_token(media_user_token):
            raise ValueError("account_context_required")
        async with self._audio_workers.open({"mode": "lyrics", "song_id": song_id,
                "token": media_user_token, "storefront": storefront}) as (header, chunks):
            async for _ in chunks:
                raise ProcessFailure("worker_failed")
            content = header.get("lyrics")
            return content if isinstance(content, str) else None

    async def _hand_to_warm(self, arguments: list[str], cwd: Path) -> asyncio.subprocess.Process | None:
        process, self._warm = self._warm, None
        if process is None or process.returncode is not None or process.stdin is None:
            return None
        try:
            process.stdin.write((json.dumps({"argv": arguments, "cwd": str(cwd)}) + "\n").encode())
            await process.stdin.drain()
            process.stdin.close()
            return process
        except (BrokenPipeError, ConnectionResetError):
            with contextlib.suppress(ProcessLookupError):
                os.killpg(process.pid, signal.SIGKILL)
            await process.wait()
            return None

    async def close(self) -> None:
        if self._audio_warming is not None:
            await asyncio.gather(self._audio_warming, return_exceptions=True)
        await self._audio_workers.close()
        if self._warming is not None and not self._warming.done():
            self._warming.cancel()
            await asyncio.gather(self._warming, return_exceptions=True)
        process, self._warm = self._warm, None
        if process is not None and process.returncode is None:
            with contextlib.suppress(ProcessLookupError):
                os.killpg(process.pid, signal.SIGKILL)
            await process.wait()

    async def execute(self, argv: list[str], cwd: Path) -> ProcessResult:
        async with self._semaphore:
            process = None
            if tuple(argv[:len(self.WARM_COMMAND)]) == self.WARM_COMMAND:
                process = await self._hand_to_warm(argv[len(self.WARM_COMMAND):], cwd)
                self._prepare_warm()
            if process is None:
                process = await asyncio.create_subprocess_exec(
                    *argv,
                    cwd=cwd,
                    stdin=asyncio.subprocess.DEVNULL,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                    start_new_session=True,
                )
            stdout_task = asyncio.create_task(self._read_limited(process.stdout))
            stderr_task = asyncio.create_task(self._read_limited(process.stderr, retain_tail=True))
            try:
                await asyncio.wait_for(process.wait(), timeout=self._settings.subprocess_timeout_seconds)
            except (TimeoutError, asyncio.TimeoutError) as exc:
                if process.returncode is None:
                    os.killpg(process.pid, signal.SIGKILL)
                await process.wait()
                await asyncio.gather(stdout_task, stderr_task)
                raise ProcessFailure("process_timeout", stderr=stderr_task.result()) from exc
            except asyncio.CancelledError:
                if process.returncode is None:
                    os.killpg(process.pid, signal.SIGKILL)
                await process.wait()
                await asyncio.gather(stdout_task, stderr_task)
                raise
            stdout, stderr = await asyncio.gather(stdout_task, stderr_task)
            return ProcessResult(process.returncode or 0, stdout, stderr)

    async def _read_limited(self, stream: asyncio.StreamReader | None, *, retain_tail: bool = False) -> str:
        if stream is None:
            return ""
        retained = bytearray()
        while chunk := await stream.read(8192):
            if retain_tail:
                retained.extend(chunk)
                del retained[:-self._settings.max_process_output_bytes]
            else:
                remaining = self._settings.max_process_output_bytes - len(retained)
                if remaining > 0:
                    retained.extend(chunk[:remaining])
        return retained.decode("utf-8", errors="replace")

    async def download(
        self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str,
        ceiling: str | None = None,
    ) -> list[Path]:
        return await self._download(url, quality, output, temporary, media_user_token, lyrics_only=False,
            ceiling=ceiling)

    async def download_lyrics(
        self, url: str, output: Path, temporary: Path, *, media_user_token: str
    ) -> list[Path]:
        return await self._download(url, "aac-he", output, temporary, media_user_token, lyrics_only=True)

    async def _download(
        self,
        url: str,
        quality: str,
        output: Path,
        temporary: Path,
        media_user_token: str,
        lyrics_only: bool,
        ceiling: str | None = None,
    ) -> list[Path]:
        # Revalidate before serializing the value into a Netscape cookie line.
        if not valid_media_token(media_user_token):
            raise ValueError("account_context_required")
        output.mkdir(parents=True, exist_ok=False, mode=0o750)
        temporary.mkdir(parents=True, exist_ok=False, mode=0o750)
        cookie_fd, cookie_name = tempfile.mkstemp(prefix="account-", suffix=".cookies", dir=temporary)
        cookie_path = Path(cookie_name)
        argv = [
            sys.executable, "-m", "apple_gateway.account_cli",
            "--no-config-file",
            "--config-path", str(temporary / "unused-config.ini"),
            "--no-exceptions",
            "--use-wrapper",
            "--wrapper-url", self._settings.wrapper_url,
            "--wrapper-decrypt-host", self._settings.wrapper_decrypt_host,
            "--wrapper-decrypt-port", str(self._settings.wrapper_decrypt_port),
            "--output-path", str(output),
            "--temp-path", str(temporary),
            "--song-codec-priority", quality,
            "--artist-auto-select", "all-albums",
            "--cookies-path", str(cookie_path),
            "--allstarr-web-token", str(self._settings.data_root / "web-token"),
        ]
        if ceiling:
            argv += ["--allstarr-alac-ceiling", ceiling]
        if lyrics_only:
            argv.append("--synced-lyrics-only")
        argv.append(url)
        try:
            with os.fdopen(cookie_fd, "w", encoding="utf-8") as cookie:
                os.fchmod(cookie.fileno(), 0o600)
                cookie.write("# Netscape HTTP Cookie File\n")
                cookie.write(f".music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\t{media_user_token}\n")
            started = time.monotonic()
            result = await self.execute(argv, temporary)
            elapsed_ms = (time.monotonic() - started) * 1000
        finally:
            cookie_path.unlink(missing_ok=True)
        if result.return_code == 78:
            raise ProcessFailure("account_mismatch", stderr=result.stderr)
        if result.return_code == 79:
            raise ProcessFailure("account_unavailable", stderr=result.stderr)
        if result.return_code != 0:
            raise ProcessFailure("gamdl_failed", stderr=result.stderr)
        _log_preparation(result.stdout, quality, elapsed_ms)
        artifacts = safe_files(output, {".lrc"} if lyrics_only else {
            ".m4a", ".flac", ".mp4", ".m4v", ".lrc", ".srt", ".ttml", ".jpg", ".png"
        })
        if not artifacts:
            raise ProcessFailure("artifact_missing", stderr=result.stderr)
        if not lyrics_only and not any(path.suffix.lower() in {".m4a", ".flac"} for path in artifacts):
            raise ProcessFailure("audio_artifact_missing", stderr=result.stderr)
        return artifacts

    async def to_flac(self, source: Path, target: Path) -> Path:
        if source.suffix.lower() == ".flac":
            return source
        result = await self.execute([
            self._settings.ffmpeg_path, "-nostdin", "-v", "error", "-y",
            "-i", str(source), "-map", "0:a:0", "-map_metadata", "-1",
            "-c:a", "flac", "-compression_level", "0", str(target),
        ], target.parent)
        if result.return_code != 0 or not target.is_file():
            raise ProcessFailure("transcode_failed")
        return target.resolve()

    async def _probe(self, source: Path) -> dict[str, str]:
        # One ffprobe answers both the codec and the quality-limit questions; each run costs about 50 ms.
        if facts := self._probed.pop(str(source), None):
            return facts
        result = await self.execute([
            str(Path(self._settings.ffmpeg_path).with_name("ffprobe")),
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,bits_per_raw_sample",
            "-of", "default=noprint_wrappers=1", str(source),
        ], source.parent)
        if result.return_code != 0:
            raise ProcessFailure("audio_artifact_missing")
        return dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)

    async def audio_codec(self, source: Path) -> str:
        if source.suffix.lower() == ".flac":
            return "flac"
        facts = await self._probe(source)
        codec = facts.get("codec_name", "").strip()
        if codec not in {"aac", "alac", "flac"}:
            raise ProcessFailure("audio_artifact_missing")
        if codec != "aac":
            while len(self._probed) >= 16:
                self._probed.pop(next(iter(self._probed)))
            self._probed[str(source)] = facts
        return codec

    async def exceeds_ceiling(self, source: Path, ceiling: AudioCeiling) -> bool:
        facts = await self._probe(source)
        depth = int(facts["bits_per_raw_sample"]) if facts.get("bits_per_raw_sample", "").isdigit() else 0
        rate = int(facts["sample_rate"]) if facts.get("sample_rate", "").isdigit() else 0
        return depth > ceiling.bit_depth or rate > ceiling.sample_rate

    async def seekable_flac(self, source: Path, target: Path, ceiling: AudioCeiling | None = None) -> Path:
        reduce = [] if ceiling is None else [
            "-ar", str(ceiling.sample_rate), "-sample_fmt", "s16" if ceiling.bit_depth <= 16 else "s32",
        ]
        result = await self.execute([
            self._settings.ffmpeg_path, "-nostdin", "-v", "error", "-y",
            "-i", str(source), "-map", "0:a:0", "-map_metadata", "-1", *reduce,
            "-c:a", "flac", "-compression_level", "0", "-f", "flac", str(target),
        ], target.parent)
        if result.return_code != 0 or not target.is_file():
            raise ProcessFailure("transcode_failed")
        result = await self.execute([
            "metaflac", "--add-seekpoint=0", "--add-seekpoint=10s", str(target),
        ], target.parent)
        if result.return_code != 0:
            raise ProcessFailure("transcode_failed")
        return target.resolve()

    async def stream_flac(self, source: Path, chunk_size: int = 64 * 1024) -> AsyncIterator[bytes]:
        """Yield the final FLAC bytes without materializing a second complete file."""
        if source.suffix.lower() == ".flac":
            with source.open("rb") as artifact:
                while chunk := await asyncio.to_thread(artifact.read, chunk_size):
                    yield chunk
            return

        async with self._semaphore:
            process = await asyncio.create_subprocess_exec(
                self._settings.ffmpeg_path,
                "-nostdin", "-v", "error",
                "-i", str(source),
                "-map", "0:a:0", "-map_metadata", "-1",
                "-c:a", "flac", "-compression_level", "0",
                "-f", "flac", "pipe:1",
                cwd=source.parent,
                stdin=asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                start_new_session=True,
            )
            stderr_task = asyncio.create_task(self._read_limited(process.stderr, retain_tail=True))
            deadline = asyncio.get_running_loop().time() + self._settings.subprocess_timeout_seconds
            try:
                if process.stdout is None:
                    raise ProcessFailure("transcode_failed")
                while True:
                    remaining = deadline - asyncio.get_running_loop().time()
                    if remaining <= 0:
                        raise ProcessFailure("process_timeout")
                    try:
                        chunk = await asyncio.wait_for(process.stdout.read(chunk_size), timeout=remaining)
                    except (TimeoutError, asyncio.TimeoutError) as exc:
                        raise ProcessFailure("process_timeout") from exc
                    if not chunk:
                        break
                    yield chunk

                remaining = deadline - asyncio.get_running_loop().time()
                if remaining <= 0:
                    raise ProcessFailure("process_timeout")
                try:
                    return_code = await asyncio.wait_for(process.wait(), timeout=remaining)
                except (TimeoutError, asyncio.TimeoutError) as exc:
                    raise ProcessFailure("process_timeout") from exc
                await stderr_task
                if return_code != 0:
                    raise ProcessFailure("transcode_failed")
            finally:
                if process.returncode is None:
                    os.killpg(process.pid, signal.SIGKILL)
                    await process.wait()
                if not stderr_task.done():
                    stderr_task.cancel()
                await asyncio.gather(stderr_task, return_exceptions=True)

    async def audio_duration(self, source: Path) -> float | None:
        try:
            result = await self.execute([
                str(Path(self._settings.ffmpeg_path).with_name("ffprobe")),
                "-v", "error", "-select_streams", "a:0",
                "-show_entries", "stream=duration:format=duration",
                "-of", "json", str(source),
            ], source.parent)
            payload = json.loads(result.stdout) if result.return_code == 0 else {}
            stream = (payload.get("streams") or [{}])[0]
            duration = float(stream.get("duration") or payload.get("format", {}).get("duration"))
            return duration if math.isfinite(duration) and duration > 0 else None
        except (FileNotFoundError, ValueError, TypeError, ProcessFailure, KeyError):
            return None
