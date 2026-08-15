#!/usr/bin/env python3
"""Bounded direct-vs-Allstarr Jellyfin WebSocket qualification."""

from __future__ import annotations

import getpass
import json
import os
import secrets
import sys
import time
import urllib.parse
import urllib.request

try:
    from websockets.sync.client import connect
    from websockets.exceptions import InvalidStatus
except ImportError as exc:  # pragma: no cover - environment prerequisite
    raise SystemExit("install the Python 'websockets' package to run this smoke test") from exc


def socket_url(base_url: str, device_id: str) -> str:
    parsed = urllib.parse.urlsplit(base_url)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise ValueError("Jellyfin base URLs must be absolute HTTP(S) URLs")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("Jellyfin base URLs cannot contain credentials, queries, or fragments")
    scheme = "wss" if parsed.scheme == "https" else "ws"
    path = f"{parsed.path.rstrip('/')}/socket"
    return urllib.parse.urlunsplit(
        (scheme, parsed.netloc, path, urllib.parse.urlencode({"deviceId": device_id}), "")
    )


def qualify(label: str, base_url: str, token: str, run_id: str) -> set[str]:
    device_id = f"allstarr-ws-{run_id}-{label}"
    authorization = (
        'MediaBrowser Client="AllstarrLiveSmoke", Device="Qualification", '
        f'DeviceId="{device_id}", Version="1", Token="{token}"'
    )
    started = time.monotonic()
    message_types: set[str] = set()
    with connect(
        socket_url(base_url, device_id),
        additional_headers={"X-Emby-Authorization": authorization},
        open_timeout=10,
        close_timeout=5,
    ) as socket:
        if not socket.ping().wait(timeout=5):
            raise RuntimeError(f"{label} did not return a WebSocket pong")
        socket.send(json.dumps({"MessageType": "ForceKeepAlive", "Data": 100}))
        socket.send(json.dumps({"MessageType": "SessionsStart", "Data": "0,1500"}))
        request = urllib.request.Request(
            f"{base_url.rstrip('/')}/Sessions/Capabilities/Full",
            data=json.dumps(
                {
                    "PlayableMediaTypes": ["Audio"],
                    "SupportedCommands": ["Playstate"],
                    "SupportsMediaControl": True,
                    "SupportsPersistentIdentifier": True,
                }
            ).encode(),
            headers={
                "Content-Type": "application/json",
                "X-Emby-Authorization": authorization,
            },
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=10) as response:
            if response.status not in {200, 204}:
                raise RuntimeError(f"{label} capabilities returned HTTP {response.status}")
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and "Sessions" not in message_types:
            try:
                raw = socket.recv(timeout=max(0.1, deadline - time.monotonic()))
            except TimeoutError:
                break
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8")
            message = json.loads(raw)
            message_type = message.get("MessageType")
            if isinstance(message_type, str):
                message_types.add(message_type)

    if "Sessions" not in message_types:
        raise RuntimeError(f"{label} did not return a Sessions frame")
    elapsed_ms = (time.monotonic() - started) * 1_000
    print(
        f"PASS {label} header auth and bidirectional frames "
        f"types={','.join(sorted(message_types))} ms={elapsed_ms:.1f}"
    )
    return message_types


def reject_invalid_token(label: str, base_url: str, run_id: str) -> None:
    authorization = (
        'MediaBrowser Client="AllstarrLiveSmoke", Device="Qualification", '
        f'DeviceId="allstarr-ws-{run_id}-invalid", Version="1", Token="invalid"'
    )
    try:
        with connect(
            socket_url(base_url, f"allstarr-ws-{run_id}-invalid"),
            additional_headers={"X-Emby-Authorization": authorization},
            open_timeout=10,
        ):
            pass
    except InvalidStatus as exc:
        if exc.response.status_code == 403:
            print(f"PASS {label} invalid token rejected http=403")
            return
        raise RuntimeError(
            f"{label} invalid token returned HTTP {exc.response.status_code}"
        ) from exc
    raise RuntimeError(f"{label} accepted an invalid token")


def main() -> int:
    token = os.environ.get("JELLYFIN_TOKEN") or getpass.getpass("Jellyfin token: ")
    if not token:
        raise ValueError("a temporary Jellyfin token is required")
    direct_base = os.environ.get("DIRECT_BASE", "https://jellyfin.joshpatra.me")
    allstarr_base = os.environ.get("ALLSTARR_BASE", "https://jfm.joshpatra.me")
    run_id = secrets.token_hex(6)

    direct_types = qualify("direct", direct_base, token, run_id)
    allstarr_types = qualify("allstarr", allstarr_base, token, run_id)
    reject_invalid_token("direct", direct_base, run_id)
    reject_invalid_token("allstarr", allstarr_base, run_id)
    if "Sessions" not in direct_types & allstarr_types:
        raise RuntimeError("direct and Allstarr did not both return Sessions frames")
    print("live-jellyfin-websocket-end checks=5 failures=0")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print(f"FAIL {exc}", file=sys.stderr)
        raise SystemExit(1)
