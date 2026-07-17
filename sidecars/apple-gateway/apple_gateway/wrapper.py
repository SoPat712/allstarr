from __future__ import annotations

from typing import Any

import httpx

from .security import sanitize_json


class WrapperResponse:
    def __init__(self, status_code: int, payload: Any):
        self.status_code = status_code
        self.payload = payload


class WrapperClient:
    def __init__(self, base_url: str, timeout_seconds: int, client: httpx.AsyncClient | None = None):
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(
            base_url=f"{base_url}/",
            timeout=httpx.Timeout(timeout_seconds),
            follow_redirects=False,
            trust_env=False,
        )

    async def close(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    async def request(self, method: str, path: str, payload: dict[str, str] | None = None) -> WrapperResponse:
        try:
            response = await self._client.request(method, path.lstrip("/"), json=payload)
        except (httpx.TimeoutException, httpx.NetworkError):
            return WrapperResponse(503, {"error": "wrapper_unavailable"})
        if response.is_redirect:
            return WrapperResponse(502, {"error": "wrapper_redirect_rejected"})
        try:
            body: Any = response.json()
        except ValueError:
            body = {"error": "invalid_wrapper_response"}
        return WrapperResponse(response.status_code, sanitize_json(body))

    async def health(self) -> WrapperResponse:
        return await self.request("GET", "health")

    async def me(self) -> WrapperResponse:
        return await self.request("GET", "me")

    async def login(self, username: str, password: str) -> WrapperResponse:
        return await self.request("POST", "login", {"username": username, "password": password})

    async def login_2fa(self, code: str) -> WrapperResponse:
        return await self.request("POST", "login/2fa", {"code": code})
