import contextlib
import io
import json
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

import listener_capacity as tool


class ListenerCapacityTests(unittest.TestCase):
    def setUp(self):
        self.requests = []
        self.mode = "ok"
        self.busy_sent = False
        self.starts = 0
        self.failed_seek = False
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass
            def do_GET(self):
                owner.requests.append((self.path, self.headers.get("Range"), self.headers.get("Authorization"), self.headers.get("X-Emby-Device-Id")))
                if owner.mode == "rate-limit":
                    self.send_response(429)
                    self.send_header("Retry-After", "1")
                    self.end_headers()
                    return
                if owner.mode == "busy" and not owner.busy_sent:
                    owner.busy_sent = True
                    self.send_response(503)
                    self.send_header("Retry-After", "0")
                    self.send_header(tool.LOCAL_BUSY_HEADER, "busy")
                    self.end_headers()
                    return
                if self.headers["Range"] == "bytes=0-":
                    owner.starts += 1
                    if owner.mode == "paced-rate" and owner.starts > 2:
                        self.send_response(429)
                        self.end_headers()
                        return
                payload = bytes(index % 251 for index in range(131072))
                requested = self.headers["Range"]
                if owner.mode == "paced-body" and owner.starts > 2 and requested == "bytes=0-":
                    prefix = b"<html>unavailable</html>"
                    payload = prefix + payload[len(prefix):]
                if requested == "bytes=0-":
                    start, end = 0, len(payload) - 1
                    self.send_response(200)
                else:
                    if requested == "bytes=-8192":
                        start, end = len(payload) - 8192, len(payload) - 1
                    else:
                        start, end = (int(part) for part in requested.removeprefix("bytes=").split("-"))
                    if owner.mode == "unknown-missing-seek" and requested != "bytes=-8192" and not owner.failed_seek:
                        owner.failed_seek = True
                        self.send_response(200)
                    else:
                        self.send_response(206)
                    self.send_header("Content-Range", f"bytes {start}-{end}/{len(payload)}")
                self.send_header("Content-Type", "text/html" if owner.mode == "paced-html" and owner.starts > 2 and requested == "bytes=0-" else "audio/mpeg")
                if not (owner.mode == "unknown-missing-seek" and requested == "bytes=0-"):
                    self.send_header("Content-Length", str(end - start + 1))
                self.end_headers()
                try:
                    self.wfile.write(payload[start:end + 1])
                except (BrokenPipeError, ConnectionResetError):
                    pass
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def run_tool(self, extra=(), protocol="jellyfin"):
        credentials = [{"token": "fixture-private-token"}] if protocol == "jellyfin" else [{"username": "fixture-user", "password": "fixture-private-password"}]
        value = {"base_url": f"http://127.0.0.1:{self.server.server_port}", "credentials": credentials,
                 "items": ["ext-deezer-song-private-fixture"]}
        output = io.StringIO()
        code = tool.main(["--input-stdin", "--protocol", protocol, "--listeners", "2", "--duration-seconds", "1", *extra],
                         input_stream=io.StringIO(json.dumps(value)), output=output, environ={})
        text = output.getvalue()
        for secret in ("fixture-private-token", "fixture-private-password", "fixture-user", "private-fixture", "127.0.0.1", "api_key", "?", "http://"):
            self.assertNotIn(secret, text)
        return code, json.loads(text)

    def test_no_network_without_explicit_run(self):
        code, result = self.run_tool()
        self.assertEqual(0, code)
        self.assertFalse(result["run"])
        self.assertEqual([], self.requests)

    def test_exact_query_download_and_independent_seeks_with_a_paced_window(self):
        code, result = self.run_tool(("--run",))
        self.assertEqual(0, code)
        self.assertEqual(2, result["counts"]["startup_successes"])
        self.assertEqual(4, result["counts"]["seek_successes"])
        self.assertEqual(2, result["counts"]["paced_streams"])
        self.assertGreaterEqual(result["elapsed_seconds"], .8)
        self.assertGreater(result["paced_bytes_read"], 0)
        self.assertLessEqual(result["bytes_read"], tool.BYTE_LIMIT)
        ranges = [request[1] for request in self.requests]
        self.assertEqual(4, ranges.count("bytes=0-"))
        self.assertIn("bytes=104857-113048", ranges)
        self.assertIn("bytes=117964-126155", ranges)
        for path, _range, authorization, device in self.requests:
            self.assertIn("api_key", parse_qs(urlsplit(path).query))
            self.assertIsNone(authorization)
            self.assertIsNone(device)

    def test_rate_limit_never_retries(self):
        self.mode = "rate-limit"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertEqual(2, result["counts"]["rate_limited_429"])
        self.assertEqual(2, len(self.requests))
        self.assertEqual(0, result["counts"]["local_retries"])

    def test_marked_local_busy_has_only_one_bounded_retry(self):
        self.mode = "busy"
        code, result = self.run_tool(("--run",))
        self.assertEqual(0, code)
        self.assertEqual(1, result["counts"]["busy_503"])
        self.assertEqual(1, result["counts"]["local_retries"])
        self.assertEqual(1, result["counts"]["retry_after_seen"])
        self.assertLessEqual(len(self.requests), 9)

    def test_paced_rate_limit_cannot_pass_completed_seek_totals(self):
        self.mode = "paced-rate"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertEqual(4, result["counts"]["seek_successes"])
        self.assertTrue(all(not row["paced_window"] for row in result["client_outcomes"]))
        self.assertEqual(0, result["counts"]["local_retries"])

    def test_paced_html_cannot_pass(self):
        self.mode = "paced-html"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertGreater(result["counts"]["protocol_errors"], 0)

    def test_paced_html_body_with_audio_mime_cannot_pass(self):
        self.mode = "paced-body"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertGreater(result["counts"]["protocol_errors"], 0)
        self.assertTrue(any(not row["paced_window"] for row in result["client_outcomes"]))

    def test_invalid_cli_never_echoes_values(self):
        for arguments in (["--token", "fixture-private-token"], ["--protocol", "https://private.invalid/?token=fixture-private-token"]):
            output = io.StringIO()
            with self.assertRaises(SystemExit) as error, contextlib.redirect_stderr(output):
                tool.main(arguments, output=io.StringIO(), environ={})
            self.assertEqual(2, error.exception.code)
            self.assertNotIn("fixture-private-token", output.getvalue())
            self.assertNotIn("private.invalid", output.getvalue())
            self.assertNotIn("?", output.getvalue())
        self.assertEqual([], self.requests)

    def test_suffix_success_cannot_mask_a_missing_far_seek(self):
        self.mode = "unknown-missing-seek"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertGreaterEqual(result["counts"]["seek_successes"], 4)
        self.assertTrue(any(not all(row["far_seeks"]) for row in result["client_outcomes"]))

    def test_subsonic_uses_only_query_credentials(self):
        code, result = self.run_tool(("--run",), protocol="subsonic")
        self.assertEqual(0, code)
        self.assertEqual(4, result["counts"]["seek_successes"])
        self.assertTrue(all(urlsplit(path).path == "/rest/download" for path, *_ in self.requests))

    def test_cli_rejects_more_than_twenty_clients(self):
        with self.assertRaises(SystemExit) as error, contextlib.redirect_stderr(io.StringIO()):
            tool.main(["--listeners", "21"], output=io.StringIO(), environ={})
        self.assertEqual(2, error.exception.code)
        self.assertEqual([], self.requests)


if __name__ == "__main__":
    unittest.main()
