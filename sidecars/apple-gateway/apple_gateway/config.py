from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit


def _positive_int(name: str, default: int, minimum: int, maximum: int) -> int:
    raw = os.getenv(name, str(default))
    try:
        value = int(raw)
    except ValueError as exc:
        raise RuntimeError(f"{name} must be an integer") from exc
    if not minimum <= value <= maximum:
        raise RuntimeError(f"{name} must be between {minimum} and {maximum}")
    return value


def _http_url(name: str, default: str) -> str:
    value = os.getenv(name, default).rstrip("/")
    parsed = urlsplit(value)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
        raise RuntimeError(f"{name} must be an HTTP(S) origin without credentials")
    if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
        raise RuntimeError(f"{name} must not contain a path, query, or fragment")
    return value


def _host(name: str, default: str) -> str:
    value = os.getenv(name, default).strip()
    if not value or any(character in value for character in "/:@?#[]"):
        raise RuntimeError(f"{name} must be a hostname or address without a port")
    return value


@dataclass(frozen=True, slots=True)
class Settings:
    wrapper_url: str
    wrapper_decrypt_host: str
    wrapper_decrypt_port: int
    data_root: Path
    cookies_path: Path | None
    storefront: str
    gamdl_path: str
    ffmpeg_path: str
    subprocess_timeout_seconds: int
    wrapper_timeout_seconds: int
    max_concurrency: int
    max_process_output_bytes: int

    @classmethod
    def from_env(cls) -> "Settings":
        root = Path(os.getenv("APPLE_GATEWAY_DATA_ROOT", "/data")).expanduser().resolve()
        cookies = os.getenv("APPLE_GATEWAY_COOKIES_PATH", "").strip()
        storefront = os.getenv("APPLE_GATEWAY_STOREFRONT", "us").strip().lower()
        if len(storefront) != 2 or not storefront.isascii() or not storefront.isalpha():
            raise RuntimeError("APPLE_GATEWAY_STOREFRONT must be a two-letter storefront")
        return cls(
            wrapper_url=_http_url("APPLE_GATEWAY_WRAPPER_URL", "http://wrapper-v2"),
            wrapper_decrypt_host=_host("APPLE_GATEWAY_WRAPPER_DECRYPT_HOST", "wrapper-v2"),
            wrapper_decrypt_port=_positive_int("APPLE_GATEWAY_WRAPPER_DECRYPT_PORT", 10020, 1, 65535),
            data_root=root,
            cookies_path=Path(cookies).expanduser().resolve() if cookies else None,
            storefront=storefront,
            gamdl_path=os.getenv("APPLE_GATEWAY_GAMDL_PATH", "gamdl"),
            ffmpeg_path=os.getenv("APPLE_GATEWAY_FFMPEG_PATH", "ffmpeg"),
            subprocess_timeout_seconds=_positive_int("APPLE_GATEWAY_PROCESS_TIMEOUT_SECONDS", 900, 10, 7200),
            wrapper_timeout_seconds=_positive_int("APPLE_GATEWAY_WRAPPER_TIMEOUT_SECONDS", 45, 2, 300),
            max_concurrency=_positive_int("APPLE_GATEWAY_MAX_CONCURRENCY", 2, 1, 8),
            max_process_output_bytes=_positive_int("APPLE_GATEWAY_MAX_PROCESS_OUTPUT_BYTES", 32768, 1024, 1048576),
        )

    def prepare(self) -> None:
        self.data_root.mkdir(parents=True, exist_ok=True, mode=0o750)
        for child in ("artifacts", "jobs", "temporary"):
            (self.data_root / child).mkdir(exist_ok=True, mode=0o750)
