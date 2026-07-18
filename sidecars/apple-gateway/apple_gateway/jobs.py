from __future__ import annotations

import asyncio
import json
import os
import re
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .models import DownloadJobView
from .runner import BoundedProcessRunner, ProcessFailure
from .security import safe_apple_url


_JOB_ID = re.compile(r"^[0-9a-f]{32}$")
_TERMINAL_STATES = frozenset({"succeeded", "failed"})
_STATE_VERSION = 1


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

    def start(self) -> None:
        jobs_root = self._data_root / "jobs"
        jobs_root.mkdir(parents=True, exist_ok=True, mode=0o750)
        for root in jobs_root.iterdir():
            if root.is_symlink() or not root.is_dir() or not _JOB_ID.fullmatch(root.name):
                continue
            job = self._load_terminal(root)
            if job is not None:
                self._jobs[job.id] = job

    def enqueue(self, raw_url: str, quality: str) -> DownloadJobView:
        url, media_kind = safe_apple_url(raw_url)
        job_id = uuid.uuid4().hex
        job = JobState(job_id, media_kind)
        self._jobs[job_id] = job
        self._persist(job)
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
        self._persist(job)
        root = self._data_root / "jobs" / job.id
        try:
            job.artifacts = await self._runner.download(url, quality, root / "artifacts", root / "temporary")
            job.state = "succeeded"
        except asyncio.CancelledError:
            job.state = "failed"
            job.error_code = "gateway_stopping"
            self._persist(job)
            raise
        except ProcessFailure as exc:
            job.state = "failed"
            job.error_code = exc.code
        except Exception:
            job.state = "failed"
            job.error_code = "download_failed"
        self._persist(job)

    def _persist(self, job: JobState) -> None:
        root = self._data_root / "jobs" / job.id
        root.mkdir(parents=True, exist_ok=True, mode=0o750)
        payload = {
            "version": _STATE_VERSION,
            "id": job.id,
            "media_kind": job.media_kind,
            "state": job.state,
            "artifacts": [self._relative_artifact(root, artifact) for artifact in job.artifacts],
            "error_code": job.error_code,
        }
        target = root / "job.json"
        partial = root / "job.json.partial"
        with partial.open("w", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=True, separators=(",", ":"), sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        partial.replace(target)
        try:
            directory = os.open(root, os.O_RDONLY)
        except OSError:
            return
        try:
            os.fsync(directory)
        finally:
            os.close(directory)

    def _load_terminal(self, root: Path) -> JobState | None:
        state_file = root / "job.json"
        if state_file.is_symlink() or not state_file.is_file():
            return None
        try:
            payload: Any = json.loads(state_file.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or set(payload) != {
                "version", "id", "media_kind", "state", "artifacts", "error_code"
            }:
                return None
            if payload["version"] != _STATE_VERSION or payload["id"] != root.name:
                return None
            media_kind = payload["media_kind"]
            state = payload["state"]
            error_code = payload["error_code"]
            artifacts = payload["artifacts"]
            if not isinstance(media_kind, str) or not media_kind or state not in _TERMINAL_STATES:
                return None
            if error_code is not None and not isinstance(error_code, str):
                return None
            if not isinstance(artifacts, list) or not all(isinstance(item, str) for item in artifacts):
                return None
            resolved_artifacts = [self._resolve_artifact(root, item) for item in artifacts]
        except (OSError, UnicodeError, json.JSONDecodeError, ValueError):
            return None
        return JobState(root.name, media_kind, state, resolved_artifacts, error_code)

    @staticmethod
    def _relative_artifact(root: Path, artifact: Path) -> str:
        resolved_root = root.resolve()
        resolved = artifact.resolve()
        try:
            return resolved.relative_to(resolved_root).as_posix()
        except ValueError as exc:
            raise ValueError("job artifact escaped its job directory") from exc

    @staticmethod
    def _resolve_artifact(root: Path, relative: str) -> Path:
        path = Path(relative)
        if not relative or path.is_absolute():
            raise ValueError("invalid persisted artifact path")
        resolved_root = root.resolve()
        resolved = (root / path).resolve()
        try:
            resolved.relative_to(resolved_root)
        except ValueError as exc:
            raise ValueError("persisted artifact escaped its job directory") from exc
        return resolved
