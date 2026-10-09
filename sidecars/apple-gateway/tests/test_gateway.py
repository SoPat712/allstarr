from __future__ import annotations

import asyncio
import json
import os
import stat
import sys
from dataclasses import replace
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient
from fastapi.responses import StreamingResponse

from apple_gateway.app import (
    API_VERSION,
    FLAC_GUIDANCE_PREFIX,
    create_app,
)
from apple_gateway.config import Settings
from apple_gateway.security import media_account
from apple_gateway.runner import BoundedProcessRunner, ProcessFailure
from apple_gateway.wrapper import WrapperClient, WrapperResponse


ACCOUNT_HEADERS = {
    "Music-User-Token": "fixture-account-token",
    "X-Apple-Storefront": "us",
    "X-Allstarr-Account-Context": "a" * 64,
}
ACCOUNT = media_account(*ACCOUNT_HEADERS.values())


class FakeWrapper:
    def __init__(self, logged_in: bool = True):
        self.logged_in = logged_in
        self.login_payload: tuple[str, str] | None = None

    async def close(self) -> None:
        return None

    async def health(self) -> WrapperResponse:
        return WrapperResponse(200, {"status": "ok", "version": "0.0.2"})

    async def me(self) -> WrapperResponse:
        return WrapperResponse(200, {"auth": {"state": "authenticated" if self.logged_in else "logged_out"}})

    async def login(self, username: str, password: str) -> WrapperResponse:
        self.login_payload = (username, password)
        return WrapperResponse(202, {"auth": {"state": "awaiting_2fa"}})

    async def login_2fa(self, code: str) -> WrapperResponse:
        return WrapperResponse(200, {"auth": {"state": "authenticated"}})


class FakeRunner:
    def __init__(self):
        self.calls: list[tuple[str, str]] = []
        self.transcodes: list[str] = []
        self.tokens: list[str] = []

    async def download(self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str) -> list[Path]:
        self.calls.append((url, quality))
        self.tokens.append(media_user_token)
        output.mkdir(parents=True, exist_ok=False)
        temporary.mkdir(parents=True, exist_ok=False)
        artifact = output / "fixture.m4a"
        artifact.write_bytes(b"source")
        lyrics = output / "fixture.lrc"
        lyrics.write_text("[00:01.00]Fixture lyrics\n", encoding="utf-8")
        return [artifact, lyrics]

    async def download_lyrics(self, url: str, output: Path, temporary: Path, *, media_user_token: str) -> list[Path]:
        self.calls.append((url, "lyrics"))
        self.tokens.append(media_user_token)
        output.mkdir(parents=True, exist_ok=False)
        temporary.mkdir(parents=True, exist_ok=False)
        lyrics = output / "fixture.lrc"
        lyrics.write_text("[00:01.00]Fixture lyrics\n", encoding="utf-8")
        return [lyrics]

    async def to_flac(self, source: Path, target: Path) -> Path:
        self.transcodes.append("file")
        target.write_bytes(b"fLaCfixture")
        return target.resolve()

    async def stream_flac(self, source: Path):
        self.transcodes.append("stream")
        yield b"fLaC"
        yield b"fixture"


@pytest.fixture
def settings(tmp_path: Path) -> Settings:
    return Settings(
        wrapper_url="http://wrapper-v2",
        wrapper_decrypt_host="wrapper-v2",
        wrapper_decrypt_port=18080,
        data_root=tmp_path,
        cookies_path=None,
        storefront="us",
        gamdl_path=sys.executable,
        ffmpeg_path=sys.executable,
        subprocess_timeout_seconds=2,
        wrapper_timeout_seconds=2,
        max_concurrency=1,
        max_process_output_bytes=64,
    )


@pytest.fixture
def client(settings: Settings) -> tuple[TestClient, FakeWrapper, FakeRunner]:
    wrapper = FakeWrapper()
    runner = FakeRunner()
    app = create_app(settings, wrapper, runner)
    with TestClient(app, headers=ACCOUNT_HEADERS) as test_client:
        yield test_client, wrapper, runner


