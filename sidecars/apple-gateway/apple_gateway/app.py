from __future__ import annotations

import asyncio
import hashlib
import importlib.metadata
import shutil
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, AsyncIterator, Awaitable, Callable
from dataclasses import dataclass

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse, Response, StreamingResponse
from starlette.background import BackgroundTask

from .config import Settings
from .models import Login2faRequest, LoginRequest, MediaAccount
from .runner import BoundedProcessRunner, ProcessFailure
from .security import media_account, song_url
from .wrapper import WrapperClient, WrapperResponse

API_VERSION = "2.0.0"
CAPABILITIES = (
    "stream-audio-song",
    "download-audio-song",
    "synced-lyrics-artifact",
    "codec-alac",
    "codec-aac",
)
PREPARED_CACHE_TTL_SECONDS = 6 * 60 * 60
PREPARED_CACHE_MAX_TRACKS = 32
FLAC_GUIDANCE_PREFIX = b"ID3\x04\x00\x00\x00\x00\x00\x00"


def _version(distribution: str) -> str:
    try:
        return importlib.metadata.version(distribution)
    except importlib.metadata.PackageNotFoundError:
        return "unavailable"


def _authenticated(payload: Any) -> bool:
    if not isinstance(payload, dict):
        return False
    if payload.get("logged_in") is True or payload.get("authenticated") is True:
        return True
    auth = payload.get("auth")
    return isinstance(auth, dict) and (
        auth.get("logged_in") is True
        or str(auth.get("state", "")).lower() in {"authenticated", "logged_in", "ready"}
    )


def _codec(quality: str) -> str:
    mapping = {
        "alac": "alac",
        "alac-16-44": "alac",
        "alac-24-96": "alac",
        "alac-24-192": "alac",
        "aac": "aac",
        "aac-320": "aac",
        "aac-web": "aac-web",
        "aac-96": "aac-he",
        "aac-he": "aac-he",
        "aac-he-web": "aac-he-web",
    }
    try:
        return mapping[quality.lower()]
    except KeyError as exc:
        raise HTTPException(status_code=400, detail="unsupported_quality") from exc


def _forward(response: WrapperResponse) -> JSONResponse:
    return JSONResponse(status_code=response.status_code, content=response.payload)


def _account(request: Request) -> MediaAccount:
    names = ("Music-User-Token", "X-Apple-Storefront", "X-Allstarr-Account-Context")
    values = [request.headers.getlist(name) for name in names]
    if any(len(value) != 1 for value in values):
        raise HTTPException(status_code=401, detail="account_context_required")
    try:
        return media_account(*(value[0] for value in values))
    except ValueError:
        raise HTTPException(status_code=401, detail="account_context_required") from None


