from __future__ import annotations

import asyncio
import os
import signal
from dataclasses import dataclass
from pathlib import Path
from typing import AsyncIterator

from .config import Settings
from .security import safe_files


@dataclass(frozen=True, slots=True)
class ProcessResult:
    return_code: int
    stdout: str
    stderr: str


class ProcessFailure(RuntimeError):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


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
            stderr_task = asyncio.create_task(self._read_limited(process.stderr))
            try:
                await asyncio.wait_for(process.wait(), timeout=self._settings.subprocess_timeout_seconds)
            except (TimeoutError, asyncio.TimeoutError) as exc:
                os.killpg(process.pid, signal.SIGKILL)
                await process.wait()
                await asyncio.gather(stdout_task, stderr_task)
                raise ProcessFailure("process_timeout") from exc
            stdout, stderr = await asyncio.gather(stdout_task, stderr_task)
            return ProcessResult(process.returncode or 0, stdout, stderr)

    async def _read_limited(self, stream: asyncio.StreamReader | None) -> str:
        if stream is None:
            return ""
        retained = bytearray()
        while chunk := await stream.read(8192):
            remaining = self._settings.max_process_output_bytes - len(retained)
            if remaining > 0:
                retained.extend(chunk[:remaining])
        return retained.decode("utf-8", errors="replace")

    async def download(self, url: str, quality: str, output: Path, temporary: Path) -> list[Path]:
        output.mkdir(parents=True, exist_ok=False, mode=0o750)
        temporary.mkdir(parents=True, exist_ok=False, mode=0o750)
        argv = [
            self._settings.gamdl_path,
            "--no-config-file",
            "--no-exceptions",
            "--use-wrapper",
            "--wrapper-url", self._settings.wrapper_url,
            "--wrapper-decrypt-host", self._settings.wrapper_decrypt_host,
            "--wrapper-decrypt-port", str(self._settings.wrapper_decrypt_port),
            "--output-path", str(output),
            "--temp-path", str(temporary),
            "--song-codec-priority", quality,
            "--artist-auto-select", "all-albums",
        ]
        if self._settings.cookies_path:
            argv.extend(["--cookies-path", str(self._settings.cookies_path)])
        argv.append(url)
        result = await self.execute(argv, temporary)
        if result.return_code != 0:
            raise ProcessFailure("gamdl_failed")
        artifacts = safe_files(output, {".m4a", ".flac", ".mp4", ".m4v", ".lrc", ".srt", ".ttml", ".jpg", ".png"})
        if not artifacts:
            raise ProcessFailure("artifact_missing")
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
            stderr_task = asyncio.create_task(self._read_limited(process.stderr))
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