def test_capabilities_are_versioned_and_truthful(client):
    response = client[0].get("/api/capabilities")
    assert response.status_code == 200
    assert response.json()["sidecarApiVersion"] == API_VERSION
    ids = {item["id"] for item in response.json()["capabilities"]}
    assert {
        "download-audio-song",
        "synced-lyrics-artifact",
    } <= ids
    assert not any(item.startswith("metadata-") for item in ids)


def test_health_reports_wrapper_and_authentication(client):
    payload = client[0].get("/api/health").json()
    assert payload["wrapper_healthy"] is True
    assert payload["logged_in"] is True
    assert payload["versions"]["wrapper"] == "0.0.2"


def test_login_and_2fa_preserve_pending_status_without_returning_secrets(client):
    response = client[0].post("/api/login", json={"username": "user@example.test", "password": "private"})
    assert response.status_code == 202
    assert "private" not in response.text
    assert client[1].login_payload == ("user@example.test", "private")
    assert client[0].post("/api/login/2fa", json={"code": "123456"}).status_code == 200


@pytest.mark.parametrize("path", [
    "/api/search?q=fixture", "/api/song/101", "/api/album/301", "/api/artist/201",
    "/api/artist/201/albums", "/api/artist/201/tracks", "/api/jobs/download/fixture",
])
def test_catalog_and_job_routes_are_removed(client, path):
    assert client[0].get(path).status_code == 404
    assert client[2].calls == []


def test_job_creation_route_is_removed(client):
    response = client[0].post("/api/jobs/download", json={
        "url": "https://music.apple.com/us/song/101", "quality": "alac",
    })
    assert response.status_code == 404
    assert client[2].calls == []


def test_song_download_uses_safe_id_quality_mapping_and_flac_contract(client):
    response = client[0].get("/api/download/101", params={"quality": "alac-16-44"})
    assert response.status_code == 200
    assert response.headers["content-type"].startswith("audio/flac")
    assert response.content == b"fLaCfixture"
    assert client[2].calls == [("https://music.apple.com/us/song/101", "alac")]
    assert client[2].transcodes == ["file"]
    streamed = client[0].get("/api/stream/102", params={"quality": "aac-320"})
    assert streamed.status_code == 200
    assert streamed.headers["content-type"].startswith("audio/flac")
    assert streamed.content == FLAC_GUIDANCE_PREFIX + b"fLaCfixture"
    assert client[2].transcodes == ["file", "stream"]
    assert client[2].calls[-1] == ("https://music.apple.com/us/song/102", "aac")
    assert client[0].get("/api/download/not-an-id").status_code == 400


def test_song_stream_head_reports_only_known_facts_without_preparing_media(client):
    response = client[0].head("/api/stream/102", params={"quality": "aac-96"})

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("audio/flac")
    assert "content-length" not in response.headers
    assert "accept-ranges" not in response.headers
    assert client[2].calls == []
    assert client[2].transcodes == []
    assert client[0].head("/api/stream/not-an-id").status_code == 400
    assert client[0].head("/api/stream/102", params={"quality": "made-up"}).status_code == 400


@pytest.mark.asyncio
async def test_song_stream_opens_before_preparing_configured_quality(settings: Settings):
    runner = FakeRunner()
    app = create_app(settings, FakeWrapper(), runner)
    route = next(
        route
        for route in app.routes
        if getattr(route, "path", None) == "/api/stream/{song_id}"
    )

    response = await route.endpoint("102", "alac-16-44", ACCOUNT)

    assert isinstance(response, StreamingResponse)
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["x-accel-buffering"] == "no"
    assert runner.calls == []
    assert b"".join(
        [chunk async for chunk in response.body_iterator]
    ) == FLAC_GUIDANCE_PREFIX + b"fLaCfixture"
    assert runner.calls[-1][1] == "alac"


def test_flac_guidance_prefix_is_an_empty_id3v24_tag():
    assert FLAC_GUIDANCE_PREFIX == b"ID3\x04\x00\x00\x00\x00\x00\x00"
    assert len(FLAC_GUIDANCE_PREFIX) == 10


