from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import os
import signal
import sys
from contextlib import asynccontextmanager

from .runner import ProcessFailure


class _Worker:
    def __init__(self, process, stderr):
        self.process = process
        self.stderr = stderr
        self.account = None
        self.busy = False

    async def stop(self):
        with contextlib.suppress(ProcessLookupError):
            os.killpg(self.process.pid, signal.SIGKILL)
        await asyncio.gather(self.process.wait(), self.process.stdout.read(), self.stderr)

    async def frame(self, deadline):
        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0:
            raise ProcessFailure("process_timeout")

        async def read():
            prefix = await self.process.stdout.readexactly(5)
            length = int.from_bytes(prefix[1:], "big")
            if length > 4 * 1024 * 1024:
                raise ProcessFailure("worker_failed")
            return prefix[:1], await self.process.stdout.readexactly(length)

        try:
            return await asyncio.wait_for(read(), remaining)
        except asyncio.TimeoutError:
            raise ProcessFailure("process_timeout") from None
        except (asyncio.IncompleteReadError, BrokenPipeError, ConnectionResetError):
            raise ProcessFailure("worker_failed") from None


class WorkerPool:
    def __init__(self, settings, *, command=None, semaphore=None):
        self._settings = settings
        self._command = command or [sys.executable, "-m", "apple_gateway.worker"]
        self._semaphore = semaphore or asyncio.Semaphore(settings.max_concurrency)
        self._lock = asyncio.Lock()
        self._workers = []
        self._closed = False

    async def _diagnostics(self, stream):
        retained = bytearray()
        while chunk := await stream.read(8192):
            retained.extend(chunk)
            del retained[:-self._settings.max_process_output_bytes]
        return retained.decode("utf-8", errors="replace")

    async def _start(self):
        settings = self._settings
        environment = {**os.environ, "APPLE_GATEWAY_DATA_ROOT": str(settings.data_root),
            "APPLE_GATEWAY_WRAPPER_URL": settings.wrapper_url,
            "APPLE_GATEWAY_WRAPPER_DECRYPT_HOST": settings.wrapper_decrypt_host,
            "APPLE_GATEWAY_WRAPPER_DECRYPT_PORT": str(settings.wrapper_decrypt_port),
            "APPLE_GATEWAY_FFMPEG_PATH": settings.ffmpeg_path,
            "APPLE_GATEWAY_WRAPPER_TIMEOUT_SECONDS": str(settings.wrapper_timeout_seconds),
            "APPLE_GATEWAY_PROCESS_TIMEOUT_SECONDS": str(max(10, settings.subprocess_timeout_seconds))}
        process = await asyncio.create_subprocess_exec(*self._command,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            start_new_session=True, env=environment)
        worker = _Worker(process, asyncio.create_task(self._diagnostics(process.stderr)))
        self._workers.append(worker)
        return worker

    async def warm_up(self):
        async with self._lock:
            if not self._closed and not self._workers:
                await self._start()

    @asynccontextmanager
    async def open(self, request):
        async with self._semaphore:
            account = hashlib.sha256(f"{request['token']}\n{request['storefront']}".encode()).hexdigest()
            async with self._lock:
                if self._closed:
                    raise ProcessFailure("worker_unavailable")
                for dead in list(self._workers):
                    if not dead.busy and dead.process.returncode is not None:
                        await dead.stop()
                        self._workers.remove(dead)
                available = [item for item in self._workers if not item.busy]
                worker = next((item for item in available if item.account == account), None)
                worker = worker or (available[0] if available else await self._start())
                worker.busy, worker.account = True, account
            complete = False
            deadline = asyncio.get_running_loop().time() + self._settings.subprocess_timeout_seconds

            async def receive():
                kind, payload = await worker.frame(deadline)
                if kind == b"F":
                    try:
                        code = json.loads(payload)["code"]
                    except (ValueError, KeyError, TypeError):
                        code = "worker_failed"
                    allowed = {"account_mismatch", "account_unavailable", "audio_artifact_missing", "manifest_invalid",
                        "invalid_audio_fragment", "incompatible_audio_fragments", "segment_download_failed",
                        "decrypt_failed", "transcode_failed", "tagging_failed", "download_failed"}
                    raise ProcessFailure(code if code in allowed else "worker_failed")
                return kind, payload

            async def chunks():
                nonlocal complete
                while True:
                    kind, payload = await receive()
                    if kind == b"E":
                        complete = True
                        return
                    if kind != b"D" or not payload:
                        raise ProcessFailure("worker_failed")
                    yield payload

            try:
                worker.process.stdin.write((json.dumps(request) + "\n").encode())
                await asyncio.wait_for(worker.process.stdin.drain(), max(0.001, deadline - asyncio.get_running_loop().time()))
                kind, payload = await receive()
                if kind != b"H":
                    raise ProcessFailure("worker_failed")
                try:
                    header = json.loads(payload)
                except ValueError:
                    raise ProcessFailure("worker_failed") from None
                yield header, chunks()
            except asyncio.TimeoutError:
                raise ProcessFailure("process_timeout") from None
            except (BrokenPipeError, ConnectionResetError):
                raise ProcessFailure("worker_failed") from None
            finally:
                if not complete:
                    await worker.stop()
                    async with self._lock:
                        if worker in self._workers:
                            self._workers.remove(worker)
                worker.busy = False

    async def close(self):
        async with self._lock:
            self._closed = True
            for worker in self._workers:
                await worker.stop()
            self._workers.clear()
