import json
import os
import signal
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from .protocol import MAXIMUM_BYTES, challenge_context

BUSY = threading.Lock()


def run_browser(payload: bytes) -> dict:
    process = subprocess.Popen(
        [sys.executable, "-m", "session_verifier.browser"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, start_new_session=True,
    )
    try:
        output, _ = process.communicate(payload, timeout=105)
        if process.returncode or len(output) > MAXIMUM_BYTES:
            raise RuntimeError("verification_unavailable")
        response = json.loads(output)
        if not isinstance(response.get("callback_url"), str):
            raise RuntimeError("verification_unavailable")
        return response
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()


class Handler(BaseHTTPRequestHandler):
    timeout = 10

    def log_message(self, *_):
        pass

    def reply(self, status: int, value: dict):
        body = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.reply(200 if self.path == "/api/health" else 404, {"status": "ready"} if self.path == "/api/health" else {"error": "not_found"})

    def do_POST(self):
        if self.path != "/api/verify":
            self.reply(404, {"error": "not_found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= MAXIMUM_BYTES or self.headers.get_content_type() != "application/json":
                raise ValueError("invalid_request")
            payload = self.rfile.read(length)
            challenge_context(json.loads(payload)["verification_url"])
        except (ValueError, KeyError, TypeError):
            self.reply(400, {"error": "unsupported_challenge"})
            return
        if not BUSY.acquire(blocking=False):
            self.reply(429, {"error": "verification_busy"})
            return
        try:
            self.reply(200, run_browser(payload))
        except Exception:
            self.reply(503, {"error": "verification_unavailable"})
        finally:
            BUSY.release()


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()
