from __future__ import annotations

import asyncio
import logging
import time
import uuid
from pathlib import Path

from fastapi.responses import StreamingResponse

from .runner import ProcessFailure

logger = logging.getLogger("uvicorn.error")


class SharedAudio:
    def __init__(self, open_audio, target, *, completed, song_id, quality, finalize):
        self._open_audio, self._target, self._completed = open_audio, target, completed
        self._finalize = finalize
        self._song_id, self._quality = song_id, quality
        self._changed = asyncio.Condition()
        self.ready = asyncio.get_running_loop().create_future()
        self.path: Path | None = None
        self._source: Path | None = None
        self.available = 0
        self.finished = False
        self.error = None
        self.waiters = 0
        self.first_byte_ms = 0
        self.task = asyncio.create_task(self._produce())

    async def _produce(self):
        partial = None
        finalized = None
        started = time.monotonic()
        try:
            async with self._open_audio() as (header, chunks):
                target = self._target(header)
                target.parent.mkdir(parents=True, exist_ok=True, mode=0o750)
                partial = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
                self.path = partial
                self._source = partial
                with partial.open("xb") as output:
                    async for chunk in chunks:
                        if not chunk:
                            continue
                        output.write(chunk)
                        output.flush()
                        async with self._changed:
                            self.available += len(chunk)
                            if not self.ready.done():
                                self.first_byte_ms = round((time.monotonic() - started) * 1000)
                                self.ready.set_result(header)
                            self._changed.notify_all()
                if not self.available:
                    raise ProcessFailure("audio_artifact_missing")
            finalized = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
            try:
                await self._finalize(partial, finalized)
            except Exception as exc:
                logger.warning("Audio cache finalization song=%s quality=%s failure=%s; retaining validated output",
                    self._song_id, self._quality, type(exc).__name__)
                finalized.unlink(missing_ok=True)
                finalized = None
            self._completed(header)
            if finalized is None:
                partial.replace(target)
                self._source = target
            else:
                finalized.replace(target)
            self.path = target
            logger.info("Audio progressive song=%s quality=%s first_byte_ms=%d total_ms=%d bytes=%d",
                self._song_id, self._quality, self.first_byte_ms, round((time.monotonic() - started) * 1000), self.available)
        except BaseException as exc:
            if isinstance(exc, OSError):
                exc = ProcessFailure("cache_write_failed")
            self.error = exc
            if not self.ready.done():
                if isinstance(exc, asyncio.CancelledError):
                    self.ready.cancel()
                else:
                    self.ready.set_exception(exc)
            if not isinstance(exc, asyncio.CancelledError):
                code = exc.code if isinstance(exc, ProcessFailure) else "download_failed"
                logger.warning("Audio progressive song=%s quality=%s bytes=%d failure=%s",
                    self._song_id, self._quality, self.available, code)
        finally:
            if partial is not None and self.error is not None:
                partial.unlink(missing_ok=True)
            if finalized is not None:
                finalized.unlink(missing_ok=True)
            async with self._changed:
                self.finished = True
                self._changed.notify_all()

    def discard(self):
        if self._source is not None and self._source != self.path:
            self._source.unlink(missing_ok=True)

    async def read(self, release):
        try:
            await asyncio.shield(self.ready)
            if self.finished and self.error is not None:
                raise self.error
            with self._source.open("rb") as source:
                position = 0
                while True:
                    async with self._changed:
                        await self._changed.wait_for(lambda: self.available > position or self.finished)
                        size = min(64 * 1024, self.available - position)
                        finished, error = self.finished, self.error
                    if size:
                        data = source.read(size)
                        if len(data) != size:
                            raise ProcessFailure("audio_artifact_missing")
                        position += len(data)
                        yield data
                    elif finished:
                        if error is not None:
                            raise error
                        return
        finally:
            await release()


class AudioResponse(StreamingResponse):
    def __init__(self, *args, release, **kwargs):
        super().__init__(*args, **kwargs)
        self._release = release

    async def __call__(self, scope, receive, send):
        try:
            await super().__call__(scope, receive, send)
        finally:
            try:
                await self.body_iterator.aclose()
            finally:
                await self._release()
