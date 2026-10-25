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
        self.slot_lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(6)
        self.active_slots = self.peak_slots = 0
        self.active_paced = self.peak_paced = 0
        self.paced_overlap_started = self.paced_overlap_ended = None
        self.client_ranges = {}
        self.active_items = set()
        self.overlapping_items = set()
        self.phase_barriers = {phase: threading.Barrier(5) for phase in ("first-seek", "second-seek", "paced")}
        self.payload = bytes(index % 251 for index in range(131072))
        self.limited_payload = self.payload * 8
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass
            def do_GET(self):
                owner.requests.append((self.path, self.headers.get("Range"), self.headers.get("Authorization"), self.headers.get("X-Emby-Device-Id")))
                if owner.mode == "six-slots":
                    self.serve_limited()
                    return
                if owner.mode == "rate-limit":
                    self.send_response(429)
                    self.send_header("Retry-After", "1")
                    self.end_headers()
                    return
                if owner.mode == "busy-forever" or owner.mode == "busy-seek" and self.headers["Range"] == "bytes=104857-113048":
                    self.send_busy()
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
            def send_busy(self):
                self.send_response(503)
                self.send_header("Retry-After", "0")
                self.send_header(tool.LOCAL_BUSY_HEADER, "busy")
                self.end_headers()

            def serve_limited(self):
                if not owner.slots.acquire(blocking=False):
                    self.send_busy()
                    return
                item = urlsplit(self.path).path.split("/")[2]
                requested = self.headers["Range"]
                with owner.slot_lock:
                    owner.active_slots += 1
                    owner.peak_slots = max(owner.peak_slots, owner.active_slots)
                    if item in owner.active_items:
                        owner.overlapping_items.add(item)
                    owner.active_items.add(item)
                    history = owner.client_ranges.setdefault(item, [])
                    startup = not history
                    history.append(requested)
                phase = "startup" if startup else "paced" if requested == "bytes=0-" else (
                    "first-seek" if requested == f"bytes={len(owner.limited_payload) * 4 // 5}-{len(owner.limited_payload) * 4 // 5 + 8191}" else "second-seek")
                held = True
                paced = False
                def release():
                    nonlocal held, paced
                    if held:
                        with owner.slot_lock:
                            owner.active_slots -= 1
                            owner.active_items.discard(item)
                            if paced:
                                owner.active_paced -= 1
                                if owner.paced_overlap_started is not None and owner.paced_overlap_ended is None:
                                    owner.paced_overlap_ended = time.monotonic()
                        owner.slots.release()
                        held = False
                try:
                    if phase != "startup":
                        owner.phase_barriers[phase].wait(timeout=1)
                    if phase == "paced":
                        with owner.slot_lock:
                            paced = True
                            owner.active_paced += 1
                            owner.peak_paced = max(owner.peak_paced, owner.active_paced)
                            if owner.active_paced == 5:
                                owner.paced_overlap_started = time.monotonic()
                    start, end = 0, len(owner.limited_payload) - 1
                    if phase in ("first-seek", "second-seek"):
                        start, end = (int(part) for part in requested.removeprefix("bytes=").split("-"))
                    self.send_response(206)
                    self.send_header("Content-Range", f"bytes {start}-{end}/{len(owner.limited_payload)}")
                    self.send_header("Content-Type", "audio/mpeg")
                    self.send_header("Content-Length", str(end - start + 1))
                    self.end_headers()
                    if phase == "startup":
                        # The startup reader deliberately aborts after this prefix.
                        release()
                        self.wfile.write(owner.limited_payload[:4096])
                    elif phase == "paced":
                        for offset in range(0, len(owner.limited_payload), 4096):
                            self.wfile.write(owner.limited_payload[offset:offset + 4096])
                            self.wfile.flush()
                            time.sleep(4096 / (2 * tool.PACE))
                    else:
                        # Release before the final write makes the next request's admission deterministic.
                        release()
                        self.wfile.write(owner.limited_payload[start:end + 1])
                except threading.BrokenBarrierError:
                    self.send_busy()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                finally:
                    release()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def run_tool(self, extra=(), protocol="jellyfin", listeners=2, duration=1, items=None):
        credentials = [{"token": "fixture-private-token"}] if protocol == "jellyfin" else [{"username": "fixture-user", "password": "fixture-private-password"}]
        value = {"base_url": f"http://127.0.0.1:{self.server.server_port}", "credentials": credentials,
                 "items": items or ["ext-deezer-song-private-fixture"]}
        output = io.StringIO()
        code = tool.main(["--input-stdin", "--protocol", protocol, "--listeners", str(listeners), "--duration-seconds", str(duration), *extra],
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

    def test_five_parallel_clients_complete_sequential_seeks_and_windows_with_six_credential_slots(self):
        self.mode = "six-slots"
        items = [f"ext-deezer-song-private-fixture-{index}" for index in range(5)]
        code, result = self.run_tool(("--run",), listeners=5, duration=2, items=items)
        self.assertEqual(0, code)
        self.assertEqual(1, result["credential_sets"])
        self.assertEqual(5, result["counts"]["startup_successes"])
        self.assertEqual(10, result["counts"]["seek_successes"])
        self.assertEqual(5, result["counts"]["paced_streams"])
        self.assertEqual(0, result["counts"]["busy_503"])
        self.assertEqual(0, result["counts"]["local_retries"])
        self.assertEqual(20, result["counts"]["requests"])
        self.assertTrue(all(row["startup"] and all(row["far_seeks"]) and row["paced_window"] for row in result["client_outcomes"]))
        self.assertGreaterEqual(result["elapsed_seconds"], 1.8)
        self.assertLessEqual(result["bytes_read"], tool.BYTE_LIMIT)
        self.assertLessEqual(self.peak_slots, 6)
        self.assertEqual(5, self.peak_paced)
        self.assertEqual(set(), self.overlapping_items)
        self.assertEqual(5, len(self.client_ranges))
        first, second = len(self.limited_payload) * 4 // 5, len(self.limited_payload) * 9 // 10
        for history in self.client_ranges.values():
            self.assertEqual(["bytes=0-", f"bytes={first}-{first + 8191}", f"bytes={second}-{second + 8191}", "bytes=0-"], history)
        deadline = time.monotonic() + 2
        while self.paced_overlap_ended is None and time.monotonic() < deadline:
            time.sleep(.01)
        self.assertIsNotNone(self.paced_overlap_started)
        self.assertIsNotNone(self.paced_overlap_ended)
        self.assertGreater(self.paced_overlap_ended - self.paced_overlap_started, 1.5)

    def test_unrecovered_local_busy_fails_after_only_one_retry_per_client(self):
        self.mode = "busy-forever"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertEqual(4, result["counts"]["requests"])
        self.assertEqual(4, result["counts"]["busy_503"])
        self.assertEqual(2, result["counts"]["local_retries"])
        self.assertTrue(all(not row["startup"] and not row["paced_window"] for row in result["client_outcomes"]))

    def test_a_later_successful_seek_cannot_hide_an_unrecovered_failed_seek(self):
        self.mode = "busy-seek"
        code, result = self.run_tool(("--run",))
        self.assertEqual(1, code)
        self.assertEqual(2, result["counts"]["seek_successes"])
        self.assertEqual(4, result["counts"]["busy_503"])
        self.assertEqual(2, result["counts"]["local_retries"])
        self.assertTrue(all(row["far_seeks"] == [False, True] and not row["paced_window"] for row in result["client_outcomes"]))

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
