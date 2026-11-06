from __future__ import annotations

import asyncio
import base64
import json
import socket
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from apple_gateway.sessions import AccountSessions, create_session
from test_gateway import settings


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("session fixtures must not use the network")
    monkeypatch.setattr(socket.socket, "connect", forbidden)


def session():
    return SimpleNamespace(expires_at=float("inf"), close=AsyncMock())


@pytest.mark.asyncio
async def test_warm_account_reuses_setup_but_separates_tokens_and_storefronts(tmp_path):
    factory = AsyncMock(side_effect=lambda *args: session())
    sessions = AccountSessions(tmp_path / "web-token", factory=factory)
    first = await sessions.get("first-token", "us")
    assert await sessions.get("first-token", "us") is first
    assert await sessions.get("other-token", "us") is not first
    assert await sessions.get("first-token", "gb") is not first
    assert factory.await_count == 3
    await sessions.close()
    first.close.assert_awaited_once()


@pytest.mark.asyncio
async def test_expired_session_is_closed_and_replaced(tmp_path):
    expired, refreshed = session(), session()
    factory = AsyncMock(side_effect=[expired, refreshed])
    sessions = AccountSessions(tmp_path / "web-token", factory=factory)
    assert await sessions.get("fixture-token", "us") is expired
    expired.expires_at = 0
    assert await sessions.get("fixture-token", "us") is refreshed
    expired.close.assert_awaited_once()
    await sessions.close()


@pytest.mark.asyncio
async def test_auth_failure_refreshes_session_and_public_token_once(tmp_path):
    token_path = tmp_path / "web-token"
    token_path.write_text("expired-token")
    first, refreshed = session(), session()
    factory = AsyncMock(side_effect=[first, refreshed])
    sessions = AccountSessions(token_path, factory=factory)
    error = httpx.HTTPStatusError("fixture", request=httpx.Request("GET", "https://fixture.invalid"),
        response=httpx.Response(401))
    operation = AsyncMock(side_effect=[error, "complete"])
    assert await sessions.prepare("fixture-token", "us", operation) == "complete"
    assert operation.call_args_list[0].args == (first,)
    assert operation.call_args_list[1].args == (refreshed,)
    assert not token_path.exists()
    first.close.assert_awaited_once()
    await sessions.close()


@pytest.mark.asyncio
async def test_auth_refresh_never_retries_forever_or_swallows_cancellation(tmp_path):
    sessions = AccountSessions(tmp_path / "web-token", factory=AsyncMock(side_effect=lambda *args: session()))
    error = httpx.HTTPStatusError("fixture", request=httpx.Request("GET", "https://fixture.invalid"),
        response=httpx.Response(403))
    operation = AsyncMock(side_effect=error)
    with pytest.raises(httpx.HTTPStatusError):
        await sessions.prepare("fixture-token", "us", operation)
    assert operation.await_count == 2
    canceled = AsyncMock(side_effect=asyncio.CancelledError())
    with pytest.raises(asyncio.CancelledError):
        await sessions.prepare("fixture-token", "us", canceled)
    assert canceled.await_count == 1
    await sessions.close()


@pytest.mark.asyncio
async def test_simultaneous_setup_for_one_account_creates_one_session(tmp_path):
    entered, release = asyncio.Event(), asyncio.Event()

    async def create(*args):
        entered.set()
        await release.wait()
        return session()

    factory = AsyncMock(side_effect=create)
    sessions = AccountSessions(tmp_path / "web-token", factory=factory)
    first = asyncio.create_task(sessions.get("fixture-token", "us"))
    await entered.wait()
    second = asyncio.create_task(sessions.get("fixture-token", "us"))
    release.set()
    assert await first is await second
    factory.assert_awaited_once()
    await sessions.close()


@pytest.mark.asyncio
@pytest.mark.parametrize("mismatch", [False, True])
async def test_session_creation_binds_selected_identity_and_closes_unused_clients(settings, monkeypatch, mismatch):
    from gamdl.api import AppleMusicApi
    from gamdl.api.wrapper import WrapperApi
    from gamdl.interface import AppleMusicBaseInterface
    from apple_gateway.account_cli import AccountFailure

    def api(identity):
        return SimpleNamespace(account_info={"data": [{"id": identity}]}, active_subscription=True,
            storefront="us", client=SimpleNamespace(aclose=AsyncMock()))

    selected, comparison = api("selected"), api("different" if mismatch else "selected")
    wrapper = SimpleNamespace(client=SimpleNamespace(aclose=AsyncMock()))
    base = SimpleNamespace(apple_music_api=selected, wrapper_api=wrapper,
        itunes_api=SimpleNamespace(client=SimpleNamespace(aclose=AsyncMock())))
    token = "fixture." + base64.urlsafe_b64encode(json.dumps({"exp": time.time() + 7200}).encode()).decode() + ".fixture"
    monkeypatch.setattr(AppleMusicApi, "get_token", AsyncMock(return_value=token))
    selected_factory = AsyncMock(return_value=selected)
    monkeypatch.setattr(AppleMusicApi, "create", selected_factory)
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(return_value=comparison))
    wrapper_factory = AsyncMock(return_value=wrapper)
    monkeypatch.setattr(WrapperApi, "create", wrapper_factory)
    base_factory = AsyncMock(return_value=base)
    monkeypatch.setattr(AppleMusicBaseInterface, "create", base_factory)
    if mismatch:
        with pytest.raises(AccountFailure, match="account_mismatch"):
            await create_session(settings, "selected-token", "us")
        selected.client.aclose.assert_awaited_once()
        wrapper.client.aclose.assert_awaited_once()
        base_factory.assert_not_awaited()
    else:
        result = await create_session(settings, "selected-token", "us")
        selected_factory.assert_awaited_once_with(token=token, media_user_token="selected-token", storefront="us")
        assert "get_credentials_func" not in wrapper_factory.call_args.kwargs
        selected.client.aclose.assert_not_awaited()
        await result.close()
        selected.client.aclose.assert_awaited_once()
        wrapper.client.aclose.assert_awaited_once()
    comparison.client.aclose.assert_awaited_once()
