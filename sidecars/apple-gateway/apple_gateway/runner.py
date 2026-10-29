from __future__ import annotations

import asyncio
import json
import logging
import math
import os
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


@dataclass(frozen=True, slots=True)
class ProcessResult:
    return_code: int
    stdout: str
    stderr: str


class ProcessFailure(RuntimeError):
    def __init__(self, code: str, *, stderr: str = ""):
        super().__init__(code)
        self.code = code
        self.stderr = stderr
        self.quality: str | None = None


class BoundedProcessRunner:
    def __init__(self, settings: Settings):
        self._settings = settings
        self._semaphore = asyncio.Semaphore(settings.max_concurrency)

    async def execute(self, argv: list[str], cwd: Path) -> ProcessResult:
        async with self._semaphore:
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
        self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str
    ) -> list[Path]:
        return await self._download(url, quality, output, temporary, media_user_token, lyrics_only=False)

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
        ]
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

    async def audio_codec(self, source: Path) -> str:
        if source.suffix.lower() == ".flac":
            return "flac"
        result = await self.execute([
            str(Path(self._settings.ffmpeg_path).with_name("ffprobe")),
            "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name",
            "-of", "default=noprint_wrappers=1:nokey=1", str(source),
        ], source.parent)
        codec = result.stdout.strip()
        if result.return_code != 0 or codec not in {"aac", "alac", "flac"}:
            raise ProcessFailure("audio_artifact_missing")
        return codec

    async def seekable_flac(self, source: Path, target: Path) -> Path:
        result = await self.execute([
            self._settings.ffmpeg_path, "-nostdin", "-v", "error", "-y",
            "-i", str(source), "-map", "0:a:0", "-map_metadata", "-1",
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