@pytest.mark.asyncio
async def test_song_stream_sends_guidance_prefix_before_apple_preparation(settings: Settings):
    class BlockingRunner(FakeRunner):
        def __init__(self):
            super().__init__()
            self.started = asyncio.Event()
            self.release = asyncio.Event()

        async def download(
            self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str
        ) -> list[Path]:
            self.started.set()
            await self.release.wait()
            return await super().download(url, quality, output, temporary, media_user_token=media_user_token)

    runner = BlockingRunner()
    app = create_app(settings, FakeWrapper(), runner)
    route = next(
        route
        for route in app.routes
        if getattr(route, "path", None) == "/api/stream/{song_id}"
    )
    response = await route.endpoint("102", "aac-320", ACCOUNT)

    assert await anext(response.body_iterator) == FLAC_GUIDANCE_PREFIX
    assert not runner.started.is_set()

    first_audio = asyncio.create_task(anext(response.body_iterator))
    await asyncio.wait_for(runner.started.wait(), timeout=0.5)
    assert not first_audio.done()
    runner.release.set()
    assert await asyncio.wait_for(first_audio, timeout=0.5) == b"fLaC"


def test_song_stream_falls_back_to_web_aac_when_lossless_is_unavailable(settings):
    class FallbackRunner(FakeRunner):
        async def download(self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str) -> list[Path]:
            self.calls.append((url, quality))
            if quality == "alac":
                raise ProcessFailure("artifact_missing")
            output.mkdir(parents=True, exist_ok=False)
            temporary.mkdir(parents=True, exist_ok=False)
            artifact = output / "fixture.m4a"
            artifact.write_bytes(b"source")
            return [artifact]

    runner = FallbackRunner()
    app = create_app(settings, FakeWrapper(), runner)
    with TestClient(app, headers=ACCOUNT_HEADERS) as test_client:
        response = test_client.get("/api/stream/102", params={"quality": "alac-16-44"})

    assert response.status_code == 200
    assert [quality for _, quality in runner.calls] == ["alac", "aac-web"]


def test_song_stream_reuses_prepared_source(settings):
    runner = FakeRunner()
    app = create_app(settings, FakeWrapper(), runner)
    with TestClient(app, headers=ACCOUNT_HEADERS) as test_client:
        first = test_client.get("/api/stream/102", params={"quality": "aac-320"})
        second = test_client.get("/api/stream/102", params={"quality": "aac-320"})

    assert first.status_code == 200
    assert second.status_code == 200
    assert len(runner.calls) == 1


@pytest.mark.asyncio
async def test_simultaneous_song_streams_share_preparation(settings):
    class BlockingRunner(FakeRunner):
        async def download(self, url: str, quality: str, output: Path, temporary: Path, *, media_user_token: str) -> list[Path]:
            await asyncio.sleep(0.01)
            return await super().download(url, quality, output, temporary, media_user_token=media_user_token)

    runner = BlockingRunner()
    app = create_app(settings, FakeWrapper(), runner)
    route = next(route for route in app.routes if getattr(route, "path", None) == "/api/stream/{song_id}")
    responses = await asyncio.gather(
        route.endpoint("102", "aac-320", ACCOUNT),
        route.endpoint("102", "aac-320", ACCOUNT),
    )
    prefixes = await asyncio.gather(*(anext(response.body_iterator) for response in responses))
    assert prefixes == [FLAC_GUIDANCE_PREFIX, FLAC_GUIDANCE_PREFIX]
    await asyncio.gather(*(anext(response.body_iterator) for response in responses))

    assert len(runner.calls) == 1


def test_song_lyrics_use_gamdl_artifact_and_cache(client):
    response = client[0].get("/api/lyrics/103")
    assert response.status_code == 200
    assert response.json() == {
        "source": "GAMDL",
        "format": "LineTimed",
        "content": "[00:01.00]Fixture lyrics\n",
    }
    calls = len(client[2].calls)
    assert client[0].get("/api/lyrics/103").status_code == 200
    assert len(client[2].calls) == calls
    assert client[2].calls == [("https://music.apple.com/us/song/103", "lyrics")]


