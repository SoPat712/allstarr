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

    async def album(self, album_id: str) -> dict[str, Any] | None:
        results = await self._lookup(album_id, entity="song", limit=200)
        item = next((item for item in results if item.get("collectionId")), None)
        if item is None:
            return None
        album = self._map_album(item)
        album["tracks"] = [self._map(track) for track in results if track.get("trackId")]
        return album

    async def artist(self, artist_id: str) -> dict[str, Any] | None:
        results = await self._lookup(artist_id, entity="album", limit=1)
        item = next((item for item in results if item.get("wrapperType") == "artist"), None)
        if item is None:
            return None
        artist = self._map_artist(item)
        first_album = next((item for item in results if item.get("collectionId")), None)
        artist["image_url"] = self._artwork(first_album) if first_album else ""
        return artist

    async def artist_albums(self, artist_id: str, limit: int) -> list[dict[str, Any]]:
        results = await self._lookup(artist_id, entity="album", limit=limit)
        return [self._map_album(item) for item in results if item.get("collectionId")]

    async def artist_tracks(self, artist_id: str, limit: int) -> list[dict[str, Any]]:
        results = await self._lookup(artist_id, entity="song", limit=limit)
        return [self._map(item) for item in results if item.get("trackId")]

    async def song_url(self, song_id: str) -> str | None:
        item = await self._song(song_id)
        return str(item.get("trackViewUrl") or "") or None if item else None

    async def _song(self, song_id: str) -> dict[str, Any] | None:
        results = await self._lookup(song_id)
        return next((item for item in results if item.get("trackId")), None)

    async def _lookup(
        self,
        resource_id: str,
        entity: str | None = None,
        limit: int | None = None,
    ) -> list[dict[str, Any]]:
        params: dict[str, str | int] = {"id": resource_id, "country": self._storefront}
        if entity:
            params["entity"] = entity
        if limit:
            params["limit"] = limit
        response = await self._client.get("lookup", params=params)
        response.raise_for_status()
        return response.json().get("results", [])

    @staticmethod
    def _map(item: dict[str, Any]) -> dict[str, Any]:
        artwork = CatalogClient._artwork(item)
        release = item.get("releaseDate")
        return {
            "id": str(item["trackId"]),
            "title": str(item.get("trackName") or "Unknown title"),
            "artist": str(item.get("artistName") or "Unknown artist"),
            "artist_id": str(item.get("artistId") or ""),
            "album": str(item.get("collectionName") or "Unknown album"),
            "album_id": str(item.get("collectionId") or ""),
            "duration": max(0, int(item.get("trackTimeMillis") or 0) // 1000),
            "cover_url": artwork,
            "track_number": item.get("trackNumber"),
            "disc_number": item.get("discNumber"),
            "total_tracks": item.get("trackCount"),
            "isrc": item.get("isrc"),
            "release_date": release[:10] if isinstance(release, str) and len(release) >= 10 else None,
            "copyright": item.get("copyright"),
            "composer": None,
            "genre": item.get("primaryGenreName"),
        }

    @staticmethod
    def _map_album(item: dict[str, Any]) -> dict[str, Any]:
        release = item.get("releaseDate")
        return {
            "id": str(item["collectionId"]),
            "title": str(item.get("collectionName") or "Unknown album"),
            "artist": str(item.get("artistName") or "Unknown artist"),
            "artist_id": str(item.get("artistId") or ""),
            "cover_url": CatalogClient._artwork(item),
            "release_date": release[:10] if isinstance(release, str) and len(release) >= 10 else None,
            "track_count": item.get("trackCount"),
            "genre": item.get("primaryGenreName"),
        }

    @staticmethod
    def _map_artist(item: dict[str, Any]) -> dict[str, Any]:
        return {
            "id": str(item["artistId"]),
            "name": str(item.get("artistName") or "Unknown artist"),
        }

    @staticmethod
    def _artwork(item: dict[str, Any]) -> str:
        return str(item.get("artworkUrl100") or "").replace("100x100", "1200x1200")
