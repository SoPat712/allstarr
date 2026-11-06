from __future__ import annotations

import socket
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest
from gamdl.interface import AppleMusicBaseInterface, AppleMusicSongInterface
from gamdl.interface.enums import CoverFormat
from gamdl.interface.types import StreamInfo, StreamInfoAv

from apple_gateway.sessions import AccountSessions
from apple_gateway.worker import prepare


WORD_TTML = (Path(__file__).parent / "fixtures" / "word-lyrics.ttml").read_text()
LINE_TTML = "<tt><body><p begin='1' end='3'>Sample sunshine</p></body></tt>"


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("preparation fixtures must not use the network")
    monkeypatch.setattr(socket.socket, "connect", forbidden)


@pytest.mark.asyncio
async def test_preparation_reuses_account_clients_and_keeps_song_state_separate(tmp_path, monkeypatch):
    artwork_started = []

    async def send(request):
        artwork_started.append(request.url.path)
        return httpx.Response(200, content=b"fixture-cover")

    async def catalog(song_id):
        return {"data": [{"id": song_id, "attributes": {
            "playParams": {"id": song_id}, "hasLyrics": False, "durationInMillis": 2500,
            "artwork": {"url": f"https://fixture.invalid/{song_id}/{{w}}x{{h}}bb.jpg"}}}]}

    async def playback(song_id):
        return {"songList": [{"assets": [{"metadata": {"itemId": song_id, "itemName": f"Title {song_id}",
            "artistName": "Fixture", "playlistName": "Fixture album", "sort-artist": "Fixture",
            "sort-name": song_id, "kind": "song", "explicit": 0}}]}]}

    apple = SimpleNamespace(get_song=AsyncMock(side_effect=catalog), get_webplayback=AsyncMock())
    wrapper = SimpleNamespace(get_playback=AsyncMock(side_effect=playback))
    base = AppleMusicBaseInterface(apple, SimpleNamespace(), wrapper, CoverFormat.JPG, 1200, object())
    stream_info = AsyncMock(side_effect=lambda *args: StreamInfoAv(audio_track=StreamInfo(drm_free=True)))
    monkeypatch.setattr(AppleMusicSongInterface, "get_stream_info", stream_info)
    original_catalog, original_cover = apple.get_song, base.get_cover
    async with httpx.AsyncClient(transport=httpx.MockTransport(send)) as http:
        session = SimpleNamespace(base=base, http=http, expires_at=float("inf"), close=AsyncMock())
        factory = AsyncMock(return_value=session)
        sessions = AccountSessions(tmp_path / "web-token", factory=factory)
        request = {"song_id": "101", "quality": "alac", "token": "fixture-token", "storefront": "us",
            "ceiling": [16, 44100]}
        first, first_base, _ = await prepare(sessions, request)
        second, second_base, report = await prepare(sessions, {**request, "song_id": "102"})
        factory.assert_awaited_once_with("fixture-token", "us")
        assert first.tags.title == "Title 101" and second.tags.title == "Title 102"
        assert first.cover_bytes == second.cover_bytes == b"fixture-cover"
        assert len(artwork_started) == 2
        assert first_base is not second_base and first_base is not base
        assert apple.get_song is original_catalog and base.get_cover == original_cover
        assert first_base.http is not second_base.http
        assert first_base.http.http is second_base.http.http is http
        assert report["phases"][0]["phase"] == "setup"
        assert {phase["phase"] for phase in report["phases"]} >= {"catalog", "playback", "artwork", "manifest"}
        await sessions.close()


@pytest.mark.asyncio
async def test_lyrics_preserve_raw_ttml_word_spans():
    from apple_gateway.worker import prepare_lyrics

    ttml = "<tt><body><p begin='1'><span begin='1' end='2'>Synthetic</span></p></body></tt>"
    apple = SimpleNamespace(get_song=AsyncMock(return_value={"data": [{
        "relationships": {"lyrics": {"data": [{"attributes": {"ttml": ttml}}]}}
    }]}))

    class Sessions:
        async def prepare(self, token, storefront, operation):
            return await operation(SimpleNamespace(base=SimpleNamespace(apple_music_api=apple)))

    assert await prepare_lyrics(Sessions(), {"song_id": "103", "token": "fixture", "storefront": "us"}) == ttml


@pytest.mark.asyncio
@pytest.mark.parametrize("syllable", ["present", "missing", "404", "403", "empty", "blank", "timeout", "invalid"])
async def test_lyrics_prefer_syllables_and_fall_back_to_catalog(syllable):
    from apple_gateway.worker import prepare_lyrics

    requests = []

    async def send(request):
        requests.append(request)
        if syllable in {"404", "403"}:
            return httpx.Response(int(syllable))
        if syllable == "timeout":
            raise httpx.ReadTimeout("fixture", request=request)
        if syllable == "invalid":
            return httpx.Response(200, text="invalid")
        return httpx.Response(200, json={"data": [] if syllable == "missing" else [
            {"attributes": {"ttml": WORD_TTML if syllable == "present" else "  " if syllable == "blank" else ""}}]})

    async with httpx.AsyncClient(transport=httpx.MockTransport(send),
            headers={"authorization": "Bearer fixture-public-token"}) as http:
        apple = SimpleNamespace(client=http, language="en-US", get_song=AsyncMock(return_value={"data": [{
            "relationships": {"lyrics": {"data": [{"attributes": {"ttml": LINE_TTML}}]}}
        }]}))

        class Sessions:
            async def prepare(self, token, storefront, operation):
                assert (token, storefront) == ("fixture-token", "us")
                return await operation(SimpleNamespace(base=SimpleNamespace(apple_music_api=apple)))

        assert await prepare_lyrics(Sessions(), {"song_id": "103", "token": "fixture-token", "storefront": "us"}) == (
            WORD_TTML if syllable == "present" else LINE_TTML)
        assert len(requests) == 1
        assert str(requests[0].url) == "https://amp-api.music.apple.com/v1/catalog/us/songs/103/syllable-lyrics?l=en-US"
        assert requests[0].headers["music-user-token"] == "fixture-token"
        assert requests[0].headers["authorization"] == "Bearer fixture-public-token"
        if syllable == "present":
            apple.get_song.assert_not_awaited()
        else:
            apple.get_song.assert_awaited_once_with("103")
