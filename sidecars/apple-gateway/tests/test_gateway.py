from __future__ import annotations

import asyncio
import json
import sys
from dataclasses import replace
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient

from apple_gateway.app import API_VERSION, create_app
from apple_gateway.catalog import CatalogClient
from apple_gateway.config import Settings
from apple_gateway.runner import BoundedProcessRunner, ProcessFailure
from apple_gateway.wrapper import WrapperClient, WrapperResponse


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


class FakeCatalog:
    async def close(self) -> None:
        return None

    async def search_songs(self, query: str, limit: int) -> list[dict[str, Any]]:
        return [song("101", query)][:limit]

    async def song(self, song_id: str) -> dict[str, Any] | None:
        return None if song_id == "404" else song(song_id, "Fixture")


class FakeRunner:
    def __init__(self):
        self.calls: list[tuple[str, str]] = []
        self.transcodes: list[str] = []

    async def download(self, url: str, quality: str, output: Path, temporary: Path) -> list[Path]:
        self.calls.append((url, quality))
        output.mkdir(parents=True, exist_ok=False)
        temporary.mkdir(parents=True, exist_ok=False)
        artifact = output / "fixture.m4a"
        artifact.write_bytes(b"source")
        lyrics = output / "fixture.lrc"
        lyrics.write_text("[00:01.00]Fixture lyrics\n", encoding="utf-8")
        return [artifact, lyrics]

    async def to_flac(self, source: Path, target: Path) -> Path:
        self.transcodes.append("file")
        target.write_bytes(b"fLaCfixture")
        return target.resolve()

    async def stream_flac(self, source: Path):
        self.transcodes.append("stream")
        yield b"fLaC"
        yield b"fixture"


def song(song_id: str, title: str) -> dict[str, Any]:
    return {
        "id": song_id,
        "title": title,
        "artist": "Artist",
        "album": "Album",
        "duration": 123,
        "cover_url": "https://example.test/art.jpg",
        "track_number": 1,
        "disc_number": 1,
        "isrc": "USAAA0000001",
        "release_date": "2026-01-01",
        "copyright": None,
        "composer": None,
        "genre": "Pop",
    }


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
    app = create_app(settings, wrapper, FakeCatalog(), runner)
    with TestClient(app) as test_client:
        yield test_client, wrapper, runner


def test_capabilities_are_versioned_and_truthful(client):
    response = client[0].get("/api/capabilities")
    assert response.status_code == 200
    assert response.json()["sidecarApiVersion"] == API_VERSION
    ids = {item["id"] for item in response.json()["capabilities"]}
    assert {"metadata-search-song", "metadata-song", "download-audio-song", "synced-lyrics-artifact"} <= ids


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


def test_search_song_and_missing_song_contract(client):
    search = client[0].get("/api/search", params={"q": "Needle", "type": "song", "limit": 2})
    assert search.status_code == 200
    assert search.json()[0]["title"] == "Needle"
    assert client[0].get("/api/song/101").json()["id"] == "101"
    assert client[0].get("/api/song/404").status_code == 404


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
    assert streamed.content == b"fLaCfixture"
    assert client[2].transcodes == ["file", "stream"]
    assert client[2].calls[-1] == ("https://music.apple.com/us/song/102", "aac")
    assert client[0].get("/api/download/not-an-id").status_code == 400


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
    )
    host_index = runner.argv.index("--wrapper-decrypt-host")
    port_index = runner.argv.index("--wrapper-decrypt-port")
    assert runner.argv[host_index + 1] == "wrapper-v2"
    assert runner.argv[port_index + 1] == "18080"