def _song_request(account: MediaAccount, song_id: str, quality: str) -> str:
    try:
        url = song_url(account.storefront, song_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid_song_id") from None
    _codec(quality)
    return url


def _cache_key(account: MediaAccount, song_id: str, quality: str) -> str:
    return hashlib.sha256(f"{account.scope}\n{account.storefront}\n{song_id}\n{quality.lower()}".encode()).hexdigest()


@dataclass(slots=True)
class _Preparation:
    task: asyncio.Task[Path]
    waiters: int = 0


def create_app(
    settings: Settings | None = None,
    wrapper: WrapperClient | None = None,
    runner: BoundedProcessRunner | None = None,
) -> FastAPI:
    config = settings or Settings.from_env()
    wrapper_client = wrapper or WrapperClient(config.wrapper_url, config.wrapper_timeout_seconds)
    process_runner = runner or BoundedProcessRunner(config)
    preparations: dict[str, _Preparation] = {}
    preparation_lock = asyncio.Lock()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        config.prepare()
        yield
        async with preparation_lock:
            tasks = [entry.task for entry in preparations.values()]
            for task in tasks:
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await wrapper_client.close()

    application = FastAPI(
        title="Allstarr Apple download gateway",
        version=API_VERSION,
        docs_url=None,
        redoc_url=None,
        lifespan=lifespan,
    )

    @application.get("/api/capabilities")
    async def capabilities() -> dict[str, Any]:
        return {
            "sidecarApiVersion": API_VERSION,
            "runtime": {"gateway": API_VERSION, "gamdl": _version("gamdl")},
            "capabilities": [{"id": item, "state": "supported"} for item in CAPABILITIES],
        }

    @application.get("/api/health")
    async def health() -> dict[str, Any]:
        wrapper_health = await wrapper_client.health()
        me = await wrapper_client.me() if wrapper_health.status_code == 200 else WrapperResponse(503, {})
        wrapper_payload = wrapper_health.payload if isinstance(wrapper_health.payload, dict) else {}
        wrapper_version = str(wrapper_payload.get("version") or "unknown")
        executable = shutil.which(config.gamdl_path) is not None
        return {
            "status": "ok" if executable and wrapper_health.status_code == 200 else "degraded",
            "staged": executable,
            "daemon_running": wrapper_health.status_code == 200,
            "wrapper_healthy": wrapper_health.status_code == 200,
            "logged_in": _authenticated(me.payload),
            "versions": {"gateway": API_VERSION, "gamdl": _version("gamdl"), "wrapper": wrapper_version},
        }

    @application.get("/api/me")
    async def me() -> JSONResponse:
        return _forward(await wrapper_client.me())

    @application.post("/api/login")
    async def login(request: LoginRequest) -> JSONResponse:
        return _forward(await wrapper_client.login(request.username, request.password))

    @application.post("/api/login/2fa")
    async def login_2fa(request: Login2faRequest) -> JSONResponse:
        return _forward(await wrapper_client.login_2fa(request.code))

    async def coalesce(key: str, prepare: Callable[[], Awaitable[Path]]) -> Path:
        async with preparation_lock:
            entry = preparations.get(key)
            if entry is None:
                entry = _Preparation(asyncio.create_task(prepare()))
                preparations[key] = entry
            entry.waiters += 1
        try:
            return await asyncio.shield(entry.task)
        finally:
            canceled = None
            async with preparation_lock:
                entry.waiters -= 1
                if entry.waiters == 0:
                    preparations.pop(key, None)
                    if not entry.task.done():
                        entry.task.cancel()
                        canceled = entry.task
            if canceled is not None:
                await asyncio.gather(canceled, return_exceptions=True)

    def lyrics_path(key: str) -> Path:
        return config.data_root / "lyrics" / f"{key}.lrc"

    def cache_lyrics(artifact: Path, key: str) -> Path:
        target = lyrics_path(key)
        target.parent.mkdir(parents=True, exist_ok=True, mode=0o750)
        partial = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
        try:
            shutil.copyfile(artifact, partial)
            partial.replace(target)
        finally:
            partial.unlink(missing_ok=True)
        return target

    async def download_song_source(
        account: MediaAccount,
        song_id: str,
        quality: str,
        fallback_quality: str | None,
    ) -> tuple[Path, Path]:
        url = _song_request(account, song_id, quality)
        root = config.data_root / "artifacts" / uuid.uuid4().hex
        try:
            try:
                artifacts = await process_runner.download(
                    url, _codec(quality), root / "output", root / "temporary", media_user_token=account.token
                )
            except ProcessFailure as exc:
                if fallback_quality is None or exc.code not in {"artifact_missing", "gamdl_failed"}:
                    raise
                artifacts = await process_runner.download(
                    url, _codec(fallback_quality), root / "output-fallback", root / "temporary-fallback",
                    media_user_token=account.token,
                )
            lyrics = [artifact for artifact in artifacts if artifact.suffix.lower() == ".lrc"]
            if lyrics:
                cache_lyrics(lyrics[0], _cache_key(account, song_id, quality))
            audio = [artifact for artifact in artifacts if artifact.suffix.lower() in {".m4a", ".flac"}]
            if not audio:
                raise ProcessFailure("audio_artifact_missing")
            return root, audio[0]
        except asyncio.CancelledError:
            shutil.rmtree(root, ignore_errors=True)
            raise
        except ProcessFailure as exc:
            shutil.rmtree(root, ignore_errors=True)
            status = 504 if exc.code == "process_timeout" else 403 if exc.code == "account_mismatch" else 502
            raise HTTPException(status_code=status, detail=exc.code) from None
        except Exception:
            shutil.rmtree(root, ignore_errors=True)
            raise HTTPException(status_code=502, detail="download_failed") from None

    def cached_source(key: str) -> Path | None:
        cache_root = config.data_root / "prepared"
        now = time.time()
        candidates = sorted(
            (path for path in cache_root.glob("*")
             if not path.is_symlink() and path.is_file() and not path.name.endswith(".partial")),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        ) if cache_root.exists() else []
        for stale in candidates[PREPARED_CACHE_MAX_TRACKS:]:
            stale.unlink(missing_ok=True)
        for candidate in candidates[:PREPARED_CACHE_MAX_TRACKS]:
            if now - candidate.stat().st_mtime > PREPARED_CACHE_TTL_SECONDS:
                candidate.unlink(missing_ok=True)
            elif candidate.stem == key:
                candidate.touch()
                return candidate
        return None

    async def prepare_song(
        account: MediaAccount, song_id: str, quality: str, fallback_quality: str | None = None
    ) -> Path:
        key = _cache_key(account, song_id, quality)
        if cached := cached_source(key):
            return cached

        async def prepare_and_cache() -> Path:
            if cached := cached_source(key):
                return cached
            root, source = await download_song_source(account, song_id, quality, fallback_quality)
            try:
                cache_root = config.data_root / "prepared"
                cache_root.mkdir(parents=True, exist_ok=True, mode=0o750)
                target = cache_root / f"{key}{source.suffix.lower()}"
                partial = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
                try:
                    shutil.copyfile(source, partial)
                    partial.replace(target)
                finally:
                    partial.unlink(missing_ok=True)
                target.touch()
                return target
            finally:
                shutil.rmtree(root, ignore_errors=True)

        return await coalesce("audio:" + key, prepare_and_cache)

    @application.get("/api/download/{song_id}")
    async def download_song(
        song_id: str, quality: str = "alac-16-44", account: MediaAccount = Depends(_account)
    ) -> FileResponse:
        _song_request(account, song_id, quality)
        source = await prepare_song(account, song_id, quality)
        root = config.data_root / "artifacts" / uuid.uuid4().hex
        root.mkdir(parents=True, exist_ok=False, mode=0o750)
        try:
            artifact = await process_runner.to_flac(source, root / f"{song_id}.flac")
        except asyncio.CancelledError:
            shutil.rmtree(root, ignore_errors=True)
            raise
        except ProcessFailure as exc:
            shutil.rmtree(root, ignore_errors=True)
            status = 504 if exc.code == "process_timeout" else 502
            raise HTTPException(status_code=status, detail=exc.code) from None
        except Exception:
            shutil.rmtree(root, ignore_errors=True)
            raise HTTPException(status_code=502, detail="download_failed") from None
        return FileResponse(
            artifact, media_type="audio/flac", filename=f"{song_id}.flac",
            background=BackgroundTask(shutil.rmtree, root, ignore_errors=True),
        )

    @application.get("/api/stream/{song_id}")
    async def stream_song(
        song_id: str, quality: str = "alac-16-44", account: MediaAccount = Depends(_account)
    ) -> StreamingResponse:
        _song_request(account, song_id, quality)

        async def content() -> AsyncIterator[bytes]:
            # Keep the existing immediate empty tag while this account prepares media.
            yield FLAC_GUIDANCE_PREFIX
            source = await prepare_song(account, song_id, quality, "aac-web")
            async for chunk in process_runner.stream_flac(source):
                yield chunk

        return StreamingResponse(content(), media_type="audio/flac", headers={
            "Content-Disposition": f'inline; filename="{song_id}.flac"',
            "Cache-Control": "no-store",
            "X-Accel-Buffering": "no",
        })

    @application.head("/api/download/{song_id}")
    @application.head("/api/stream/{song_id}")
    async def head_stream(
        song_id: str, quality: str = "alac-16-44", account: MediaAccount = Depends(_account)
    ) -> Response:
        _song_request(account, song_id, quality)
        response = Response(media_type="audio/flac", headers={
            "Content-Disposition": f'inline; filename="{song_id}.flac"',
        })
        del response.headers["content-length"]
        return response

    @application.get("/api/lyrics/{song_id}")
    async def lyrics_song(
        song_id: str, quality: str = "aac-he", account: MediaAccount = Depends(_account)
    ) -> dict[str, str]:
        url = _song_request(account, song_id, quality)
        key = _cache_key(account, song_id, quality)
        cached = lyrics_path(key)

        async def prepare_lyrics() -> Path:
            if not cached.is_symlink() and cached.is_file():
                return cached
            root = config.data_root / "artifacts" / uuid.uuid4().hex
            try:
                lyrics = await process_runner.download_lyrics(
                    url, root / "output", root / "temporary", media_user_token=account.token
                )
                if not lyrics:
                    raise ProcessFailure("artifact_missing")
                return cache_lyrics(lyrics[0], key)
            except ProcessFailure as exc:
                status = 504 if exc.code == "process_timeout" else 403 if exc.code == "account_mismatch" else 404
                raise HTTPException(status_code=status, detail=exc.code) from None
            finally:
                shutil.rmtree(root, ignore_errors=True)

        if cached.is_symlink() or not cached.is_file():
            cached = await coalesce("lyrics:" + key, prepare_lyrics)
        try:
            content = cached.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            raise HTTPException(status_code=502, detail="lyrics_unreadable") from None
        if not content.strip():
            raise HTTPException(status_code=404, detail="lyrics_not_found")
        return {"source": "GAMDL", "format": "LineTimed", "content": content}

    @application.head("/api/lyrics/{song_id}")
    async def head_lyrics(
        song_id: str, quality: str = "aac-he", account: MediaAccount = Depends(_account)
    ) -> Response:
        _song_request(account, song_id, quality)
        response = Response(media_type="application/json")
        del response.headers["content-length"]
        return response

    return application


app = create_app()
