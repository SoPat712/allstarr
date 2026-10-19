import json
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch
from urllib.parse import parse_qs, quote, urlsplit

from session_verifier.protocol import allowed_resource, callback_with_grant, challenge_context, grant_from_response
from session_verifier.server import Handler

CALLBACK = "spotiflac://session-grant?cb_version=v2grant&state=fixture"
CHALLENGE = "https://api.zarz.moe/v2/challenge?id=fixture&cb=" + quote(CALLBACK, safe="")


class ProtocolTests(unittest.TestCase):
    def test_challenge_and_callback_are_bound_to_the_service(self):
        callback, verify = challenge_context(CHALLENGE)
        self.assertEqual(CALLBACK, callback)
        self.assertEqual("https://api.zarz.moe/v2/challenge/verify", verify)
        query = parse_qs(urlsplit(callback_with_grant(callback, "fixture-grant")).query)
        self.assertEqual(["fixture"], query["state"])
        self.assertEqual(["fixture-grant"], query["grant"])

    def test_rejects_other_origins_ports_paths_and_callbacks(self):
        for url in [
            CHALLENGE.replace("https:", "http:"),
            CHALLENGE.replace("api.zarz.moe", "127.0.0.1"),
            CHALLENGE.replace("api.zarz.moe", "api.zarz.moe.evil.test"),
            CHALLENGE.replace("api.zarz.moe", "api.zarz.moe:444"),
            CHALLENGE.replace("api.zarz.moe", "user@api.zarz.moe"),
            CHALLENGE.replace("/challenge?", "/other?"),
            CHALLENGE.replace("spotiflac", "https"),
            CHALLENGE + "&cb=duplicate",
            "https://api.zarz.moe/v2/challenge",
        ]:
            with self.subTest(url=url), self.assertRaises(ValueError):
                challenge_context(url)

    def test_browser_resources_cannot_reach_private_or_unapproved_hosts(self):
        for url in ["http://127.0.0.1/", "https://192.168.1.1/", "file:///etc/passwd", "https://example.test/", "https://api.zarz.moe:123/"]:
            self.assertFalse(allowed_resource(url))
        self.assertTrue(allowed_resource("https://challenges.cloudflare.com/turnstile/v0/api.js"))
        self.assertTrue(allowed_resource(CHALLENGE))

    def test_only_grant_from_exact_verification_response_is_accepted(self):
        _, verify = challenge_context(CHALLENGE)
        self.assertEqual("fixture-grant", grant_from_response(verify, verify, '{"grant":"fixture-grant"}'))
        for url, body in [
            (verify + "?other=1", '{"grant":"wrong"}'),
            ("https://other.test/verify", '{"grant":"wrong"}'),
            (verify, '{"token":"not-a-grant"}'),
            (verify, '{"code":"not-a-grant"}'),
            (verify, "not json"),
            (verify, "[]"),
            (verify, " " * (16 * 1024 + 1)),
        ]:
            self.assertIsNone(grant_from_response(url, verify, body))

    def test_chromium_canonicalization_preserves_default_port_and_host_case(self):
        _, verify = challenge_context(CHALLENGE.replace("api.zarz.moe", "API.ZARZ.MOE:443"))
        self.assertEqual("https://api.zarz.moe/v2/challenge/verify", verify)
        self.assertEqual("fixture", grant_from_response("https://API.ZARZ.MOE:443/v2/challenge/verify", verify, '{"grant":"fixture"}'))

    def test_grant_validation_rejects_controls_and_unbounded_values(self):
        for grant in ["", "\nvalue", "x" * 8193, None]:
            with self.assertRaises(ValueError):
                callback_with_grant(CALLBACK, grant)


class ServerTests(unittest.TestCase):
    def setUp(self):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = "http://127.0.0.1:" + str(self.server.server_port)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def post(self, value):
        return urllib.request.urlopen(urllib.request.Request(self.url + "/api/verify", json.dumps(value).encode(), {"Content-Type": "application/json"}))

    def test_health_does_not_start_a_browser(self):
        with patch("session_verifier.server.run_browser") as browser:
            with urllib.request.urlopen(self.url + "/api/health") as response:
                self.assertEqual(200, response.status)
            browser.assert_not_called()

    def test_supported_request_returns_only_callback(self):
        with patch("session_verifier.server.run_browser", return_value={"callback_url": callback_with_grant(CALLBACK, "fixture-grant")}):
            with self.post({"verification_url": CHALLENGE}) as response:
                self.assertEqual("no-store", response.headers["Cache-Control"])
                self.assertEqual(["callback_url"], list(json.load(response)))

    def test_bad_request_never_starts_browser(self):
        with patch("session_verifier.server.run_browser") as browser:
            with self.assertRaises(urllib.error.HTTPError) as error:
                self.post({"verification_url": "http://127.0.0.1/private"})
            self.assertEqual(400, error.exception.code)
            browser.assert_not_called()

    def test_browser_errors_are_redacted(self):
        with patch("session_verifier.server.run_browser", side_effect=RuntimeError("private-grant-value")):
            with self.assertRaises(urllib.error.HTTPError) as error:
                self.post({"verification_url": CHALLENGE})
            self.assertEqual(503, error.exception.code)
            self.assertNotIn("private-grant-value", error.exception.read().decode())


if __name__ == "__main__":
    unittest.main()
