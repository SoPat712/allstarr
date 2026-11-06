from __future__ import annotations

import asyncio
import base64
import contextlib
import hashlib
import json
import time
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Awaitable, Callable

import httpx

from .account_cli import AccountFailure, _account_id, cached_web_token, save_web_token
from .config import Settings


def authentication_error(exc: Exception) -> bool:
    status = getattr(exc, "status_code", None)
    if isinstance(exc, httpx.HTTPStatusError):
        status = exc.response.status_code
    return status in {401, 403}


@dataclass
class AccountSession:
    base: Any
    http: httpx.AsyncClient
    expires_at: float

    async def close(self) -> None:
        for client in (self.base.apple_music_api.client, self.base.wrapper_api.client,
                self.base.itunes_api.client, self.http):
            with contextlib.suppress(Exception):
                await client.aclose()


async def create_session(settings: Settings, token: str, storefront: str) -> AccountSession:
    from gamdl.api import AppleMusicApi
    from gamdl.api.wrapper import WrapperApi
    from gamdl.interface import AppleMusicBaseInterface

    token_path = settings.data_root / "web-token"
    public_token = cached_web_token(token_path)
    if public_token is None:
        public_token = await AppleMusicApi.get_token()
        save_web_token(token_path, public_token)
    wrapper = selected = comparison = base = None
    tasks = []
    accepted = False
    try:
        # Never supply login callbacks: only the explicitly selected account may be used.
        wrapper = await WrapperApi.create(base_url=settings.wrapper_url,
            decrypt_host=settings.wrapper_decrypt_host, decrypt_port=settings.wrapper_decrypt_port)
        tasks = [asyncio.create_task(AppleMusicApi.create(token=public_token, media_user_token=token,
                storefront=storefront)),
            asyncio.create_task(AppleMusicApi.create_from_wrapper(wrapper))]
        await asyncio.gather(*tasks)
        selected, comparison = (task.result() for task in tasks)
        if not _account_id(selected) or _account_id(selected) != _account_id(comparison):
            raise AccountFailure("account_mismatch")
        if selected.storefront.lower() != storefront or not selected.active_subscription:
            raise AccountFailure("account_unavailable")
        base = await AppleMusicBaseInterface.create(apple_music_api=selected, wrapper_api=wrapper)
        expires = time.time() + 900
        with contextlib.suppress(ValueError, IndexError, TypeError, AttributeError):
            claims = public_token.split(".")[1]
            expires = min(expires, float(json.loads(base64.urlsafe_b64decode(
                claims + "=" * (-len(claims) % 4)))["exp"]) - 3600)
        result = AccountSession(base, httpx.AsyncClient(timeout=settings.wrapper_timeout_seconds,
            follow_redirects=False, trust_env=False), expires)
        accepted = True
        return result
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        for index, task in enumerate(tasks):
            if task.cancelled() or task.exception() is not None or index == 0 and accepted:
                continue
            with contextlib.suppress(Exception):
                await task.result().client.aclose()
        if not accepted:
            for client in (getattr(wrapper, "client", None), getattr(getattr(base, "itunes_api", None), "client", None)):
                if client is not None:
                    with contextlib.suppress(Exception):
                        await client.aclose()


class AccountSessions:
    def __init__(self, token_path: Path, *, factory: Callable[[str, str], Awaitable[Any]]):
        self._token_path = token_path
        self._factory = factory
        self._sessions: OrderedDict[str, Any] = OrderedDict()
        self._lock = asyncio.Lock()

    @staticmethod
    def _key(token: str, storefront: str) -> str:
        return hashlib.sha256(f"{token}\n{storefront}".encode()).hexdigest()

    async def discard(self, token: str, storefront: str) -> None:
        async with self._lock:
            if session := self._sessions.pop(self._key(token, storefront), None):
                await session.close()

    async def get(self, token: str, storefront: str):
        async with self._lock:
            key = self._key(token, storefront)
            if cached := self._sessions.get(key):
                if cached.expires_at > time.time():
                    self._sessions.move_to_end(key)
                    return cached
                self._sessions.pop(key)
                await cached.close()
            while len(self._sessions) >= 8:
                _, oldest = self._sessions.popitem(last=False)
                await oldest.close()
            session = await self._factory(token, storefront)
            self._sessions[key] = session
            return session

    async def prepare(self, token: str, storefront: str, operation):
        for attempt in range(2):
            try:
                return await operation(await self.get(token, storefront))
            except Exception as exc:
                if not authentication_error(exc):
                    raise
                await self.discard(token, storefront)
                self._token_path.unlink(missing_ok=True)
                if attempt:
                    raise

    async def close(self) -> None:
        async with self._lock:
            for session in self._sessions.values():
                await session.close()
            self._sessions.clear()
