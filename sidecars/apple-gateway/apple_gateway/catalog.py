from __future__ import annotations

from typing import Any

import httpx


class CatalogClient:
    """Small metadata seam backed by Apple's public iTunes Search API."""

    def __init__(self, storefront: str, client: httpx.AsyncClient | None = None):
        self._storefront = storefront.upper()
        self._owns_client = client is None
        self._client = client or httpx.AsyncClient(
            base_url="https://itunes.apple.com/",
            timeout=httpx.Timeout(10),
            follow_redirects=False,
            trust_env=False,
        )

    async def close(self) -> None:
        if self._owns_client:
            await self._client.aclose()

    async def search_songs(self, query: str, limit: int) -> list[dict[str, Any]]:
        response = await self._client.get("search", params={
            "term": query,
            "country": self._storefront,
            "media": "music",
            "entity": "song",
            "limit": limit,
        })
        response.raise_for_status()
        return [self._map(item) for item in response.json().get("results", []) if item.get("trackId")]

    async def song(self, song_id: str) -> dict[str, Any] | None:
        item = await self._song(song_id)
        return self._map(item) if item else None

    async def song_url(self, song_id: str) -> str | None:
        item = await self._song(song_id)
        return str(item.get("trackViewUrl") or "") or None if item else None

    async def _song(self, song_id: str) -> dict[str, Any] | None:
        response = await self._client.get("lookup", params={"id": song_id, "country": self._storefront})
        response.raise_for_status()
        return next((item for item in response.json().get("results", []) if item.get("trackId")), None)

    @staticmethod
    def _map(item: dict[str, Any]) -> dict[str, Any]:
        artwork = str(item.get("artworkUrl100") or "").replace("100x100", "1200x1200")
        release = item.get("releaseDate")
        return {
            "id": str(item["trackId"]),
            "title": str(item.get("trackName") or "Unknown title"),
            "artist": str(item.get("artistName") or "Unknown artist"),
            "album": str(item.get("collectionName") or "Unknown album"),
            "duration": max(0, int(item.get("trackTimeMillis") or 0) // 1000),
            "cover_url": artwork,
            "track_number": item.get("trackNumber"),
            "disc_number": item.get("discNumber"),
            "isrc": item.get("isrc"),
            "release_date": release[:10] if isinstance(release, str) and len(release) >= 10 else None,
            "copyright": item.get("copyright"),
            "composer": None,
            "genre": item.get("primaryGenreName"),
        }
