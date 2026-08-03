#!/usr/bin/env python3
"""Bounded, redacted live qualification for Navidrome/OpenSubsonic endpoints."""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import os
import secrets
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import BinaryIO

MAX_BODY_BYTES = 65_536
API_VERSION = "1.16.1"
STATEFUL_CONFIRMATION = "create-and-delete-throwaway-playlist"


class SmokeError(RuntimeError):
    pass


def utc_now() -> str:
    return (
        datetime.now(timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z")
    )


def normalize_base_url(value: str) -> str:
    parsed = urllib.parse.urlsplit(value.strip())
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise SmokeError("SUBSONIC_BASE_URL must be an absolute HTTP(S) URL")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise SmokeError(
            "Subsonic base URLs cannot contain credentials, queries, or fragments"
        )
    return urllib.parse.urlunsplit(
        (parsed.scheme, parsed.netloc, parsed.path.rstrip("/"), "", "")
    )


def read_bounded(stream: BinaryIO) -> bytes:
    body = stream.read(MAX_BODY_BYTES + 1)
    if len(body) > MAX_BODY_BYTES:
        raise SmokeError(
            f"response exceeded the {MAX_BODY_BYTES}-byte qualification limit"
        )
    return body


@dataclass(frozen=True)
class Response:
    status: int
    content_type: str
    content_range: str | None
    etag: str | None
    body: bytes
    elapsed_ms: float


@dataclass(frozen=True)
class Envelope:
    status: str
    error_code: int | None

    @property
    def succeeded(self) -> bool:
        return self.status == "ok"


def json_root(body: bytes) -> Mapping[str, object]:
    value = json.loads(body)
    root = value.get("subsonic-response") if isinstance(value, dict) else None
    if not isinstance(root, dict):
        raise SmokeError("response did not contain a Subsonic envelope")
    return root


def envelope(body: bytes, content_type: str = "") -> Envelope:
    if "xml" in content_type.lower() or body.lstrip().startswith(b"<"):
        root = ET.fromstring(body)
        error = next(
            (item for item in root if item.tag.rsplit("}", 1)[-1] == "error"), None
        )
        code = error.attrib.get("code") if error is not None else None
        return Envelope(
            root.attrib.get("status", ""),
            int(code) if code and code.isdigit() else None,
        )
    root = json_root(body)
    error = root.get("error")
    code = error.get("code") if isinstance(error, dict) else None
    return Envelope(
        str(root.get("status", "")), int(code) if isinstance(code, int) else None
    )


def nested_lists(value: object, *keys: str) -> Iterable[Mapping[str, object]]:
    current: list[object] = [value]
    for key in keys:
        following: list[object] = []
        for item in current:
            child = item.get(key) if isinstance(item, dict) else None
            if isinstance(child, list):
                following.extend(child)
            elif child is not None:
                following.append(child)
        current = following
    return (item for item in current if isinstance(item, dict))


def first_id(items: Iterable[Mapping[str, object]]) -> str | None:
    for item in items:
        value = item.get("id")
        if isinstance(value, str) and 0 < len(value) <= 1_000:
            return value
    return None


def public_shape(value: object) -> object:
    if isinstance(value, dict):
        return {key: public_shape(child) for key, child in sorted(value.items())}
    if isinstance(value, list):
        return [public_shape(value[0])] if value else []
    if value is None:
        return None
    return type(value).__name__


def playlist_matches(
    root: Mapping[str, object], playlist_id: str, allowed_names: set[str]
) -> bool:
    playlist = root.get("playlist")
    return (
        isinstance(playlist, dict)
        and playlist.get("id") == playlist_id
        and playlist.get("name") in allowed_names
    )


def stateful_steps() -> tuple[str, ...]:
    return ("create", "add", "reread", "rename", "reorder", "empty", "delete-exact-id")


class SubsonicClient:
    def __init__(
        self,
        base_url: str,
        username: str,
        password: str,
        client_name: str,
        timeout: float,
    ):
        self.base_url = normalize_base_url(base_url)
        self.username = username
        self.password = password
        self.client_name = client_name
        self.timeout = timeout

    def _auth(self, mode: str) -> list[tuple[str, str]]:
        if mode == "missing":
            return []
        if mode == "invalid":
            return [("u", "allstarr-invalid-smoke"), ("p", "allstarr-invalid-smoke")]
        if mode == "password":
            return [("u", self.username), ("p", self.password)]
        if mode == "token":
            salt = secrets.token_hex(8)
            token = hashlib.md5(
                (self.password + salt).encode(), usedforsecurity=False
            ).hexdigest()
            return [("u", self.username), ("t", token), ("s", salt)]
        raise ValueError(f"unsupported authentication mode: {mode}")

    def call(
        self,
        method: str,
        params: Sequence[tuple[str, object]] = (),
        *,
        http_method: str = "GET",
        response_format: str | None = "json",
        view_suffix: bool = False,
        auth: str = "token",
        headers: Mapping[str, str] | None = None,
    ) -> Response:
        suffix = ".view" if view_suffix else ""
        url = f"{self.base_url}/rest/{method}{suffix}"
        values: list[tuple[str, object]] = [
            ("v", API_VERSION),
            ("c", self.client_name),
            *self._auth(auth),
            *params,
        ]
        if response_format:
            values.append(("f", response_format))
        encoded = urllib.parse.urlencode(values, doseq=True).encode()
        request_headers = {"User-Agent": f"{self.client_name}/1", **(headers or {})}
        if http_method == "POST":
            request_headers["Content-Type"] = "application/x-www-form-urlencoded"
            request = urllib.request.Request(
                url, data=encoded, headers=request_headers, method="POST"
            )
        else:
            request = urllib.request.Request(
                f"{url}?{encoded.decode()}", headers=request_headers, method=http_method
            )

        started = time.monotonic()
        try:
            response = urllib.request.urlopen(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            response = error
        try:
            body = read_bounded(response)
            return Response(
                response.status,
                response.headers.get_content_type(),
                response.headers.get("Content-Range"),
                response.headers.get("ETag"),
                body,
                (time.monotonic() - started) * 1_000,
            )
        finally:
            response.close()


class Results:
    def __init__(
        self, run_id: str, started_at: str, client_name: str, artifact_dir: Path
    ):
        self.run_id = run_id
        self.started_at = started_at
        self.client_name = client_name
        self.artifact_dir = artifact_dir
        self.checks = 0
        self.failures = 0
        self.records: list[dict[str, object]] = []
        self.blockers: list[str] = []

    def check_envelope(
        self, label: str, response: Response, expected_success: bool = True
    ) -> Mapping[str, object] | None:
        self.checks += 1
        try:
            observed = envelope(response.body, response.content_type)
            passed = observed.succeeded == expected_success
            root = json_root(response.body) if "json" in response.content_type else None
            detail = f"envelope={observed.status} error={observed.error_code}"
        except (SmokeError, ValueError, json.JSONDecodeError, ET.ParseError) as error:
            observed = None
            root = None
            passed = False
            detail = f"parse={type(error).__name__}"
        self._record(label, response, passed, detail)
        return root

    def check_http(
        self, label: str, response: Response, expected_statuses: set[int]
    ) -> bool:
        self.checks += 1
        passed = (
            response.status in expected_statuses
            and 0 < len(response.body) <= MAX_BODY_BYTES
        )
        detail = f"range={'yes' if response.content_range else 'no'} etag={'yes' if response.etag else 'no'}"
        self._record(label, response, passed, detail)
        return passed

    def check_value(self, label: str, passed: bool) -> None:
        self.checks += 1
        if not passed:
            self.failures += 1
        print(f"{'PASS' if passed else 'FAIL'} {label}")
        self.records.append({"label": label, "passed": passed})

    def blocked(self, reason: str) -> None:
        self.blockers.append(reason)
        print(f"BLOCKED {reason}")

    def _record(
        self, label: str, response: Response, passed: bool, detail: str
    ) -> None:
        if not passed:
            self.failures += 1
        print(
            f"{'PASS' if passed else 'FAIL'} {label} http={response.status} "
            f"bytes={len(response.body)} ms={response.elapsed_ms:.1f} {detail}"
        )
        self.records.append(
            {
                "label": label,
                "passed": passed,
                "httpStatus": response.status,
                "bytes": len(response.body),
                "elapsedMilliseconds": round(response.elapsed_ms, 1),
                "contentType": response.content_type,
                "hasContentRange": bool(response.content_range),
                "hasEtag": bool(response.etag),
            }
        )

    def write(self) -> None:
        self.artifact_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
        report = {
            "runId": self.run_id,
            "startedAt": self.started_at,
            "endedAt": utc_now(),
            "client": self.client_name,
            "maximumBodyBytes": MAX_BODY_BYTES,
            "checks": self.checks,
            "failures": self.failures,
            "blockers": self.blockers,
            "results": self.records,
        }
        (self.artifact_dir / "report.json").write_text(
            json.dumps(report, indent=2) + "\n"
        )


def require_root(
    results: Results, label: str, response: Response
) -> Mapping[str, object]:
    root = results.check_envelope(label, response)
    if root is None or root.get("status") != "ok":
        raise SmokeError(f"{label} did not return a successful JSON envelope")
    return root


def media_ids(
    client: SubsonicClient,
    results: Results,
) -> tuple[str, str, list[str], dict[str, Mapping[str, object]]]:
    indexes = require_root(results, "direct getIndexes", client.call("getIndexes"))
    artists = require_root(results, "direct getArtists", client.call("getArtists"))
    artist_id = first_id(nested_lists(indexes, "indexes", "index", "artist"))
    artist_id = artist_id or first_id(
        nested_lists(artists, "artists", "index", "artist")
    )
    if not artist_id:
        raise SmokeError("no artist was available for dynamic qualification")

    artist = require_root(
        results, "direct getArtist", client.call("getArtist", [("id", artist_id)])
    )
    album_id = first_id(nested_lists(artist, "artist", "album"))
    if not album_id:
        raise SmokeError("the selected artist had no album for dynamic qualification")

    album = require_root(
        results, "direct getAlbum", client.call("getAlbum", [("id", album_id)])
    )
    songs = list(nested_lists(album, "album", "song"))
    song_ids = [value for value in (first_id([song]) for song in songs) if value]
    if not song_ids:
        raise SmokeError("the selected album had no song for dynamic qualification")
    song = require_root(
        results, "direct getSong", client.call("getSong", [("id", song_ids[0])])
    )
    return (
        artist_id,
        album_id,
        song_ids[:2],
        {"artist": artist, "album": album, "song": song},
    )


def inventory(client: SubsonicClient, results: Results) -> None:
    results.check_envelope(
        "password GET JSON ping", client.call("ping", auth="password")
    )
    results.check_envelope(
        "token POST JSON ping.view",
        client.call("ping", http_method="POST", view_suffix=True, auth="token"),
    )
    results.check_envelope(
        "password POST XML ping.view",
        client.call(
            "ping",
            http_method="POST",
            response_format="xml",
            view_suffix=True,
            auth="password",
        ),
    )
    results.check_envelope(
        "invalid authentication",
        client.call("ping", auth="invalid"),
        expected_success=False,
    )
    results.check_envelope(
        "missing authentication",
        client.call("ping", http_method="POST", response_format="xml", auth="missing"),
        expected_success=False,
    )
    results.check_envelope("getLicense", client.call("getLicense"))
    results.check_envelope(
        "getOpenSubsonicExtensions POST XML",
        client.call(
            "getOpenSubsonicExtensions", http_method="POST", response_format="xml"
        ),
    )
    results.check_envelope("getMusicFolders", client.call("getMusicFolders"))
    results.check_envelope(
        "getAlbumList2 POST",
        client.call(
            "getAlbumList2", [("type", "newest"), ("size", 5)], http_method="POST"
        ),
    )
    results.check_envelope(
        "search3 independent windows",
        client.call(
            "search3",
            [
                ("query", "a"),
                ("artistCount", 3),
                ("artistOffset", 0),
                ("albumCount", 4),
                ("albumOffset", 1),
                ("songCount", 5),
                ("songOffset", 2),
            ],
        ),
    )
    playlists = require_root(results, "getPlaylists JSON", client.call("getPlaylists"))
    playlist_container = playlists.get("playlists")
    results.check_value(
        "getPlaylists container accepts an empty list",
        isinstance(playlist_container, dict)
        and (
            "playlist" not in playlist_container
            or isinstance(playlist_container["playlist"], list)
        ),
    )
    results.check_envelope(
        "getPlaylists POST XML",
        client.call("getPlaylists", http_method="POST", response_format="xml"),
    )


def details(
    client: SubsonicClient,
    results: Results,
    artist_id: str,
    album_id: str,
    song_ids: list[str],
    song_root: Mapping[str, object],
    prefix: str,
) -> dict[str, Mapping[str, object]]:
    selected_song = next(nested_lists(song_root, "song"), {})
    title = (
        selected_song.get("title")
        if isinstance(selected_song.get("title"), str)
        else "a"
    )
    artist_name = (
        selected_song.get("artist")
        if isinstance(selected_song.get("artist"), str)
        else "a"
    )
    cover_id = (
        selected_song.get("coverArt")
        if isinstance(selected_song.get("coverArt"), str)
        else album_id
    )

    search = require_root(
        results,
        f"{prefix} dynamic search3",
        client.call(
            "search3",
            [("query", title), ("artistCount", 5), ("albumCount", 5), ("songCount", 5)],
        ),
    )
    similar = require_root(
        results,
        f"{prefix} getSimilarSongs2",
        client.call("getSimilarSongs2", [("id", song_ids[0]), ("count", 5)]),
    )
    top = require_root(
        results,
        f"{prefix} getTopSongs",
        client.call("getTopSongs", [("artist", artist_name), ("count", 5)]),
    )
    lyrics = require_root(
        results,
        f"{prefix} getLyricsBySongId",
        client.call("getLyricsBySongId", [("id", song_ids[0])]),
    )
    cover = client.call(
        "getCoverArt", [("id", cover_id), ("size", 256)], response_format=None
    )
    results.check_http(f"{prefix} bounded getCoverArt", cover, {200})
    stream = client.call(
        "stream",
        [("id", song_ids[0])],
        response_format=None,
        headers={"Range": "bytes=0-65535"},
    )
    results.check_http(f"{prefix} bounded stream range", stream, {200, 206})
    return {"search3": search, "similar": similar, "top": top, "lyrics": lyrics}


def exact_details(
    client: SubsonicClient,
    results: Results,
    artist_id: str,
    album_id: str,
    song_id: str,
    prefix: str,
) -> dict[str, Mapping[str, object]]:
    return {
        "artist": require_root(
            results,
            f"{prefix} getArtist",
            client.call("getArtist", [("id", artist_id)]),
        ),
        "album": require_root(
            results, f"{prefix} getAlbum", client.call("getAlbum", [("id", album_id)])
        ),
        "song": require_root(
            results, f"{prefix} getSong", client.call("getSong", [("id", song_id)])
        ),
    }


def compare_read_only(
    direct: dict[str, Mapping[str, object]],
    other: dict[str, Mapping[str, object]],
    results: Results,
) -> None:
    for name in sorted(direct.keys() & other.keys()):
        results.check_value(
            f"Allstarr {name} response shape",
            public_shape(direct[name]) == public_shape(other[name]),
        )


def run_stateful(client: SubsonicClient, results: Results, song_ids: list[str]) -> None:
    if len(song_ids) < 2:
        raise SmokeError(
            "stateful qualification requires two dynamically selected local songs"
        )
    print(f"DRY-RUN stateful steps={','.join(stateful_steps())}")
    run_name = f"allstarr-smoke-{results.run_id}"
    renamed = f"{run_name}-renamed"
    allowed_names = {run_name, renamed}
    playlist_id: str | None = None

    def playlist_root(label: str) -> Mapping[str, object]:
        assert playlist_id is not None
        return require_root(
            results, label, client.call("getPlaylist", [("id", playlist_id)])
        )

    def exact_song_ids(root: Mapping[str, object]) -> list[str]:
        return [
            str(item["id"])
            for item in nested_lists(root, "playlist", "entry")
            if isinstance(item.get("id"), str)
        ]

    try:
        created = require_root(
            results,
            "create exact throwaway playlist",
            client.call(
                "createPlaylist",
                [("name", run_name), ("songId", song_ids[0])],
                http_method="POST",
            ),
        )
        playlist_id = first_id(nested_lists(created, "playlist"))
        if not playlist_id or not playlist_matches(created, playlist_id, {run_name}):
            raise SmokeError(
                "createPlaylist did not return the exact throwaway playlist"
            )

        results.check_envelope(
            "add selected song",
            client.call(
                "updatePlaylist",
                [("playlistId", playlist_id), ("songIdToAdd", song_ids[1])],
                http_method="POST",
            ),
        )
        results.check_value(
            "reread added order",
            exact_song_ids(playlist_root("reread added playlist")) == song_ids,
        )
        results.check_envelope(
            "rename exact throwaway playlist",
            client.call(
                "updatePlaylist",
                [("playlistId", playlist_id), ("name", renamed)],
                http_method="POST",
            ),
        )
        results.check_value(
            "reread renamed identity",
            playlist_matches(
                playlist_root("reread renamed playlist"), playlist_id, {renamed}
            ),
        )
        results.check_envelope(
            "reorder selected songs",
            client.call(
                "updatePlaylist",
                [
                    ("playlistId", playlist_id),
                    ("songIndexToRemove", 0),
                    ("songIdToAdd", song_ids[0]),
                ],
                http_method="POST",
            ),
        )
        results.check_value(
            "reread reordered songs",
            exact_song_ids(playlist_root("reread reordered playlist"))
            == [song_ids[1], song_ids[0]],
        )
        results.check_envelope(
            "empty exact throwaway playlist",
            client.call(
                "updatePlaylist",
                [
                    ("playlistId", playlist_id),
                    ("songIndexToRemove", 1),
                    ("songIndexToRemove", 0),
                ],
                http_method="POST",
            ),
        )
        results.check_value(
            "reread empty playlist",
            exact_song_ids(playlist_root("reread empty playlist")) == [],
        )
        if not playlist_matches(
            playlist_root("verify before exact delete"), playlist_id, allowed_names
        ):
            raise SmokeError(
                "refusing to delete a playlist whose exact ID and name no longer match"
            )
        results.check_envelope(
            "delete exact throwaway playlist",
            client.call("deletePlaylist", [("id", playlist_id)], http_method="POST"),
        )
        playlist_id = None
    finally:
        if playlist_id:
            try:
                current = json_root(
                    client.call("getPlaylist", [("id", playlist_id)]).body
                )
                if playlist_matches(current, playlist_id, allowed_names):
                    cleanup = client.call(
                        "deletePlaylist", [("id", playlist_id)], http_method="POST"
                    )
                    if envelope(cleanup.body, cleanup.content_type).succeeded:
                        print("PASS cleanup exact throwaway playlist")
                    else:
                        print(
                            "CLEANUP-BLOCKED exact playlist delete returned a failed envelope",
                            file=sys.stderr,
                        )
                else:
                    print(
                        "CLEANUP-BLOCKED exact playlist ID/name verification failed",
                        file=sys.stderr,
                    )
            except (
                SmokeError,
                ValueError,
                OSError,
                json.JSONDecodeError,
                ET.ParseError,
            ) as error:
                print(f"CLEANUP-BLOCKED {type(error).__name__}", file=sys.stderr)


def credentials() -> tuple[str, str]:
    username = (
        os.environ.get("SUBSONIC_USERNAME") or input("Subsonic username: ").strip()
    )
    password = os.environ.get("SUBSONIC_PASSWORD") or getpass.getpass(
        "Subsonic password: "
    )
    if not username or not password:
        raise SmokeError("a Subsonic username and password are required")
    return username, password


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--stateful",
        action="store_true",
        help="create and delete one exact throwaway playlist",
    )
    args = parser.parse_args()

    base_url = os.environ.get("SUBSONIC_BASE_URL")
    if not base_url:
        raise SmokeError("set SUBSONIC_BASE_URL; the script has no live-server default")
    username, password = credentials()
    timeout = float(os.environ.get("SUBSONIC_TIMEOUT_SECONDS", "20"))
    if not 0 < timeout <= 120:
        raise SmokeError("SUBSONIC_TIMEOUT_SECONDS must be between 0 and 120")
    if (
        args.stateful
        and os.environ.get("SUBSONIC_STATEFUL_CONFIRM") != STATEFUL_CONFIRMATION
    ):
        raise SmokeError(
            f"--stateful requires SUBSONIC_STATEFUL_CONFIRM={STATEFUL_CONFIRMATION}"
        )

    started_at = utc_now()
    run_id = (
        started_at.replace("-", "").replace(":", "").replace("T", "-").removesuffix("Z")
    )
    client_name = f"AllstarrSubsonicSmoke-{run_id}"
    artifact_value = os.environ.get("SUBSONIC_ARTIFACT_DIR")
    artifact_dir = (
        Path(artifact_value)
        if artifact_value
        else Path(tempfile.mkdtemp(prefix=f"allstarr-subsonic-{run_id}-"))
    )
    results = Results(run_id, started_at, client_name, artifact_dir)
    direct = SubsonicClient(base_url, username, password, client_name, timeout)
    print(
        f"live-subsonic-start={started_at} run-id={run_id} client={client_name} "
        f"max-body-bytes={MAX_BODY_BYTES} stateful={int(args.stateful)}"
    )

    try:
        inventory(direct, results)
        artist_id, album_id, song_ids, direct_details = media_ids(direct, results)
        direct_details.update(
            details(
                direct,
                results,
                artist_id,
                album_id,
                song_ids,
                direct_details["song"],
                "direct",
            )
        )
        results.blocked(
            "star/unstar/scrobble mutations are not part of read-only qualification"
        )
        print(
            "PASS dynamically selected local artist/album/song without recording identifiers"
        )

        allstarr_url = os.environ.get("ALLSTARR_SUBSONIC_BASE_URL")
        if allstarr_url:
            allstarr = SubsonicClient(
                allstarr_url, username, password, client_name, timeout
            )
            allstarr_details = exact_details(
                allstarr, results, artist_id, album_id, song_ids[0], "Allstarr"
            )
            allstarr_details.update(
                details(
                    allstarr,
                    results,
                    artist_id,
                    album_id,
                    song_ids,
                    allstarr_details["song"],
                    "Allstarr",
                )
            )
            compare_read_only(direct_details, allstarr_details, results)
        else:
            results.blocked(
                "Allstarr comparison requires ALLSTARR_SUBSONIC_BASE_URL and a dedicated instance"
            )

        if args.stateful:
            run_stateful(direct, results, song_ids)
        else:
            results.blocked(
                f"playlist writes require --stateful and SUBSONIC_STATEFUL_CONFIRM={STATEFUL_CONFIRMATION}"
            )
    finally:
        results.write()
        ended_at = utc_now()
        print(f"log-correlation since={started_at} user-agent={client_name}/1")
        print(
            f"live-subsonic-end={ended_at} checks={results.checks} failures={results.failures} "
            f"report={artifact_dir / 'report.json'}"
        )
    return 0 if results.failures == 0 else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (SmokeError, ValueError, urllib.error.URLError) as error:
        print(f"ERROR {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(2)
