from __future__ import annotations

import re
from pathlib import Path
from typing import Any

from .models import MediaAccount

SECRET_KEY = re.compile(r"(authorization|cookie|password|secret|session|token)", re.IGNORECASE)
APPLE_ID = re.compile(r"^[0-9]{1,24}$")
STOREFRONT = re.compile(r"^[a-z]{2}$")
ACCOUNT_SCOPE = re.compile(r"^[0-9a-f]{64}$")


def sanitize_json(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: sanitize_json(item) for key, item in value.items() if not SECRET_KEY.search(str(key))}
    if isinstance(value, list):
        return [sanitize_json(item) for item in value]
    return value


def song_url(storefront: str, song_id: str) -> str:
    if not STOREFRONT.fullmatch(storefront) or not APPLE_ID.fullmatch(song_id):
        raise ValueError("invalid_song_id")
    return f"https://music.apple.com/{storefront}/song/{song_id}"


def valid_media_token(token: str) -> bool:
    return 1 <= len(token) <= 16384 and all(
        33 <= ord(character) <= 126 and character not in ";," for character in token
    )


def media_account(token: str, storefront: str, scope: str) -> MediaAccount:
    if (
        not valid_media_token(token)
        or not STOREFRONT.fullmatch(storefront)
        or not ACCOUNT_SCOPE.fullmatch(scope)
    ):
        raise ValueError("account_context_required")
    return MediaAccount(token, storefront, scope)


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