@pytest.mark.asyncio
async def test_gamdl_command_targets_separate_wrapper_decrypt_socket(settings: Settings, tmp_path: Path):
    class CapturingRunner(BoundedProcessRunner):
        def __init__(self, configured: Settings):
            super().__init__(configured)
            self.argv: list[str] = []

        async def execute(self, argv: list[str], cwd: Path):
            from apple_gateway.runner import ProcessResult
            self.argv = argv
            (tmp_path / "output" / "fixture.m4a").parent.mkdir(parents=True, exist_ok=True)
            (tmp_path / "output" / "fixture.m4a").write_bytes(b"fixture")
            return ProcessResult(0, "", "")

    runner = CapturingRunner(settings)
    await runner.download(
        "https://music.apple.com/us/song/101",
        "alac",
        tmp_path / "output",
        tmp_path / "temporary",
        media_user_token="fixture-account-token",
    )
    host_index = runner.argv.index("--wrapper-decrypt-host")
    port_index = runner.argv.index("--wrapper-decrypt-port")
    assert runner.argv[host_index + 1] == "wrapper-v2"
    assert runner.argv[port_index + 1] == "18080"


@pytest.mark.asyncio
async def test_process_runner_caps_output_and_times_out(settings: Settings, tmp_path: Path):
    runner = BoundedProcessRunner(settings)
    result = await runner.execute([sys.executable, "-c", "print('x' * 1000)"], tmp_path)
    assert len(result.stdout.encode()) == settings.max_process_output_bytes

    timeout_settings = replace(settings, subprocess_timeout_seconds=0.05)
    timeout_runner = BoundedProcessRunner(timeout_settings)
    with pytest.raises(ProcessFailure, match="process_timeout"):
        await timeout_runner.execute([sys.executable, "-c", "import time; time.sleep(2)"], tmp_path)


@pytest.mark.asyncio
async def test_process_runner_streams_exact_flac_stdout(settings: Settings, tmp_path: Path):
    producer = tmp_path / "fake-ffmpeg"
    producer.write_text(
        "#!/usr/bin/env python3\n"
        "import sys, time\n"
        "sys.stdout.buffer.write(b'fLaC')\n"
        "sys.stdout.buffer.flush()\n"
        "time.sleep(0.01)\n"
        "sys.stdout.buffer.write(b'fixture')\n",
        encoding="utf-8",
    )
    producer.chmod(0o750)
    source = tmp_path / "source.m4a"
    source.write_bytes(b"encrypted-source-fixture")
    runner = BoundedProcessRunner(replace(settings, ffmpeg_path=str(producer)))

    chunks = [chunk async for chunk in runner.stream_flac(source, chunk_size=4)]

    assert b"".join(chunks) == b"fLaCfixture"


@pytest.mark.asyncio
async def test_process_runner_stamps_piped_flac_duration_without_changing_frames(
    settings: Settings, tmp_path: Path
):
    packed = (48_000 << 44) | (1 << 41) | (15 << 36)
    header = b"fLaC\x00\x00\x00\x22" + b"\x00" * 10 + packed.to_bytes(8, "big")
    producer = tmp_path / "fake-ffmpeg"
    producer.write_text(
        "#!/usr/bin/env python3\n"
        f"import sys; sys.stdout.buffer.write({(header + b'unchanged-frames')!r})\n",
        encoding="utf-8",
    )
    producer.chmod(0o750)
    probe = tmp_path / "ffprobe"
    probe.write_text(
        "#!/usr/bin/env python3\n"
        "print('{\"streams\":[{}],\"format\":{\"duration\":\"2.5\"}}')\n",
        encoding="utf-8",
    )
    probe.chmod(0o750)
    source = tmp_path / "source.m4a"
    source.write_bytes(b"source")
    runner = BoundedProcessRunner(replace(settings, ffmpeg_path=str(producer)))

    output = b"".join([chunk async for chunk in runner.stream_flac(source)])

    assert output[:18] == header[:18]
    assert int.from_bytes(output[18:26], "big") & ((1 << 36) - 1) == 120_000
    assert output[26:] == b"unchanged-frames"


