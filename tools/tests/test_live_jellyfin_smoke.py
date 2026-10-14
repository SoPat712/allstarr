#!/usr/bin/env python3
"""Offline checks of the live harness, using only a loopback fixture server."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit


SCRIPT = Path(__file__).with_name("live_jellyfin_smoke.sh").read_text()
SEARCH_PROJECT = Path(__file__).with_name("SearchContract").joinpath("SearchContract.csproj").resolve()


def function(name):
    return re.search(rf"^{name}\(\) \{{\n.*?^\}}", SCRIPT, re.M | re.S)[0]


def audio(identifier, title="Fixture", artist="Artist", isrc=None):
    providers = {} if isrc is None else {"ISRC": isrc}
    item = {"Id": identifier, "Type": "Audio", "Name": title,
            "Artists": [artist], "ProviderIds": providers}
    if identifier.startswith("ext-"):
        item.update(Name=f"{title} [A]", Album="Album [Provider]",
                    Artists=[f"{artist} [Provider]"],
                    Path=f"/music/{artist} [Provider]/Album [Provider]/{title} [A].flac")
    return item


def envelope(items, total=None):
    return {"Items": items, "StartIndex": 0, "TotalRecordCount": len(items) if total is None else total}


class PlaybackIdentityTests(unittest.TestCase):
    def check_identity(self, identity, sources):
        body = function("check_external_provider_case")
        self.assertIn('"$playback_identity_contract"', body)
        contract = re.search(r"^playback_identity_contract='(.*?)'", SCRIPT, re.M | re.S)[1]
        return subprocess.run(["jq", "-e", "--arg", "id", identity, contract],
                              input=json.dumps({"MediaSources": sources}), text=True, capture_output=True).returncode == 0

    def test_encoded_recording_identity_is_preserved(self):
        identifier = "ext-fixture-song-qobuz:123"
        self.assertTrue(self.check_identity(identifier, [{"Id": identifier, "DirectStreamUrl":
            "/Audio/ext-fixture-song-qobuz%3A123/stream?static=true"}]))

    def test_ordinary_recording_identity_is_preserved(self):
        identifier = "ext-fixture-song-123"
        self.assertTrue(self.check_identity(identifier, [{"Id": identifier, "DirectStreamUrl":
            "/Audio/ext-fixture-song-123/stream?static=true"}]))

    def test_different_source_identity_is_rejected(self):
        self.assertFalse(self.check_identity("ext-fixture-song-123", [{"Id": "different", "DirectStreamUrl":
            "/Audio/ext-fixture-song-123/stream"}]))

    def test_identity_in_wrong_path_or_query_is_rejected(self):
        identifier = "ext-fixture-song-123"
        for path in ["/Other/" + identifier + "/stream", "/Audio/different/stream?id=" + identifier,
                     "/Audio/" + identifier + "4/stream", "/Audio/" + identifier + "/universal"]:
            with self.subTest(path=path):
                self.assertFalse(self.check_identity(identifier, [{"Id": identifier, "DirectStreamUrl": path}]))

    def test_missing_sources_are_rejected(self):
        self.assertFalse(self.check_identity("ext-fixture-song-123", []))


class StreamFixture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path.startswith("/native/"):
            url = urlsplit(self.path)
            query = parse_qs(url.query)
            mode = url.path.split("/")[2]
            offset = int(query.get("StartIndex", ["0"])[0])
            self.server.native_requests.append((mode, offset, query))
            valid = (self.headers.get("Authorization") == "fixture-auth" and
                     query.get("Fields") == ["ProviderIds"] and
                     query.get("IncludeItemTypes") == ["Audio"] and
                     "SearchTerm" not in query)
            items = [audio(f"native-{index}", "Unrelated", "Other") for index in range(500)]
            # This tagged native counterpart is outside the requested search text and first capture page.
            items.append(audio("native-500", "Alternate title", "Different credit", "us-ab1-26-12345"))
            selected = items[offset:offset + int(query.get("Limit", ["500"])[0])]
            if mode == "truncated" and offset > 0:
                selected = []
            if mode == "duplicate" and offset > 0:
                selected = [items[0]]
            if mode == "invalid" and selected:
                selected[0].pop("ProviderIds")
            total = 100001 if mode == "over-limit" else 502 if mode == "changed-total" and offset else len(items)
            value = {"Items": selected, "StartIndex": offset + (mode == "wrong-offset"), "TotalRecordCount": total}
            body = json.dumps(value).encode()
            self.send_response(200 if valid else 401)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path.startswith("/api/admin/"):
            cookie = self.headers.get("Cookie")
            valid_cookie = cookie in ("fixture=admin", "fixture=listener", "fixture=foreign")
            administrator = cookie == "fixture=admin"
            status = 200 if valid_cookie else 401
            if self.path.endswith("auth/me"):
                value = {"authenticated": valid_cookie}
            elif self.path.endswith("ui/home"):
                value = {"schema": {}, "stats": {"cacheTracks": None, "keptTracks": None},
                         "providerHealth": {"providers": []}, "activity": {"items": []}}
            elif self.path.endswith("ui/now-playing"):
                owner = "another-user" if cookie == "fixture=foreign" else "fixture-user"
                value = {"items": [{"userId": owner}]}
            elif self.path.endswith("ui/activity") or administrator:
                value = {"items": []}
            else:
                status = 403 if valid_cookie else 401
                value = {"error": "Administrator permissions required"}
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path == "/timeout":
            time.sleep(1.2)
            return
        progressive = self.path in ("/progressive", "/short-progressive")
        body = b"a" * (1024 if self.path == "/short-progressive" else
                       131072 if progressive or self.path == "/oversized-range" else 65536)
        self.send_response(200 if progressive else 206)
        self.send_header("Content-Type", "text/html" if self.path == "/html" else "audio/flac")
        if self.path != "/missing-provider":
            self.send_header("X-Allstarr-Provider", "qobuz")
        if not progressive:
            self.send_header("Content-Range", "bytes 0-65535/200000")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_POST(self):
        login = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
        valid = login.get("username") in ("fixture", "fixture-admin", "fixture-foreign") and login.get("password") == "fixture-password"
        administrator = login.get("username") == "fixture-admin"
        body = json.dumps({"authenticated": valid, "user": {
            "isAdministrator": administrator, "allstarrUserId": "fixture-user"}}).encode()
        self.send_response(200 if valid else 400)
        if valid:
            audience = "admin" if administrator else "foreign" if login.get("username") == "fixture-foreign" else "listener"
            self.send_header("Set-Cookie", f"fixture={audience}; Path=/; HttpOnly")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class JellyfinSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), StreamFixture)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def test_external_search_keeps_artist_to_disambiguate_shared_titles(self):
        for item, expected in (
            ({"Name": "Just One Touch [A]", "Artists": ["Fixture Artist [YouTube Music]"]}, "Just One Touch Fixture Artist"),
            ({"Name": "Track [A]/[E]", "Artists": ["Artist [Deezer]"]}, "Track Artist"),
            ({"Name": "Track [A]", "AlbumArtist": "Artist"}, "Track Artist"),
            ({"Name": "Track [A]"}, "Track"),
        ):
            with self.subTest(item=item), tempfile.TemporaryDirectory() as directory:
                Path(directory, "item.json").write_text(json.dumps(item))
                result = subprocess.run(["bash", "-c", function("external_search_query") +
                                         '\nexternal_search_query item.json'], cwd=directory,
                                        text=True, capture_output=True, check=True)
                self.assertEqual(expected, result.stdout.strip())

    def test_external_title_contract(self):
        contract = re.search(r"^item_contract='\n(.*?)^'", SCRIPT, re.M | re.S)[1]
        audio = {
            "Id": "ext-qobuz-song-1", "Name": "Fixture [A]", "Type": "Audio", "MediaType": "Audio",
            "Album": "Album [Qobuz]", "AlbumId": "album", "Artists": ["Artist [Qobuz]"],
            "ArtistItems": [], "AlbumArtists": [], "RunTimeTicks": 10000000,
            "ImageTags": {"Primary": "image"}, "ProviderIds": {"qobuz": "1"}, "CanDownload": True,
            "UserData": {"Key": "key", "ItemId": "ext-qobuz-song-1"},
            "MediaSources": [{
                "Id": "source", "DirectStreamUrl": "/Audio/fixture/stream", "RunTimeTicks": 10000000,
                "Bitrate": None, "Size": None, "SupportsDirectPlay": True, "SupportsDirectStream": True,
                "MediaStreams": [{"Type": "Audio", "Index": 0, "BitRate": None}],
            }],
        }
        for title, expected in (("Fixture [A]", True), ("Fixture [A]/[E]", True),
                                ("Fixture [S]", False), ("Fixture [Qobuz]", False), ("Fixture [A] [E]", False),
                                ("Fixture", False)):
            with self.subTest(title=title):
                audio["Name"] = title
                result = subprocess.run(["jq", "-e", contract + "external_audio"],
                                        input=json.dumps(audio), text=True, capture_output=True)
                self.assertEqual(result.returncode == 0, expected, result.stderr)
                result = subprocess.run(["jq", "-e", contract + "injected_title"],
                                        input=json.dumps(title), text=True, capture_output=True)
                self.assertEqual(result.returncode == 0, expected, result.stderr)

    def stream(self, path):
        with tempfile.TemporaryDirectory() as directory:
            code = "\n".join([
                "set -euo pipefail",
                "checks=0; failures=0; blocked=0; auth=(); TIMEOUT_SECONDS=1; MAX_EXTERNAL_STREAM_TTFB_MS=1000",
                "response_file=body; direct_headers_file=headers; stream_metrics_file=stream-metrics",
                'block() { blocked=$((blocked + 1)); }',
                function("check_external_stream"),
                'check_external_stream fixture "$FIXTURE_URL"',
                'printf "RESULT %s %s %s %s\\n" "$failures" "$blocked" "$last_stream_ranges_supported" "$last_stream_provider"',
            ])
            result = subprocess.run(["bash", "-c", code], cwd=directory, text=True, capture_output=True,
                                    env={**os.environ, "FIXTURE_URL": f"http://127.0.0.1:{self.server.server_port}{path}"},
                                    timeout=3)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertLessEqual(Path(directory, "body").stat().st_size, 65536)
            return result.stdout

    def test_range_reports_actual_source(self):
        self.assertIn("RESULT 0 0 1 qobuz", self.stream("/range"))

    def test_progressive_retains_only_prefix_and_marks_seek_blocked(self):
        self.assertIn("RESULT 0 1 0 qobuz", self.stream("/progressive"))

    def test_short_progressive_body_fails(self):
        self.assertIn("RESULT 1 0 0", self.stream("/short-progressive"))

    def test_oversized_range_body_fails(self):
        self.assertIn("RESULT 1 0 0", self.stream("/oversized-range"))

    def test_html_is_not_accepted_as_audio(self):
        self.assertIn("RESULT 1 0 0", self.stream("/html"))

    def test_missing_source_header_fails(self):
        self.assertIn("RESULT 1 0 0", self.stream("/missing-provider"))

    def test_timeout_before_headers_terminates_instead_of_hanging(self):
        self.assertIn("RESULT 1 0 0", self.stream("/timeout"))

    def test_dashboard_cookie_session_and_now_playing(self):
        for username, password, expected in (("fixture", "fixture-password", "RESULT 5 0 1"),
                                             ("fixture-admin", "fixture-password", "RESULT 5 0 1"),
                                             ("fixture-foreign", "fixture-password", "RESULT 5 1 1"),
                                             ("fixture", "invalid", "RESULT 1 1 0")):
            with self.subTest(username=username, password=password), tempfile.TemporaryDirectory() as directory:
                code = "\n".join([
                    "set -euo pipefail",
                    "checks=0; failures=0; issued_admin_session=0; TIMEOUT_SECONDS=2",
                    "response_file=body; admin_cookies_file=cookies",
                    function("check_dashboard_session"),
                    "check_dashboard_session",
                    'printf "RESULT %s %s %s\\n" "$checks" "$failures" "$issued_admin_session"',
                ])
                result = subprocess.run(["bash", "-c", code], cwd=directory, text=True, capture_output=True, timeout=5,
                                        env={**os.environ, "ADMIN_BASE": f"http://127.0.0.1:{self.server.server_port}",
                                             "JELLYFIN_USERNAME": username, "JELLYFIN_PASSWORD": password})
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(expected, result.stdout)
                self.assertNotIn(password, result.stdout)


class SearchContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.workspace.cleanup)
        cls.build = Path(cls.workspace.name, "scorer")
        result = subprocess.run(["bash", "-c", function("build_search_contract") +
                                 '\nbuild_search_contract "$SCORER_BUILD"'],
                                text=True, capture_output=True, timeout=90,
                                env={**os.environ, "search_contract_project": str(SEARCH_PROJECT),
                                     "SCORER_BUILD": str(cls.build), "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        if result.returncode:
            log = cls.build / "build.log"
            raise RuntimeError("SearchContract build failed: " + (log.read_text() if log.exists() else result.stderr))
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), StreamFixture)
        cls.server.native_requests = []
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def test_build_ignores_stale_generated_files_with_relocated_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory, "source")
            project = root / "tools/tests/SearchContract/SearchContract.csproj"
            for relative in ["tools/tests/SearchContract/SearchContract.csproj", "tools/tests/SearchContract/Program.cs",
                             "allstarr/Services/Common/SearchRelevanceRanker.cs", "allstarr/Services/Common/FuzzyMatcher.cs"]:
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(SEARCH_PROJECT.parents[3] / relative, target)
            stale = project.parent / "obj/Release/net10.0/Stale.Generated.cs"
            stale.parent.mkdir(parents=True)
            stale.write_text("#error stale generated source must not be compiled\n")
            build = Path(directory, "output")
            result = subprocess.run(["bash", "-c", function("build_search_contract") +
                                     '\nbuild_search_contract "$SCORER_BUILD"'],
                                    text=True, capture_output=True, timeout=90,
                                    env={**os.environ, "search_contract_project": str(project), "SCORER_BUILD": str(build)})
            self.assertEqual(result.returncode, 0, (build / "build.log").read_text())
            self.assertTrue((build / "bin/SearchContract.dll").is_file())

    def score(self, page, local=(), query="Fixture Artist", expected=()):
        capture = {"query": query, "page": envelope(page), "local": envelope(list(local))}
        return self.score_capture(capture, expected)

    def score_capture(self, capture, expected):
        result = subprocess.run(["dotnet", str(self.build / "bin/SearchContract.dll")],
                                input=capture if isinstance(capture, str) else json.dumps(capture),
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(1 if expected else 0, result.returncode, result.stderr)
        output = json.loads(result.stdout)
        self.assertEqual(set(expected), set(output["errors"]))
        self.assertEqual("", result.stderr)
        return output

    def test_relevance_puts_strong_external_before_weak_native(self):
        native = audio("native-weak", "Unrelated", "Other")
        output = self.score([audio("ext-qobuz-song-1"), native], [native])
        self.assertEqual(100, output["scores"][0])
        self.assertGreater(output["scores"][0], output["scores"][1])
        self.assertEqual(1, output["localPreferenceChecks"])

    def test_descending_scores_allow_equal_relevance(self):
        native = audio("native-exact")
        output = self.score([native, audio("ext-qobuz-song-1"), audio("ext-deezer-song-2", "Unrelated")], [native])
        self.assertEqual(output["scores"][0], output["scores"][1])
        self.assertGreater(output["scores"][1], output["scores"][2])

    def test_reversed_relevance_fails(self):
        self.score([audio("native-weak", "Unrelated", "Other"), audio("ext-qobuz-song-1")],
                   expected=["relevance-order"])

    def test_duplicate_same_id_fails(self):
        self.score([audio("ext-qobuz-song-1"), audio("ext-qobuz-song-1")], expected=["duplicate-recording"])

    def test_same_normalized_isrc_across_different_ids_fails(self):
        self.score([audio("ext-qobuz-song-1", isrc="US-AB1-26-12345"),
                    audio("ext-deezer-song-2", isrc="usab12612345")], expected=["duplicate-recording"])

    def test_same_recording_mbid_and_provider_track_id_fail(self):
        for left, right in (({"MusicBrainzTrack": "recording"}, {"MusicBrainzRecording": "recording"}),
                            ({"qobuz": "same-track"}, {"Qobuz": "same-track"})):
            with self.subTest(left=left):
                items = [audio("ext-qobuz-song-1"), audio("ext-deezer-song-2")]
                items[0]["ProviderIds"], items[1]["ProviderIds"] = left, right
                self.score(items, expected=["duplicate-recording"])

    def test_unproven_lookalikes_and_shared_album_ids_remain_distinct(self):
        items = [audio("ext-qobuz-song-1"), audio("ext-deezer-song-2")]
        for item in items:
            item["ProviderIds"] = {"MusicBrainzAlbum": "album", "MusicBrainzArtist": "artist", "AllstarrSource": "provider"}
        self.score(items)

    def test_native_same_isrc_is_preferred(self):
        native = audio("native-copy", isrc="US-AB1-26-12345")
        output = self.score([native], [native])
        self.assertEqual(1, output["localPreferenceChecks"])
        self.score([audio("ext-qobuz-song-1", isrc="usab12612345")], [native],
                   expected=["local-copy-not-preferred"])

    def test_scorer_preserves_source_primary_credit_and_native_first_three_artists(self):
        item = audio("ext-qobuz-song-1", artist="Artist feat. Guest")
        item["Artists"] = ["Artist [Provider]", "Guest [Provider]"]
        output = self.score([item], query="Fixture Artist feat. Guest")
        self.assertEqual([100], output["scores"])
        native = audio("native-copy")
        native["Artists"] = ["Artist", "Second", "Third", "Excluded"]
        output = self.score([native], [native], query="Fixture Artist Second Third")
        self.assertEqual([100], output["scores"])

    def test_invalid_or_incomplete_capture_fails_safely(self):
        valid = {"query": "Fixture Artist", "page": envelope([audio("ext-qobuz-song-1")]), "local": envelope([])}
        cases = ["not json", {}, {**valid, "query": ""}, {**valid, "query": None},
                 {**valid, "local": envelope([], total=1)},
                 {**valid, "page": {**valid["page"], "StartIndex": 5}},
                 {**valid, "page": {**valid["page"], "TotalRecordCount": 0.5}},
                 {**valid, "page": {"Items": valid["page"]["Items"]}}]
        for field, value in (("Id", ""), ("Name", None), ("Type", "MusicAlbum"),
                             ("ProviderIds", None), ("Artists", "Artist")):
            item = {**valid["page"]["Items"][0], field: value}
            cases.append({**valid, "page": envelope([item])})
        for capture in cases:
            with self.subTest(capture=capture):
                self.score_capture(capture, ["invalid-capture"])
        missing_path = audio("ext-qobuz-song-1")
        missing_path.pop("Path")
        self.score([missing_path], expected=["missing-source-metadata"])
        self.score([], expected=["empty-first-page"])

    def capture(self, mode):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(["bash", "-c", "\n".join([
                "set -euo pipefail", "auth=(-H 'Authorization: fixture-auth'); best_user_id=fixture; TIMEOUT_SECONDS=2",
                function("capture_native_search_items"), 'capture_native_search_items native.json .',
            ])], cwd=directory, text=True, capture_output=True, timeout=10,
                env={**os.environ, "DIRECT_BASE": f"http://127.0.0.1:{self.server.server_port}/native/{mode}"})
            output = Path(directory, "native.json")
            return result, json.loads(output.read_text()) if output.exists() else None

    def test_authenticated_native_capture_paginates_without_query_filter(self):
        previous = len(self.server.native_requests)
        result, capture = self.capture("complete")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(501, capture["TotalRecordCount"])
        self.assertEqual("native-500", capture["Items"][-1]["Id"])
        self.assertEqual([0, 500], [offset for _, offset, _ in self.server.native_requests[previous:]])

    def test_native_capture_fails_closed_on_incomplete_or_invalid_pages(self):
        for mode in ("truncated", "changed-total", "duplicate", "over-limit", "invalid", "wrong-offset"):
            with self.subTest(mode=mode):
                result, _ = self.capture(mode)
                self.assertNotEqual(0, result.returncode)

    def live_contract(self, item, mode="complete", build_success=True):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "first.json").write_text(json.dumps(envelope([item])))
            code = "\n".join([
                "set -euo pipefail", "checks=0; failures=0; auth=(-H 'Authorization: fixture-auth'); best_user_id=fixture; TIMEOUT_SECONDS=2",
                function("capture_native_search_items"), function("check_search_contract"),
                'build_search_contract() { mkdir -p "$1"; cp -R "$SCORER_BIN" "$1/bin"; }' if build_success else
                'build_search_contract() { return 1; }',
                'check_search_contract "Fixture Artist" first.json work',
                'printf "RESULT %s %s\\n" "$checks" "$failures"',
            ])
            result = subprocess.run(["bash", "-c", code], cwd=directory, text=True, capture_output=True, timeout=15,
                                    env={**os.environ, "SCORER_BIN": str(self.build / "bin"),
                                         "DIRECT_BASE": f"http://127.0.0.1:{self.server.server_port}/native/{mode}"})
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertNotIn(item["Id"], result.stdout)
            self.assertNotIn("fixture-auth", result.stdout)
            return result.stdout

    def test_live_helper_executes_scorer_and_finds_native_counterpart_outside_query(self):
        self.assertIn("RESULT 1 0", self.live_contract(audio("ext-qobuz-song-1")))
        self.assertIn("RESULT 1 1", self.live_contract(audio("ext-qobuz-song-1", isrc="USAB12612345")))

    def test_live_helper_fails_on_setup_capture_or_scorer_error(self):
        self.assertIn("scorer-setup", self.live_contract(audio("ext-qobuz-song-1"), build_success=False))
        self.assertIn("incomplete-native-capture", self.live_contract(audio("ext-qobuz-song-1"), mode="truncated"))
        invalid = audio("ext-qobuz-song-1")
        invalid.pop("Path")
        self.assertIn("RESULT 1 1", self.live_contract(invalid))


if __name__ == "__main__":
    unittest.main()
