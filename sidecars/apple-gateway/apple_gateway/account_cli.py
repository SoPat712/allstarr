"""Process-local account binding around the pinned GAMDL command."""
from __future__ import annotations

import asyncio
import contextlib
import importlib
import os
import sys
from pathlib import Path
from typing import Any

from gamdl.api import AppleMusicApi

ACCOUNT_MISMATCH_EXIT = 78
ACCOUNT_UNAVAILABLE_EXIT = 79


class AccountFailure(RuntimeError):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def _account_id(api: Any) -> str | None:
    payload = getattr(api, "account_info", None)
    if not isinstance(payload, dict):
        return None
    data = payload.get("data")
    if not isinstance(data, list) or not data or not isinstance(data[0], dict):
        return None
    value = data[0].get("id")
    return value if isinstance(value, str) and value.strip() else None


def account_api_type(cookie_path: Path, failures: list[str]) -> type[AppleMusicApi]:
    class AccountAppleMusicApi(AppleMusicApi):
        @classmethod
        async def create_from_wrapper(cls, wrapper_api, *args, **kwargs):
            selected = None
            comparison = None
            accepted = False
            try:
                selected = await AppleMusicApi.create_from_netscape_cookies(
                    cookies_path=str(cookie_path), *args, **kwargs
                )
                comparison = await AppleMusicApi.create_from_wrapper(wrapper_api, *args, **kwargs)
                selected_id = _account_id(selected)
                wrapper_id = _account_id(comparison)
                if selected_id is None or wrapper_id is None or selected_id != wrapper_id:
                    raise AccountFailure("account_mismatch")
                accepted = True
                return selected
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                code = "account_mismatch" if isinstance(exc, AccountFailure) else "account_unavailable"
                failures.append(code)
                raise AccountFailure(code) from None
            finally:
                if comparison is not None:
                    with contextlib.suppress(Exception):
                        await comparison.client.aclose()
                if selected is not None and not accepted:
                    with contextlib.suppress(Exception):
                        await selected.client.aclose()

    return AccountAppleMusicApi


def run(argv: list[str]) -> int:
    failures: list[str] = []
    try:
        if argv.count("--cookies-path") != 1 or "--use-wrapper" not in argv or "--no-config-file" not in argv:
            raise ValueError()
        cookie_path = Path(argv[argv.index("--cookies-path") + 1])
        if cookie_path.is_symlink() or not cookie_path.is_file():
            raise ValueError()
    except (ValueError, IndexError):
        return ACCOUNT_UNAVAILABLE_EXIT

    cli = importlib.import_module("gamdl.cli.cli")
    original = cli.AppleMusicApi
    original_writer = cli.CustomOutputWriter
    # This replacement happens only in the short-lived command process, never
    # when this module is imported by the ASGI gateway.
    cli.AppleMusicApi = account_api_type(cookie_path, failures)
    try:
        with open(os.devnull, "w", encoding="utf-8") as quiet:
            with contextlib.redirect_stdout(quiet), contextlib.redirect_stderr(quiet):
                # GAMDL's writer captures sys.stdout in a default argument at
                # import time; supply the quiet stream explicitly as well.
                cli.CustomOutputWriter = lambda: original_writer(streams=[quiet])
                cli.main.main(args=argv, standalone_mode=False)
    except Exception:
        return ACCOUNT_MISMATCH_EXIT if "account_mismatch" in failures else ACCOUNT_UNAVAILABLE_EXIT
    finally:
        cli.AppleMusicApi = original
        cli.CustomOutputWriter = original_writer
    if failures:
        return ACCOUNT_MISMATCH_EXIT if "account_mismatch" in failures else ACCOUNT_UNAVAILABLE_EXIT
    return 0


if __name__ == "__main__":
    raise SystemExit(run(sys.argv[1:]))
