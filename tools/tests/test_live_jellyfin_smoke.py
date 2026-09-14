#!/usr/bin/env python3
"""Offline checks of the live harness, using only a loopback fixture server."""

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


SCRIPT = Path(__file__).with_name("live_jellyfin_smoke.sh").read_text()


def function(name):
    return re.search(rf"^{name}\(\) \{{\n.*?^\}}", SCRIPT, re.M | re.S)[0]


class StreamFixture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path.startswith("/api/admin/"):
            cookie = self.headers.get("Cookie")
            valid_cookie = cookie in ("fixture=admin", "fixture=listener")
            administrator = cookie == "fixture=admin"
            status = 200 if valid_cookie else 401
            if self.path.endswith("auth/me"):
                value = {"authenticated": valid_cookie}
            elif self.path.endswith("ui/home"):
                value = {"schema": {}, "stats": {"cacheTracks": None, "keptTracks": None},
                         "providerHealth": {"providers": []}, "activity": {"items": []}}
            elif administrator:
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
        progressive = self.path == "/progressive"
        body = b"a" * (131072 if progressive else 65536)
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
        valid = login.get("username") in ("fixture", "fixture-admin") and login.get("password") == "fixture-password"
        administrator = login.get("username") == "fixture-admin"
        body = json.dumps({"authenticated": valid, "user": {"isAdministrator": administrator}}).encode()
        self.send_response(200 if valid else 400)
        if valid:
            self.send_header("Set-Cookie", f"fixture={'admin' if administrator else 'listener'}; Path=/; HttpOnly")
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
                "Bitrate": 1000, "Size": 125, "SupportsDirectPlay": True, "SupportsDirectStream": True,
                "MediaStreams": [{"Type": "Audio", "Index": 0, "BitRate": 1000}],
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

    def test_html_is_not_accepted_as_audio(self):
        self.assertIn("RESULT 1 0 0", self.stream("/html"))

    def test_missing_source_header_fails(self):
        self.assertIn("RESULT 1 0 0", self.stream("/missing-provider"))

    def test_timeout_before_headers_terminates_instead_of_hanging(self):
        self.assertIn("RESULT 1 0 0", self.stream("/timeout"))

    def test_dashboard_cookie_session_and_now_playing(self):
        for username, password, expected in (("fixture", "fixture-password", "RESULT 4 0 1"),
                                             ("fixture-admin", "fixture-password", "RESULT 4 0 1"),
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


if __name__ == "__main__":
    unittest.main()