def test_piped_flac_header_only_stamps_unknown_valid_duration():
    packed = (44_100 << 44) | (1 << 41) | (15 << 36)
    header = b"fLaC\x00\x00\x00\x22" + b"\x00" * 10 + packed.to_bytes(8, "big")

    assert BoundedProcessRunner._flac_header_with_duration(header, 2) == (
        header[:18] + (packed | 88_200).to_bytes(8, "big")
    )
    assert BoundedProcessRunner._flac_header_with_duration(header, None) == header
    assert BoundedProcessRunner._flac_header_with_duration(header[:11], 2) == header[:11]
    assert BoundedProcessRunner._flac_header_with_duration(header[:18] + (packed | 1).to_bytes(8, "big"), 2) == (
        header[:18] + (packed | 1).to_bytes(8, "big")
    )


@pytest.mark.asyncio
async def test_process_runner_relays_existing_flac_without_reencoding(settings: Settings, tmp_path: Path):
    source = tmp_path / "source.flac"
    source.write_bytes(b"fLaCready")
    runner = BoundedProcessRunner(settings)

    chunks = [chunk async for chunk in runner.stream_flac(source, chunk_size=3)]

    assert b"".join(chunks) == b"fLaCready"


@pytest.mark.asyncio
async def test_wrapper_client_rejects_redirects_and_redacts_nested_tokens():
    async def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/redirect":
            return httpx.Response(302, headers={"location": "https://evil.example"})
        return httpx.Response(200, json={"auth": {"state": "authenticated", "token": "secret"}})

    http_client = httpx.AsyncClient(base_url="http://wrapper/", transport=httpx.MockTransport(handler), follow_redirects=False)
    wrapper = WrapperClient("http://wrapper", 2, http_client)
    assert (await wrapper.request("GET", "redirect")).payload["error"] == "wrapper_redirect_rejected"
    payload = (await wrapper.me()).payload
    assert payload == {"auth": {"state": "authenticated"}}
    await http_client.aclose()


@pytest.mark.parametrize("method", ["GET", "HEAD"])
@pytest.mark.parametrize("kind", ["stream", "download", "lyrics"])
@pytest.mark.parametrize("failure", ["missing", "token", "storefront", "scope", "duplicate"])
def test_media_requires_valid_selected_account_before_any_work(settings, method, kind, failure):
    headers = ACCOUNT_HEADERS.copy()
    if failure == "missing":
        headers.pop("Music-User-Token")
    elif failure == "token":
        headers["Music-User-Token"] = "fixture;other=value"
    elif failure == "storefront":
        headers["X-Apple-Storefront"] = "US"
    elif failure == "scope":
        headers["X-Allstarr-Account-Context"] = "../private"
    else:
        headers = list(headers.items()) + [("Music-User-Token", "second-fixture-token")]
    runner = FakeRunner()
    with TestClient(create_app(settings, FakeWrapper(), runner)) as test_client:
        response = test_client.request(method, f"/api/{kind}/101", headers=headers)
    assert response.status_code == 401
    if method == "GET":
        assert response.json() == {"detail": "account_context_required"}
        assert "fixture;" not in response.text
        assert "second-fixture" not in response.text
    assert runner.calls == []
    assert runner.transcodes == []


@pytest.mark.parametrize("kind", ["download", "stream", "lyrics"])
def test_media_cache_separates_account_revision_storefront_and_quality(settings, kind):
    runner = FakeRunner()
    variants = [
        (ACCOUNT_HEADERS, "aac"),
        ({**ACCOUNT_HEADERS, "Music-User-Token": "second-account-token", "X-Allstarr-Account-Context": "b" * 64}, "aac"),
        ({**ACCOUNT_HEADERS, "X-Allstarr-Account-Context": "c" * 64}, "aac"),
        ({**ACCOUNT_HEADERS, "X-Apple-Storefront": "gb"}, "aac"),
        (ACCOUNT_HEADERS, "aac-he"),
    ]
    with TestClient(create_app(settings, FakeWrapper(), runner)) as test_client:
        for headers, quality in variants:
            response = test_client.get(f"/api/{kind}/101", headers=headers, params={"quality": quality})
            assert response.status_code == 200
        assert len(runner.calls) == 5
        for headers, quality in variants:
            assert test_client.get(f"/api/{kind}/101", headers=headers, params={"quality": quality}).status_code == 200
    assert len(runner.calls) == 5
    assert runner.tokens == [
        "fixture-account-token", "second-account-token", "fixture-account-token",
        "fixture-account-token", "fixture-account-token",
    ]
    assert runner.calls[3][0] == "https://music.apple.com/gb/song/101"
    cached = list((settings.data_root / ("lyrics" if kind == "lyrics" else "prepared")).glob("*"))
    assert len(cached) == 5
    assert all("fixture" not in path.name and "token" not in path.name for path in cached)