def test_generic_catalog_and_library_download_jobs_are_bounded_to_apple_urls(client):
    accepted = client[0].post("/api/jobs/download", json={
        "url": "https://music.apple.com/us/album/fixture/123?i=101",
        "quality": "aac-web",
    })
    assert accepted.status_code == 202
    job_id = accepted.json()["id"]
    for _ in range(30):
        state = client[0].get(f"/api/jobs/download/{job_id}").json()
        if state["state"] == "succeeded":
            break
        asyncio.run(asyncio.sleep(0.01))
    assert state["artifact_count"] == 2

    library = client[0].post("/api/jobs/download", json={
        "url": "https://music.apple.com/library/playlist/p.ABC123",
        "quality": "alac",
    })
    assert library.status_code == 202
    assert client[0].post("/api/jobs/download", json={
        "url": "https://evil.example/album/123",
        "quality": "alac",
    }).status_code == 400


def test_terminal_download_job_is_persisted_and_rehydrated_after_restart(settings: Settings):
    runner = FakeRunner()
    first_app = create_app(settings, FakeWrapper(), FakeCatalog(), runner)
    with TestClient(first_app) as first_client:
        accepted = first_client.post("/api/jobs/download", json={
            "url": "https://music.apple.com/us/playlist/fixture/pl.123",
            "quality": "alac",
        })
        job_id = accepted.json()["id"]
        for _ in range(30):
            state = first_client.get(f"/api/jobs/download/{job_id}").json()
            if state["state"] == "succeeded":
                break
            asyncio.run(asyncio.sleep(0.01))
        assert state["state"] == "succeeded"

    state_file = settings.data_root / "jobs" / job_id / "job.json"
    persisted = json.loads(state_file.read_text(encoding="utf-8"))
    assert persisted["state"] == "succeeded"
    assert persisted["artifacts"] == ["artifacts/fixture.m4a", "artifacts/fixture.lrc"]
    assert not state_file.with_name("job.json.partial").exists()

    second_runner = FakeRunner()
    second_app = create_app(settings, FakeWrapper(), FakeCatalog(), second_runner)
    with TestClient(second_app) as second_client:
        restored = second_client.get(f"/api/jobs/download/{job_id}")
        assert restored.status_code == 200
        assert restored.json() == {
            "id": job_id,
            "state": "succeeded",
            "media_kind": "playlist",
                "artifact_count": 2,
            "error_code": None,
        }
    assert second_runner.calls == []


def test_restart_ignores_nonterminal_and_invalid_persisted_jobs(settings: Settings):
    settings.prepare()
    jobs_root = settings.data_root / "jobs"
    running_id = "a" * 32
    invalid_id = "b" * 32
    for job_id, state, artifacts in (
        (running_id, "running", []),
        (invalid_id, "succeeded", ["../../outside.m4a"]),
    ):
        root = jobs_root / job_id
        root.mkdir()
        (root / "job.json").write_text(json.dumps({
            "version": 1,
            "id": job_id,
            "media_kind": "album",
            "state": state,
            "artifacts": artifacts,
            "error_code": None,
        }), encoding="utf-8")

    app = create_app(settings, FakeWrapper(), FakeCatalog(), FakeRunner())
    with TestClient(app) as test_client:
        assert test_client.get(f"/api/jobs/download/{running_id}").status_code == 404
        assert test_client.get(f"/api/jobs/download/{invalid_id}").status_code == 404


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


@pytest.mark.asyncio
async def test_catalog_mapping_is_deterministic():
    async def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"results": [{
            "trackId": 101,
            "trackName": "Fixture",
            "artistName": "Artist",
            "collectionName": "Album",
            "trackTimeMillis": 123900,
            "artworkUrl100": "https://img/100x100.jpg",
            "releaseDate": "2026-01-02T00:00:00Z",
        }]})

    http_client = httpx.AsyncClient(base_url="https://itunes.apple.com/", transport=httpx.MockTransport(handler))
    catalog = CatalogClient("us", http_client)
    result = await catalog.search_songs("Fixture", 1)
    assert result[0]["duration"] == 123
    assert result[0]["cover_url"] == "https://img/1200x1200.jpg"
    await http_client.aclose()
