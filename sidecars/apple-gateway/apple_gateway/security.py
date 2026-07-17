from __future__ import annotations

import re
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit, urlunsplit


SECRET_KEY = re.compile(r"(authorization|cookie|password|secret|session|token)", re.IGNORECASE)
APPLE_ID = re.compile(r"^[0-9]{1,24}$")
SUPPORTED_APPLE_PATH = re.compile(
    r"^/(?:[a-z]{2}/)?(?:album|song|playlist|artist|music-video|post|library)(?:/|$)",
    re.IGNORECASE,
)


def sanitize_json(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: sanitize_json(item) for key, item in value.items() if not SECRET_KEY.search(str(key))}
    if isinstance(value, list):
        return [sanitize_json(item) for item in value]
    return value


def safe_apple_url(raw: str) -> tuple[str, str]:
    parsed = urlsplit(raw.strip())
    if (
        parsed.scheme != "https"
        or parsed.hostname != "music.apple.com"
        or parsed.username
        or parsed.password
        or parsed.fragment
        or not SUPPORTED_APPLE_PATH.match(parsed.path)
    ):
        raise ValueError("unsupported_apple_music_url")
    sanitized = urlunsplit(("https", "music.apple.com", parsed.path, parsed.query, ""))
    segment = next((part for part in parsed.path.lower().split("/") if part in {
        "album", "song", "playlist", "artist", "music-video", "post", "library"
    }), "catalog")
    return sanitized, segment


def song_url(storefront: str, song_id: str) -> str:
    if not APPLE_ID.fullmatch(song_id):
        raise ValueError("invalid_song_id")
    return f"https://music.apple.com/{storefront}/song/{song_id}"


def safe_files(root: Path, suffixes: set[str]) -> list[Path]:
    resolved_root = root.resolve()
    result: list[Path] = []
    for candidate in root.rglob("*"):
        if candidate.is_symlink() or not candidate.is_file() or candidate.suffix.lower() not in suffixes:
            continue
        resolved = candidate.resolve()
        if not resolved.is_relative_to(resolved_root):
            continue
        result.append(resolved)
    return sorted(result, key=lambda item: item.as_posix())
