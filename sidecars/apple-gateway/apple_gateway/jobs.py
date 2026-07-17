from __future__ import annotations

import asyncio
import uuid
from dataclasses import dataclass, field
from pathlib import Path

from .models import DownloadJobView
from .runner import BoundedProcessRunner, ProcessFailure
from .security import safe_apple_url


@dataclass(slots=True)
class JobState:
    id: str
    media_kind: str
    state: str = "queued"
    artifacts: list[Path] = field(default_factory=list)
    error_code: str | None = None

    def view(self) -> DownloadJobView:
        return DownloadJobView(
            id=self.id,
            state=self.state,
            media_kind=self.media_kind,
            artifact_count=len(self.artifacts),
            error_code=self.error_code,
        )


class DownloadJobManager:
    def __init__(self, runner: BoundedProcessRunner, data_root: Path):
        self._runner = runner
        self._data_root = data_root
        self._jobs: dict[str, JobState] = {}
        self._tasks: set[asyncio.Task[None]] = set()

    def enqueue(self, raw_url: str, quality: str) -> DownloadJobView:
        url, media_kind = safe_apple_url(raw_url)
        job_id = uuid.uuid4().hex
        job = JobState(job_id, media_kind)
        self._jobs[job_id] = job
        task = asyncio.create_task(self._run(job, url, quality))
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)
        return job.view()

    def get(self, job_id: str) -> DownloadJobView | None:
        job = self._jobs.get(job_id)
        return job.view() if job else None

    async def close(self) -> None:
        if not self._tasks:
            return
        for task in self._tasks:
            task.cancel()
        await asyncio.gather(*self._tasks, return_exceptions=True)

    async def _run(self, job: JobState, url: str, quality: str) -> None:
        job.state = "running"
        root = self._data_root / "jobs" / job.id
        try:
            job.artifacts = await self._runner.download(url, quality, root / "artifacts", root / "temporary")
            job.state = "succeeded"
        except asyncio.CancelledError:
            job.state = "failed"
            job.error_code = "gateway_stopping"
            raise
        except ProcessFailure as exc:
            job.state = "failed"
            job.error_code = exc.code
        except Exception:
            job.state = "failed"
            job.error_code = "download_failed"