@pytest.mark.parametrize("kind", ["stream", "download", "lyrics"])
def test_head_validates_account_without_preparing_artifacts(client, kind):
    response = client[0].head(f"/api/{kind}/101")
    assert response.status_code == 200
    assert "content-length" not in response.headers
    assert client[2].calls == []
    assert client[2].tokens == []


@pytest.mark.asyncio
async def test_simultaneous_lyrics_for_same_account_coalesce(settings):
    class BlockingRunner(FakeRunner):
        async def download_lyrics(self, url, output, temporary, *, media_user_token):
            await asyncio.sleep(0.01)
            return await super().download_lyrics(url, output, temporary, media_user_token=media_user_token)

    runner = BlockingRunner()
    app = create_app(settings, FakeWrapper(), runner)
    route = next(route for route in app.routes if getattr(route, "path", None) == "/api/lyrics/{song_id}" and "GET" in route.methods)
    results = await asyncio.gather(
        route.endpoint("101", "aac-he", ACCOUNT),
        route.endpoint("101", "aac-he", ACCOUNT),
    )
    assert results[0] == results[1]
    assert len(runner.calls) == 1


@pytest.mark.asyncio
async def test_simultaneous_accounts_do_not_share_preparation(settings):
    entered = 0
    both_entered = asyncio.Event()

    class BlockingRunner(FakeRunner):
        async def download(self, url, quality, output, temporary, *, media_user_token):
            nonlocal entered
            entered += 1
            if entered == 2:
                both_entered.set()
            await asyncio.wait_for(both_entered.wait(), 1)
            return await super().download(url, quality, output, temporary, media_user_token=media_user_token)

    runner = BlockingRunner()
    app = create_app(settings, FakeWrapper(), runner)
    route = next(route for route in app.routes if getattr(route, "path", None) == "/api/stream/{song_id}" and "GET" in route.methods)
    other = media_account("other-account-token", "us", "b" * 64)
    responses = [await route.endpoint("101", "aac", account) for account in (ACCOUNT, other)]
    assert await asyncio.gather(*(anext(response.body_iterator) for response in responses)) == [FLAC_GUIDANCE_PREFIX] * 2
    await asyncio.wait_for(asyncio.gather(*(anext(response.body_iterator) for response in responses)), 2)
    assert set(runner.tokens) == {"fixture-account-token", "other-account-token"}
    assert len(runner.calls) == 2
    for response in responses:
        await response.body_iterator.aclose()


@pytest.mark.asyncio
async def test_last_canceled_stream_waiter_cancels_account_preparation(settings):
    started = asyncio.Event()
    canceled = asyncio.Event()

    class BlockingRunner(FakeRunner):
        async def download(self, url, quality, output, temporary, *, media_user_token):
            started.set()
            try:
                await asyncio.Future()
            finally:
                canceled.set()

    app = create_app(settings, FakeWrapper(), BlockingRunner())
    route = next(route for route in app.routes if getattr(route, "path", None) == "/api/stream/{song_id}" and "GET" in route.methods)
    response = await route.endpoint("101", "aac", ACCOUNT)
    assert await anext(response.body_iterator) == FLAC_GUIDANCE_PREFIX
    preparation = asyncio.create_task(anext(response.body_iterator))
    await asyncio.wait_for(started.wait(), 1)
    preparation.cancel()
    with pytest.raises(asyncio.CancelledError):
        await preparation
    assert canceled.is_set()


