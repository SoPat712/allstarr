from __future__ import annotations

import asyncio
import hashlib
import importlib.metadata
import logging
import os
import shutil
import time
import uuid
import xml.etree.ElementTree as ET
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, AsyncIterator, Awaitable, Callable
from dataclasses import dataclass

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse, Response, StreamingResponse
from starlette.background import BackgroundTask

from .config import Settings
from .delivery import AudioResponse, SharedAudio
from .models import Login2faRequest, LoginRequest, MediaAccount
from .runner import AudioCeiling, BoundedProcessRunner, ProcessFailure, log_audio_failure
from .security import diagnostic_tail, media_account, song_url
from .wrapper import WrapperClient, WrapperResponse

API_VERSION = "2.1.0"
CAPABILITIES = (
    "stream-audio-song",
    "audio-file",
    "download-audio-song",
    "synced-lyrics-artifact",
    "codec-alac",
    "codec-aac",
)
PREPARED_CACHE_TTL_SECONDS = 6 * 60 * 60
PREPARED_CACHE_MAX_TRACKS = 32
logger = logging.getLogger("uvicorn.error")
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


# The requested tier is a ceiling: GAMDL picks the largest stream within it, and conversion covers songs without one.
AUDIO_CEILINGS = {"alac-16-44": AudioCeiling(16, 44100), "alac-24-96": AudioCeiling(24, 96000)}


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


