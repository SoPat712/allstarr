#!/usr/bin/env python3
"""Bounded, read-only qualification for Allstarr's BrainzMash /ws/2 contract."""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass


MAX_RESPONSE_BYTES = 1024 * 1024
DEFAULT_BASE_URL = "https://api.brainzmash.cc/ws/2"


class QualificationError(RuntimeError):
    pass


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


@dataclass(frozen=True)
class Observation:
    case: str
    status: int
    elapsed_ms: int
    bytes_read: int
    entity_count: int | None = None


class Client:
    def __init__(self, base_url: str, user_agent: str, timeout: float, interval_ms: int):
        parsed = urllib.parse.urlsplit(base_url.rstrip("/"))
        if parsed.scheme != "https" or parsed.hostname != "api.brainzmash.cc" or parsed.path != "/ws/2":
            raise QualificationError("The live qualifier only permits https://api.brainzmash.cc/ws/2.")
        if not user_agent.strip() or "\r" in user_agent or "\n" in user_agent:
            raise QualificationError("A valid, operator-authorized User-Agent is required.")
        self.base_url = base_url.rstrip("/")
        self.user_agent = user_agent.strip()
        self.timeout = timeout
        self.interval = interval_ms / 1000
        self.last_request = 0.0
        self.opener = urllib.request.build_opener(NoRedirects())
        self.observations: list[Observation] = []

    def get(self, case: str, resource: str, query: dict[str, str] | None = None, expected=(200,)) -> dict:
        remaining = self.interval - (time.monotonic() - self.last_request)
        if remaining > 0:
            time.sleep(remaining)
        url = f"{self.base_url}/{resource.lstrip('/')}"
        if query:
            url = f"{url}?{urllib.parse.urlencode(query)}"
        request = urllib.request.Request(
            url,
            headers={"Accept": "application/json", "User-Agent": self.user_agent},
        )
        started = time.monotonic()
        status = 0
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                status = response.status
                body = response.read(MAX_RESPONSE_BYTES + 1)
        except urllib.error.HTTPError as error:
            status = error.code
            body = error.read(MAX_RESPONSE_BYTES + 1)
        finally:
            self.last_request = time.monotonic()
        elapsed_ms = round((time.monotonic() - started) * 1000)
        if len(body) > MAX_RESPONSE_BYTES:
            raise QualificationError(f"{case}: response exceeded {MAX_RESPONSE_BYTES} bytes")
        if status not in expected:
            raise QualificationError(f"{case}: expected HTTP {expected}, received {status}")
        if not body:
            payload: dict = {}
        else:
            try:
                payload = json.loads(body)
            except json.JSONDecodeError as error:
                raise QualificationError(f"{case}: response was not JSON") from error
            if not isinstance(payload, dict):
                raise QualificationError(f"{case}: response root was not an object")
        count = len(payload.get("recordings", [])) if isinstance(payload.get("recordings"), list) else None
        self.observations.append(Observation(case, status, elapsed_ms, len(body), count))
        return payload


def require_text(payload: dict, field: str, case: str) -> str:
    value = payload.get(field)
    if not isinstance(value, str) or not value.strip():
        raise QualificationError(f"{case}: required field {field!r} was absent")
    return value


def qualify(client: Client) -> dict:
    searches = (
        ("base-edition", "Sunroof", "Nicky Youre", None),
        ("featured-credit", "Kiss Me More", "Doja Cat", "multiple-credits"),
        ("versioned-title", "Dream A Little Dream Of Me", "Ella Fitzgerald", None),
    )
    seed = None
    for case, title, artist, expectation in searches:
        payload = client.get(
            f"search-{case}",
            "recording/",
            {"query": f'recording:"{title}" AND artist:"{artist}"', "fmt": "json", "limit": "10"},
        )
        recordings = payload.get("recordings")
        if not isinstance(recordings, list) or not recordings:
            raise QualificationError(f"search-{case}: no recording candidates returned")
        if expectation == "multiple-credits" and not any(
            len(item.get("artist-credit") or []) > 1 for item in recordings
        ):
            raise QualificationError(f"search-{case}: no multi-artist credit returned")
        if seed is None:
            seed = next((item for item in recordings if item.get("releases") and item.get("artist-credit")), recordings[0])

    recording_id = require_text(seed, "id", "recording-seed")
    recording = client.get(
        "recording-detail",
        f"recording/{recording_id}",
        {"fmt": "json", "inc": "artists+releases+isrcs+aliases+genres"},
    )
    require_text(recording, "title", "recording-detail")
    credits = recording.get("artist-credit") or seed.get("artist-credit") or []
    releases = recording.get("releases") or seed.get("releases") or []
    artist_id = next((credit.get("artist", {}).get("id") for credit in credits if credit.get("artist", {}).get("id")), None)
    release_id = next((release.get("id") for release in releases if release.get("id")), None)
    if not artist_id or not release_id:
        raise QualificationError("recording-detail: artist/release hierarchy was incomplete")

    artist = client.get("artist-detail", f"artist/{artist_id}", {"fmt": "json", "inc": "aliases"})
    require_text(artist, "name", "artist-detail")
    release = client.get(
        "release-detail",
        f"release/{release_id}",
        {"fmt": "json", "inc": "artist-credits+release-groups+recordings+media+labels"},
    )
    release_group = release.get("release-group") or {}
    release_group_id = require_text(release_group, "id", "release-detail")
    media = release.get("media")
    if not isinstance(media, list) or not media:
        raise QualificationError("release-detail: media/track hierarchy was absent")
    group = client.get(
        "release-group-detail",
        f"release-group/{release_group_id}",
        {"fmt": "json", "inc": "artist-credits+aliases"},
    )
    require_text(group, "title", "release-group-detail")
    client.get(
        "missing-recording",
        "recording/ffffffff-ffff-4fff-8fff-ffffffffffff",
        {"fmt": "json"},
        expected=(404,),
    )
    return {
        "endpoint": client.base_url,
        "sourceRevision": "brainzmash:ws2",
        "passed": True,
        "requestCount": len(client.observations),
        "observations": [observation.__dict__ for observation in client.observations],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--user-agent", default=os.environ.get("BRAINZMASH_USER_AGENT", ""))
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--interval-ms", type=int, default=1000)
    args = parser.parse_args()
    try:
        if args.interval_ms < 100:
            raise QualificationError("The live request interval must be at least 100 ms.")
        result = qualify(Client(args.base_url, args.user_agent, args.timeout, args.interval_ms))
        print(json.dumps(result, indent=2))
        return 0
    except (QualificationError, urllib.error.URLError) as error:
        print(f"BrainzMash qualification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