@pytest.mark.asyncio
@pytest.mark.parametrize("result_code", [0, 1, 78, 79])
async def test_runner_cookie_is_private_exact_and_removed_on_completion(settings, tmp_path, result_code):
    from apple_gateway.runner import ProcessResult

    class CapturingRunner(BoundedProcessRunner):
        async def execute(self, argv, cwd):
            self.cookie = Path(argv[argv.index("--cookies-path") + 1])
            self.argv = argv
            assert self.cookie.parent == cwd
            assert stat.S_IMODE(self.cookie.stat().st_mode) == 0o600
            assert self.cookie.read_text() == (
                "# Netscape HTTP Cookie File\n"
                ".music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\tselected-fixture-token\n"
            )
            assert "selected-fixture-token" not in argv
            assert "--use-wrapper" in argv
            assert "apple_gateway.account_cli" in argv
            output = Path(argv[argv.index("--output-path") + 1])
            (output / "fixture.m4a").write_bytes(b"audio")
            return ProcessResult(result_code, "", "")

    shared = tmp_path / "operator.cookies"
    shared.write_text("must-not-be-used")
    runner = CapturingRunner(replace(settings, cookies_path=shared))
    operation = runner.download(
        "https://music.apple.com/us/song/101", "alac", tmp_path / "output", tmp_path / "temporary",
        media_user_token="selected-fixture-token",
    )
    if result_code == 0:
        assert len(await operation) == 1
    else:
        expected = {1: "gamdl_failed", 78: "account_mismatch", 79: "account_unavailable"}[result_code]
        with pytest.raises(ProcessFailure, match=expected):
            await operation
    assert not runner.cookie.exists()
    assert str(shared) not in runner.argv
    assert shared.read_text() == "must-not-be-used"


@pytest.mark.asyncio
async def test_runner_removes_cookie_after_cancellation(settings, tmp_path):
    entered = asyncio.Event()

    class BlockingRunner(BoundedProcessRunner):
        async def execute(self, argv, cwd):
            self.cookie = Path(argv[argv.index("--cookies-path") + 1])
            assert self.cookie.is_file()
            entered.set()
            await asyncio.Future()

    runner = BlockingRunner(settings)
    task = asyncio.create_task(runner.download(
        "https://music.apple.com/us/song/101", "alac", tmp_path / "output", tmp_path / "temporary",
        media_user_token="cancel-fixture-token",
    ))
    await asyncio.wait_for(entered.wait(), 1)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert not runner.cookie.exists()


@pytest.mark.asyncio
async def test_runner_concurrent_accounts_get_distinct_cookie_files(settings, tmp_path):
    from apple_gateway.runner import ProcessResult
    cookies = {}
    both_entered = asyncio.Event()

    class CapturingRunner(BoundedProcessRunner):
        async def execute(self, argv, cwd):
            cookie = Path(argv[argv.index("--cookies-path") + 1])
            cookies[cookie] = cookie.read_text().splitlines()[-1].split("\t")[-1]
            if len(cookies) == 2:
                both_entered.set()
            await asyncio.wait_for(both_entered.wait(), 1)
            assert cookie.is_file()
            output = Path(argv[argv.index("--output-path") + 1])
            (output / "fixture.m4a").write_bytes(b"audio")
            return ProcessResult(0, "", "")

    runner = CapturingRunner(settings)
    await asyncio.gather(*(runner.download(
        "https://music.apple.com/us/song/101", "alac", tmp_path / f"out-{index}", tmp_path / f"tmp-{index}",
        media_user_token=token,
    ) for index, token in enumerate(("first-fixture-token", "second-fixture-token"))))
    assert set(cookies.values()) == {"first-fixture-token", "second-fixture-token"}
    assert len(cookies) == 2
    assert all(not path.exists() for path in cookies)


@pytest.mark.asyncio
async def test_runner_cancellation_reaps_subprocess(settings, tmp_path):
    pid_file = tmp_path / "child.pid"
    program = f"import os,time; open({str(pid_file)!r}, 'w').write(str(os.getpid())); time.sleep(30)"
    runner = BoundedProcessRunner(settings)
    task = asyncio.create_task(runner.execute([sys.executable, "-c", program], tmp_path))
    for _ in range(100):
        if pid_file.is_file():
            break
        await asyncio.sleep(0.01)
    assert pid_file.is_file()
    pid = int(pid_file.read_text())
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    with pytest.raises(ProcessLookupError):
        os.kill(pid, 0)
