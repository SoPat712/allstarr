from __future__ import annotations

import importlib.metadata
import shutil
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

import httpx
from fastapi import FastAPI, HTTPException, Query
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from starlette.background import BackgroundTask

from .catalog import CatalogClient
from .config import Settings
from .jobs import DownloadJobManager
from .models import DownloadJobRequest, DownloadJobView, Login2faRequest, LoginRequest
from .runner import BoundedProcessRunner, ProcessFailure
from .security import safe_apple_url, song_url
from .wrapper import WrapperClient, WrapperResponse

API_VERSION = "1.0.0"
CAPABILITIES = (
    "metadata-search-song",
    "metadata-search-album",
    "metadata-search-artist",
    "metadata-song",
    "metadata-album",
    "metadata-artist",
    "stream-audio-song",
    "download-audio-song",
    "synced-lyrics-artifact",
    "codec-alac",
    "codec-aac",
)


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


def create_app(
    settings: Settings | None = None,
    wrapper: WrapperClient | None = None,
    catalog: CatalogClient | None = None,
    runner: BoundedProcessRunner | None = None,
) -> FastAPI:
    config = settings or Settings.from_env()
    wrapper_client = wrapper or WrapperClient(config.wrapper_url, config.wrapper_timeout_seconds)
    catalog_client = catalog or CatalogClient(config.storefront)
    process_runner = runner or BoundedProcessRunner(config)
    jobs = DownloadJobManager(process_runner, config.data_root)

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        config.prepare()
        jobs.start()
        yield
        await jobs.close()
        await wrapper_client.close()
        await catalog_client.close()

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

    @application.get("/api/search")
    async def search(
        q: str = Query(min_length=1, max_length=500),
        type: str = Query(default="song", pattern="^(song|album|artist)$"),
        limit: int = Query(default=20, ge=1, le=100),
    ) -> list[dict[str, Any]]:
        try:
            return await catalog_client.search(q, type, limit)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None

    @application.get("/api/song/{song_id}")
    async def song(song_id: str) -> dict[str, Any]:
        try:
            result = await catalog_client.song(song_id)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None
        if result is None:
            raise HTTPException(status_code=404, detail="song_not_found")
        return result

    @application.get("/api/album/{album_id}")
    async def album(album_id: str) -> dict[str, Any]:
        try:
            result = await catalog_client.album(album_id)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None
        if result is None:
            raise HTTPException(status_code=404, detail="album_not_found")
        return result

    @application.get("/api/artist/{artist_id}")
    async def artist(artist_id: str) -> dict[str, Any]:
        try:
            result = await catalog_client.artist(artist_id)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None
        if result is None:
            raise HTTPException(status_code=404, detail="artist_not_found")
        return result

    @application.get("/api/artist/{artist_id}/albums")
    async def artist_albums(
        artist_id: str,
        limit: int = Query(default=100, ge=1, le=200),
    ) -> list[dict[str, Any]]:
        try:
            return await catalog_client.artist_albums(artist_id, limit)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None

    @application.get("/api/artist/{artist_id}/tracks")
    async def artist_tracks(
        artist_id: str,
        limit: int = Query(default=100, ge=1, le=200),
    ) -> list[dict[str, Any]]:
        try:
            return await catalog_client.artist_tracks(artist_id, limit)
        except (httpx.HTTPError, ValueError):
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None

    async def prepare_song(
        song_id: str,
        quality: str,
        fallback_quality: str | None = None,
    ) -> tuple[Path, Path]:
        try:
            song_url(config.storefront, song_id)
            canonical_url = await catalog_client.song_url(song_id)
            if canonical_url is None:
                raise HTTPException(status_code=404, detail="song_not_found")
            url, _ = safe_apple_url(canonical_url)
        except ValueError:
            raise HTTPException(status_code=400, detail="invalid_song_id") from None
        except httpx.HTTPError:
            raise HTTPException(status_code=502, detail="catalog_unavailable") from None
        request_id = uuid.uuid4().hex
        root = config.data_root / "artifacts" / request_id
        try:
            try:
                artifacts = await process_runner.download(url, _codec(quality), root / "output", root / "temporary")
            except ProcessFailure as exc:
                if fallback_quality is None or exc.code not in {"artifact_missing", "gamdl_failed"}:
                    raise
                artifacts = await process_runner.download(
                    url,
                    _codec(fallback_quality),
                    root / "output-fallback",
                    root / "temporary-fallback",
                )
            lyrics = [artifact for artifact in artifacts if artifact.suffix.lower() == ".lrc"]
            if lyrics:
                lyrics_root = config.data_root / "lyrics"
                lyrics_root.mkdir(exist_ok=True, mode=0o750)
                target = lyrics_root / f"{song_id}.lrc"
                partial = lyrics_root / f"{song_id}.lrc.partial"
                shutil.copyfile(lyrics[0], partial)
                partial.replace(target)
            audio = [artifact for artifact in artifacts if artifact.suffix.lower() in {".m4a", ".flac"}]
            if not audio:
                raise ProcessFailure("audio_artifact_missing")
            return root, audio[0]
        except ProcessFailure as exc:
            shutil.rmtree(root, ignore_errors=True)
            status = 504 if exc.code == "process_timeout" else 502
            raise HTTPException(status_code=status, detail=exc.code) from None
        except Exception:
            shutil.rmtree(root, ignore_errors=True)
            raise HTTPException(status_code=502, detail="download_failed") from None

    @application.get("/api/download/{song_id}")
    async def download_song(song_id: str, quality: str = "alac-16-44") -> FileResponse:
        root, source = await prepare_song(song_id, quality)
        try:
            artifact = await process_runner.to_flac(source, root / f"{song_id}.flac")
        except ProcessFailure as exc:
            shutil.rmtree(root, ignore_errors=True)
            status = 504 if exc.code == "process_timeout" else 502
            raise HTTPException(status_code=status, detail=exc.code) from None
        except Exception:
            shutil.rmtree(root, ignore_errors=True)
            raise HTTPException(status_code=502, detail="download_failed") from None
        return FileResponse(
            artifact,
            media_type="audio/flac",
            filename=f"{song_id}.flac",
            background=BackgroundTask(shutil.rmtree, root, ignore_errors=True),
        )

    @application.get("/api/stream/{song_id}")
    async def stream_song(song_id: str, quality: str = "alac-16-44") -> StreamingResponse:
        root, source = await prepare_song(song_id, quality, "aac-web")
        return StreamingResponse(
            process_runner.stream_flac(source),
            media_type="audio/flac",
            headers={"Content-Disposition": f'inline; filename="{song_id}.flac"'},
            background=BackgroundTask(shutil.rmtree, root, ignore_errors=True),
        )

    @application.get("/api/lyrics/{song_id}")
    async def lyrics_song(song_id: str) -> dict[str, str]:
        try:
            song_url(config.storefront, song_id)
        except ValueError:
            raise HTTPException(status_code=400, detail="invalid_song_id") from None
        cached = config.data_root / "lyrics" / f"{song_id}.lrc"
        if not cached.is_file():
            root, _ = await prepare_song(song_id, "alac-16-44")
            shutil.rmtree(root, ignore_errors=True)
        if not cached.is_file():
            raise HTTPException(status_code=404, detail="lyrics_not_found")
        try:
            content = cached.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            raise HTTPException(status_code=502, detail="lyrics_unreadable") from None
        if not content.strip():
            raise HTTPException(status_code=404, detail="lyrics_not_found")
        return {"source": "GAMDL", "format": "LineTimed", "content": content}

    @application.post("/api/jobs/download", status_code=202, response_model=DownloadJobView)
    async def enqueue_download(request: DownloadJobRequest) -> DownloadJobView:
        try:
            return jobs.enqueue(request.url, _codec(request.quality))
        except ValueError:
            raise HTTPException(status_code=400, detail="unsupported_apple_music_url") from None

    @application.get("/api/jobs/download/{job_id}", response_model=DownloadJobView)
    async def get_download_job(job_id: str) -> DownloadJobView:
        job = jobs.get(job_id)
        if job is None:
            raise HTTPException(status_code=404, detail="job_not_found")
        return job

    return application


app = create_app()