def _lyrics_timing(content: str) -> str:
    if content.lstrip().startswith("<"):
        try:
            root = ET.fromstring(content)
            if any(element.tag.rsplit("}", 1)[-1] == "span" and element.get("begin") is not None and
                    (element.get("end") is not None or element.get("dur") is not None) and
                    "".join(element.itertext()).strip() for element in root.iter()):
                return "word"
        except ET.ParseError:
            pass
    return "line"


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
    audio_preparations: dict[str, SharedAudio] = {}
    preparation_lock = asyncio.Lock()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        config.prepare()
        # A waiting helper holds about 80 MB.
        if warm_up := getattr(process_runner, "warm_up_audio", None) or getattr(process_runner, "warm_up", None):
            warm_up()
        yield
        async with preparation_lock:
            tasks = [entry.task for entry in [*preparations.values(), *audio_preparations.values()]]
            for task in tasks:
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        if close := getattr(process_runner, "close", None):
            await close()
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

    def preparation_failure(exc):
        code = exc.code if isinstance(exc, ProcessFailure) else "download_failed"
        status = 504 if code == "process_timeout" else 403 if code == "account_mismatch" else 502
        return HTTPException(status_code=status, detail=code,
            headers={"X-Allstarr-Failure-Code": code} if code == "account_mismatch" else None)

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
    ) -> tuple[Path, Path, str]:
        url = _song_request(account, song_id, quality)
        root = config.data_root / "artifacts" / uuid.uuid4().hex
        used_quality = quality
        try:
            try:
                limit = AUDIO_CEILINGS.get(quality.lower()) if _codec(quality) == "alac" else None
                artifacts = await process_runner.download(
                    url, _codec(quality), root / "output", root / "temporary", media_user_token=account.token,
                    **({"ceiling": f"{limit.bit_depth}-{limit.sample_rate}"} if limit else {}),
                )
            except ProcessFailure as exc:
                if fallback_quality is None or exc.code not in {"artifact_missing", "audio_artifact_missing", "gamdl_failed"}:
                    raise
                used_quality = fallback_quality
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
            return root, audio[0], used_quality
        except asyncio.CancelledError:
            shutil.rmtree(root, ignore_errors=True)
            raise
        except ProcessFailure as exc:
            exc.quality = used_quality
            shutil.rmtree(root, ignore_errors=True)
            status = 504 if exc.code == "process_timeout" else 403 if exc.code == "account_mismatch" else 502
            raise HTTPException(status_code=status, detail=exc.code,
                headers={"X-Allstarr-Failure-Code": "account_mismatch"} if exc.code == "account_mismatch" else None) from exc
        except Exception:
            shutil.rmtree(root, ignore_errors=True)
            raise HTTPException(status_code=502, detail="download_failed") from None

    def cached_source(key: str, *, audio_file: bool = False, original_container: bool = False,
            stream: bool = False) -> Path | None:
        cache_root = config.data_root / "prepared"
        now = time.time()
        tracks: dict[str, list[Path]] = {}
        for path in cache_root.glob("*"):
            if not path.is_symlink() and path.is_file() and not path.name.endswith(".partial"):
                tracks.setdefault(path.name.split(".")[0], []).append(path)
        ordered = sorted(tracks.values(), key=lambda paths: max(path.stat().st_atime for path in paths), reverse=True)
        found = None
        for index, paths in enumerate(ordered):
            if index >= PREPARED_CACHE_MAX_TRACKS or now - max(path.stat().st_atime for path in paths) > PREPARED_CACHE_TTL_SECONDS:
                for path in paths:
                    path.unlink(missing_ok=True)
            elif paths[0].name.split(".")[0] == key:
                for path in paths:
                    os.utime(path, ns=(time.time_ns(), path.stat().st_mtime_ns))
                    if (".audio." in path.name) == audio_file and (
                        not audio_file or (".audio.original." in path.name) == original_container
                    ) and (".audio.stream." in path.name) == stream:
                        found = path
        return found

    async def validate_cached(source, account, song_id):
        validate = getattr(process_runner, "validate_audio", None) or BoundedProcessRunner(config).validate_audio
        try:
            await asyncio.wait_for(validate(source), config.subprocess_timeout_seconds)
        except ProcessFailure as exc:
            log_audio_failure(song_id, exc, stage="validate", token=account.token)
            if remember := getattr(process_runner, "remember_file_retry", None):
                remember(song_id)
            raise

    async def progressive_audio(account, song_id, quality, *, original_container=False, force_flac=False):
        from .finalization import finalize_audio
        key = _cache_key(account, song_id, quality)
        variant = ".original" if original_container else ".stream" if force_flac else ""
        identity = f"{key}{variant}"

        def target(header):
            return config.data_root / "prepared" / f"{key}.audio{variant}{header.suffix}"

        def completed(header):
            if header.lyrics:
                path = lyrics_path(key)
                path.parent.mkdir(parents=True, exist_ok=True, mode=0o750)
                partial = path.with_name(f"{path.name}.{uuid.uuid4().hex}.partial")
                try:
                    partial.write_text(header.lyrics, encoding="utf-8")
                    partial.replace(path)
                finally:
                    partial.unlink(missing_ok=True)

        async with preparation_lock:
            entry = audio_preparations.get(identity)
            if entry is None:
                entry = SharedAudio(lambda: process_runner.open_audio(song_id, _codec(quality),
                    media_user_token=account.token, storefront=account.storefront,
                    ceiling=AUDIO_CEILINGS.get(quality.lower()), original_container=original_container,
                    force_flac=force_flac), target, completed=completed, song_id=song_id, quality=quality,
                    validate=lambda source: validate_cached(source, account, song_id),
                    finalize=lambda source, target: asyncio.wait_for(
                        finalize_audio(source, target, config.ffmpeg_path), config.subprocess_timeout_seconds))
                audio_preparations[identity] = entry
            entry.waiters += 1
        released = False

        async def release():
            nonlocal released
            if released:
                return
            released = True
            canceled = None
            async with preparation_lock:
                entry.waiters -= 1
                if entry.waiters == 0:
                    if audio_preparations.get(identity) is entry:
                        audio_preparations.pop(identity)
                    if not entry.task.done():
                        entry.task.cancel()
                        canceled = entry.task
            if canceled is not None:
                await asyncio.gather(canceled, return_exceptions=True)
            if entry.waiters == 0:
                entry.discard()

        try:
            header = await asyncio.shield(entry.ready)
            if entry.finished and entry.error is not None:
                raise entry.error
        except BaseException as exc:
            await release()
            if isinstance(exc, asyncio.CancelledError):
                raise
            raise preparation_failure(exc) from None
        return entry, header, entry.read(release), release

    async def prepare_song(
        account: MediaAccount, song_id: str, quality: str, fallback_quality: str | None = None
    ) -> Path:
        key = _cache_key(account, song_id, quality)
        if hasattr(process_runner, "open_audio"):
            if cached := cached_source(key, audio_file=True, original_container=True):
                return cached
            entry, _, chunks, release = await progressive_audio(account, song_id, quality, original_container=True)
            try:
                async for _ in chunks:
                    pass
                return entry.path
            except Exception as exc:
                raise preparation_failure(exc) from None
            finally:
                await chunks.aclose()
                await release()
        if cached := cached_source(key):
            return cached

        async def prepare_and_cache() -> Path:
            if cached := cached_source(key):
                return cached
            root, source, used_quality = await download_song_source(account, song_id, quality, fallback_quality)
            try:
                cache_root = config.data_root / "prepared"
                cache_root.mkdir(parents=True, exist_ok=True, mode=0o750)
                target = cache_root / f"{key}.{used_quality.lower()}{source.suffix.lower()}"
                partial = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
                try:
                    shutil.copyfile(source, partial)
                    await validate_cached(partial, account, song_id)
                    partial.replace(target)
                finally:
                    partial.unlink(missing_ok=True)
                target.touch()
                cached_source(key)
                return target
            finally:
                shutil.rmtree(root, ignore_errors=True)

        return await coalesce("audio:" + key, prepare_and_cache)

    @application.api_route("/api/audio/{song_id}", methods=["GET", "HEAD"])
    async def audio_song(
        song_id: str, quality: str = "alac-16-44", account: MediaAccount = Depends(_account),
        original_container: bool = False,
        request: Request = None,
    ) -> Response:
        _song_request(account, song_id, quality)
        key = _cache_key(account, song_id, quality)
        preparation_headers = {
            "X-Allstarr-Prepare-Download-Ms": "0",
            "X-Allstarr-Prepare-Process-Ms": "0",
        }

        cached = cached_source(key, audio_file=True, original_container=original_container)
        if cached is None and hasattr(process_runner, "open_audio"):
            entry, header, content, release = await progressive_audio(account, song_id, quality,
                original_container=original_container)
            if request is not None and request.method == "HEAD":
                try:
                    async for _ in content:
                        pass
                except Exception as exc:
                    raise preparation_failure(exc) from None
                cached = entry.path
            else:
                headers = {**preparation_headers, "Cache-Control": "no-store", "X-Accel-Buffering": "no",
                    "Content-Disposition": f'inline; filename="{song_id}{header.suffix}"'}
                headers["X-Allstarr-Prepare-Download-Ms"] = str(entry.first_byte_ms)
                if header.duration_seconds is not None:
                    headers["X-Allstarr-Duration-Seconds"] = str(header.duration_seconds)
                return AudioResponse(content, media_type=header.media_type, headers=headers, release=release)

        async def prepare_audio() -> Path:
            if cached := cached_source(key, audio_file=True, original_container=original_container):
                return cached
            gamdl_seconds = conversion_seconds = 0.0
            used_quality = quality
            started = time.monotonic()
            partial = None
            try:
                source = await prepare_song(account, song_id, quality, "aac-web")
                gamdl_seconds = time.monotonic() - started
                started = time.monotonic()
                used_quality = source.name.split(".")[1] if source.name.count(".") == 2 else quality
                codec = await process_runner.audio_codec(source)
                if codec not in {"aac", "alac", "flac"}:
                    raise ProcessFailure("audio_artifact_missing")
                ceiling = AUDIO_CEILINGS.get(quality) if codec in {"alac", "flac"} else None
                if ceiling and not await process_runner.exceeds_ceiling(source, ceiling):
                    ceiling = None
                copy_original = codec == "aac" or original_container and codec == "alac" and ceiling is None
                if copy_original and source.suffix.lower() != ".m4a":
                    raise ProcessFailure("audio_artifact_missing")
                suffix = ".m4a" if copy_original else ".flac"
                variant = ".original" if original_container else ""
                target = source.parent / f"{key}.audio{variant}{suffix}"
                partial = target.with_name(f"{target.name}.{uuid.uuid4().hex}.partial")
                if copy_original:
                    shutil.copyfile(source, partial)
                    await asyncio.sleep(0)
                else:
                    await process_runner.seekable_flac(source, partial, ceiling) if ceiling \
                        else await process_runner.seekable_flac(source, partial)
                await validate_cached(partial, account, song_id)
                partial.replace(target)
                cached_source(key, audio_file=True, original_container=original_container)
                conversion_seconds = time.monotonic() - started
                preparation_headers["X-Allstarr-Prepare-Download-Ms"] = str(round(gamdl_seconds * 1000))
                preparation_headers["X-Allstarr-Prepare-Process-Ms"] = str(round(conversion_seconds * 1000))
                logger.info("Audio prepare song=%s quality=%s gamdl_seconds=%.3f conversion_seconds=%.3f bytes=%d",
                    song_id, used_quality, gamdl_seconds, conversion_seconds, target.stat().st_size)
                return target
            except (HTTPException, ProcessFailure) as exc:
                if isinstance(exc, HTTPException):
                    code = str(exc.detail)
                    failure = exc.__cause__
                    gamdl_seconds = time.monotonic() - started
                else:
                    code = exc.code
                    failure = exc
                    conversion_seconds = time.monotonic() - started
                used_quality = getattr(failure, "quality", None) or used_quality
                tail = diagnostic_tail(getattr(failure, "stderr", ""), account.token).replace("\n", " | ")
                logger.warning("Audio prepare song=%s quality=%s gamdl_seconds=%.3f conversion_seconds=%.3f bytes=0 stage=%s failure=%s stderr=%s",
                    song_id, used_quality, gamdl_seconds, conversion_seconds, getattr(failure, "stage", None) or "prepare", code, tail)
                if isinstance(exc, HTTPException):
                    raise
                status = 504 if code == "process_timeout" else 403 if code == "account_mismatch" else 502
                raise HTTPException(status_code=status, detail=code,
                    headers={"X-Allstarr-Failure-Code": code} if code == "account_mismatch" else None) from None
            except Exception:
                logger.warning("Audio prepare song=%s quality=%s gamdl_seconds=%.3f conversion_seconds=%.3f bytes=0 failure=download_failed",
                    song_id, used_quality, gamdl_seconds, time.monotonic() - started)
                raise HTTPException(status_code=502, detail="download_failed") from None
            finally:
                if partial is not None:
                    partial.unlink(missing_ok=True)

        variant = "original:" if original_container else ""
        artifact = cached or await coalesce(
            "file:" + variant + key, prepare_audio
        )
        is_mp4 = artifact.suffix == ".m4a"
        return FileResponse(artifact, media_type="audio/mp4" if is_mp4 else "audio/flac",
            filename=f"{song_id}{artifact.suffix}", content_disposition_type="inline", headers=preparation_headers)

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
            log_audio_failure(song_id, exc, token=account.token)
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
    ) -> Response:
        _song_request(account, song_id, quality)
        key = _cache_key(account, song_id, quality)
        if hasattr(process_runner, "open_audio") and (cached := cached_source(key, audio_file=True, stream=True)):
            return FileResponse(cached, media_type="audio/flac", filename=f"{song_id}.flac",
                content_disposition_type="inline")
        chunks = release = None

        async def close_stream():
            if chunks is not None:
                await chunks.aclose()
            if release is not None:
                await release()

        async def content() -> AsyncIterator[bytes]:
            nonlocal chunks, release
            yield FLAC_GUIDANCE_PREFIX
            if hasattr(process_runner, "open_audio"):
                _, _, chunks, release = await progressive_audio(account, song_id, quality, force_flac=True)
                try:
                    async for chunk in chunks:
                        yield chunk
                finally:
                    await close_stream()
            else:
                source = await prepare_song(account, song_id, quality, "aac-web")
                try:
                    async for chunk in process_runner.stream_flac(source):
                        yield chunk
                except ProcessFailure as exc:
                    log_audio_failure(song_id, exc, token=account.token)
                    raise

        headers = {
            "Content-Disposition": f'inline; filename="{song_id}.flac"',
            "Cache-Control": "no-store",
            "X-Accel-Buffering": "no",
        }
        return AudioResponse(content(), media_type="audio/flac", headers=headers, release=close_stream)

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
    ) -> JSONResponse:
        url = _song_request(account, song_id, quality)
        key = _cache_key(account, song_id, quality) + "-ttml-v2"

        def cached_lyrics() -> Path | None:
            for timing in ("word", "line"):
                path = lyrics_path(key + "-" + timing)
                if not path.is_symlink() and path.is_file():
                    return path
            return None

        async def prepare_lyrics() -> Path:
            if cached := cached_lyrics():
                return cached
            if open_lyrics := getattr(process_runner, "lyrics", None):
                try:
                    content = await open_lyrics(song_id, media_user_token=account.token, storefront=account.storefront)
                except ProcessFailure as exc:
                    status = 504 if exc.code == "process_timeout" else 403 if exc.code == "account_mismatch" else 404
                    raise HTTPException(status_code=status, detail=exc.code) from None
                if not content:
                    raise HTTPException(status_code=404, detail="lyrics_not_found")
                cached = lyrics_path(key + "-" + _lyrics_timing(content))
                cached.parent.mkdir(parents=True, exist_ok=True, mode=0o750)
                partial = cached.with_name(f"{cached.name}.{uuid.uuid4().hex}.partial")
                try:
                    partial.write_text(content, encoding="utf-8")
                    partial.replace(cached)
                finally:
                    partial.unlink(missing_ok=True)
                return cached
            root = config.data_root / "artifacts" / uuid.uuid4().hex
            try:
                lyrics = await process_runner.download_lyrics(
                    url, root / "output", root / "temporary", media_user_token=account.token
                )
                if not lyrics:
                    raise ProcessFailure("artifact_missing")
                content = lyrics[0].read_text(encoding="utf-8")
                return cache_lyrics(lyrics[0], key + "-" + _lyrics_timing(content))
            except ProcessFailure as exc:
                status = 504 if exc.code == "process_timeout" else 403 if exc.code == "account_mismatch" else 404
                raise HTTPException(status_code=status, detail=exc.code) from None
            finally:
                shutil.rmtree(root, ignore_errors=True)

        cached = cached_lyrics()
        if cached is None:
            cached = await coalesce("lyrics:" + key, prepare_lyrics)
        try:
            content = cached.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            raise HTTPException(status_code=502, detail="lyrics_unreadable") from None
        if not content.strip():
            raise HTTPException(status_code=404, detail="lyrics_not_found")
        return JSONResponse({"source": "GAMDL", "format": "TTML" if content.lstrip().startswith("<") else "LineTimed", "content": content},
            headers={"X-Lyrics-Timing": _lyrics_timing(content)})

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
