from __future__ import annotations

import asyncio
import importlib
import socket
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from apple_gateway.account_cli import (
    ACCOUNT_MISMATCH_EXIT,
    ACCOUNT_UNAVAILABLE_EXIT,
    AccountFailure,
    AppleMusicApi,
    account_api_type,
    run,
)


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("account CLI fixtures must not use the network")
    monkeypatch.setattr(socket.socket, "connect", forbidden)
    monkeypatch.setattr(socket, "create_connection", forbidden)


def api(identity):
    return SimpleNamespace(
        account_info={"data": [{"id": identity}]} if identity is not None else {},
        client=SimpleNamespace(aclose=AsyncMock()),
        active_subscription=True,
        account_restrictions=None,
    )


@pytest.mark.asyncio
async def test_account_factory_retains_selected_api_and_closes_comparison(monkeypatch, tmp_path):
    selected = api("same-fixture-account")
    comparison = api("same-fixture-account")
    cookies = tmp_path / "account.cookies"
    selected_factory = AsyncMock(return_value=selected)
    comparison_factory = AsyncMock(return_value=comparison)
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", selected_factory)
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", comparison_factory)
    wrapper = object()
    failures = []

    actual = await account_api_type(cookies, failures).create_from_wrapper(wrapper, language="en-US")

    assert actual is selected
    selected_factory.assert_awaited_once_with(cookies_path=str(cookies), language="en-US")
    comparison_factory.assert_awaited_once_with(wrapper, language="en-US")
    comparison.client.aclose.assert_awaited_once()
    selected.client.aclose.assert_not_awaited()
    assert failures == []


@pytest.mark.asyncio
@pytest.mark.parametrize("selected_id,wrapper_id", [
    ("selected-fixture-account", "different-fixture-account"),
    (None, "fixture-account"),
    ("fixture-account", None),
    ("", ""),
])
async def test_account_factory_missing_or_mismatched_identity_fails_closed(
    monkeypatch, tmp_path, selected_id, wrapper_id
):
    selected = api(selected_id)
    comparison = api(wrapper_id)
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", AsyncMock(return_value=selected))
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(return_value=comparison))
    failures = []

    with pytest.raises(AccountFailure, match="^account_mismatch$") as failure:
        await account_api_type(tmp_path / "account.cookies", failures).create_from_wrapper(object())

    assert failure.value.__cause__ is None
    assert "fixture-account" not in str(failure.value)
    assert failures == ["account_mismatch"]
    selected.client.aclose.assert_awaited_once()
    comparison.client.aclose.assert_awaited_once()


@pytest.mark.asyncio
async def test_account_factory_redacts_upstream_failure_and_closes_selected_api(monkeypatch, tmp_path):
    selected = api("fixture-account")
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", AsyncMock(return_value=selected))
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(
        side_effect=RuntimeError("private-token private-id private-response")
    ))
    failures = []

    with pytest.raises(AccountFailure, match="^account_unavailable$") as failure:
        await account_api_type(tmp_path / "account.cookies", failures).create_from_wrapper(object())

    assert failure.value.__cause__ is None
    assert failures == ["account_unavailable"]
    selected.client.aclose.assert_awaited_once()


@pytest.mark.asyncio
async def test_account_factory_cancellation_closes_selected_client(monkeypatch, tmp_path):
    selected = api("fixture-account")
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", AsyncMock(return_value=selected))
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(side_effect=asyncio.CancelledError()))
    failures = []

    with pytest.raises(asyncio.CancelledError):
        await account_api_type(tmp_path / "account.cookies", failures).create_from_wrapper(object())

    selected.client.aclose.assert_awaited_once()
    assert failures == []


def argv(tmp_path):
    cookie = tmp_path / "account.cookies"
    cookie.write_text("# Netscape HTTP Cookie File\n")
    return [
        "--no-config-file", "--config-path", str(tmp_path / "unused-config.ini"),
        "--no-exceptions", "--use-wrapper", "--wrapper-url", "http://wrapper-fixture",
        "--wrapper-decrypt-host", "wrapper-fixture", "--wrapper-decrypt-port", "10020",
        "--cookies-path", str(cookie), "--output-path", str(tmp_path / "output"),
        "--temp-path", str(tmp_path), "--song-codec-priority", "alac",
        "--artist-auto-select", "all-albums", "https://music.apple.com/us/song/101",
    ]


def test_actual_gamdl_cli_uses_selected_api_at_interface_boundary(monkeypatch, tmp_path, capsys):
    cli = importlib.import_module("gamdl.cli.cli")
    original = cli.AppleMusicApi
    selected = api("same-fixture-account")
    comparison = api("same-fixture-account")
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", AsyncMock(return_value=selected))
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(return_value=comparison))
    monkeypatch.setattr(cli.WrapperApi, "create", AsyncMock(return_value=object()))
    seen = []

    class ReachedMediaBoundary(BaseException):
        pass

    async def interface_factory(**kwargs):
        seen.append(kwargs["apple_music_api"])
        raise ReachedMediaBoundary()

    monkeypatch.setattr(cli.AppleMusicBaseInterface, "create", interface_factory)

    with pytest.raises(ReachedMediaBoundary):
        run(argv(tmp_path))

    assert seen == [selected]
    comparison.client.aclose.assert_awaited_once()
    assert cli.AppleMusicApi is original
    assert capsys.readouterr().out == ""


def test_actual_gamdl_cli_mismatch_cannot_reach_media_interface(monkeypatch, tmp_path, capsys):
    cli = importlib.import_module("gamdl.cli.cli")
    original = cli.AppleMusicApi
    selected = api("selected-fixture-account")
    comparison = api("wrapper-fixture-account")
    monkeypatch.setattr(AppleMusicApi, "create_from_netscape_cookies", AsyncMock(return_value=selected))
    monkeypatch.setattr(AppleMusicApi, "create_from_wrapper", AsyncMock(return_value=comparison))
    monkeypatch.setattr(cli.WrapperApi, "create", AsyncMock(return_value=object()))
    interface_factory = AsyncMock(side_effect=AssertionError("mismatch reached media"))
    monkeypatch.setattr(cli.AppleMusicBaseInterface, "create", interface_factory)

    assert run(argv(tmp_path)) == ACCOUNT_MISMATCH_EXIT

    interface_factory.assert_not_awaited()
    selected.client.aclose.assert_awaited_once()
    comparison.client.aclose.assert_awaited_once()
    assert cli.AppleMusicApi is original
    output = capsys.readouterr()
    assert output.out == ""
    assert output.err == ""


def test_account_cli_rejects_missing_cookie_path_before_upstream_call(monkeypatch):
    cli = importlib.import_module("gamdl.cli.cli")
    monkeypatch.setattr(cli.main, "main", lambda *args, **kwargs: pytest.fail("must not call GAMDL"))
    assert run(["--use-wrapper", "--no-config-file"]) == ACCOUNT_UNAVAILABLE_EXIT
