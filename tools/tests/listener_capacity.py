#!/usr/bin/env python3
"""Explicitly opted-in, bounded original-audio concurrency and seek check."""
import argparse
import concurrent.futures
import http.client
import io
import json
import math
import os
import queue
import socket
import statistics
import sys
import threading
import time
import urllib.parse

MAX_CLIENTS = 20
MAX_DURATION = 120
BYTE_LIMIT = 32 * 1024 * 1024
PACE = 48 * 1024
LOCAL_BUSY_HEADER = "X-Allstarr-Capacity"


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, _message):
        self.exit(2, "Invalid command line; use --help for supported options.\n")


class SafeInputError(Exception):
    pass


def bounded_int(low, high):
    def parse(value):
        try:
            result = int(value)
        except ValueError:
            raise argparse.ArgumentTypeError("Use a whole number.") from None
        if not low <= result <= high:
            raise argparse.ArgumentTypeError(f"Use a value from {low} to {high}.")
        return result
    return parse


def load_inputs(args, source, environ):
    try:
        if args.input_stdin:
            raw = source.read(65537)
            if len(raw) > 65536:
                raise SafeInputError("Input exceeds its limit.")
            value = json.loads(raw)
            base, credentials, items = value["base_url"], value["credentials"], value["items"]
        else:
            base = environ.get("ALLSTARR_LOAD_BASE_URL", "")
            credentials = json.loads(environ.get("ALLSTARR_LOAD_CREDENTIALS_JSON", "[]"))
            items = json.loads(environ.get("ALLSTARR_LOAD_ITEM_IDS_JSON", "[]"))
        parsed = urllib.parse.urlsplit(base)
        if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise SafeInputError("Use an HTTP or HTTPS base address without credentials, query or fragment.")
        if not isinstance(credentials, list) or not 1 <= len(credentials) <= MAX_CLIENTS:
            raise SafeInputError("Supply one to twenty credential sets through environment or stdin.")
        fields = ("token",) if args.protocol == "jellyfin" else ("username", "password")
        if any(not isinstance(row, dict) or any(not isinstance(row.get(key), str) or not row[key] or len(row[key]) > 4096 for key in fields) for row in credentials):
            raise SafeInputError("A credential set is incomplete or invalid.")
        if not isinstance(items, list) or not 1 <= len(items) <= MAX_CLIENTS or any(
            not isinstance(item, str) or len(item) > 500 or not item.startswith(("ext-deezer-song-", "ext-monochrome-song-")) or
            any(ord(char) < 32 for char in item) for item in items):
            raise SafeInputError("Supply one to twenty authorized direct Deezer or Monochrome song identifiers through environment or stdin.")
        return parsed, credentials, items
    except SafeInputError:
        raise
    except (ValueError, KeyError, TypeError, AttributeError):
        raise SafeInputError("The nonlogged input document is invalid.") from None


def resolve(parsed):
    results = queue.Queue(maxsize=1)
    def lookup():
        try:
            results.put(socket.getaddrinfo(parsed.hostname, parsed.port or (443 if parsed.scheme == "https" else 80), type=socket.SOCK_STREAM)[0])
        except Exception:
            results.put(None)
    threading.Thread(target=lookup, daemon=True).start()
    try:
        address = results.get(timeout=5)
    except queue.Empty:
        address = None
    if address is None:
        raise SafeInputError("The server address could not be resolved within the time limit.")
    return address


class Summary:
    def __init__(self):
        self.lock = threading.Lock()
        self.counts = {name: 0 for name in ("requests", "successes", "busy_503", "unavailable_503", "rate_limited_429", "retry_after_seen", "local_retries", "transport_errors", "protocol_errors", "startup_successes", "seek_successes", "paced_streams", "paced_eof")}
        self.startup, self.seeks = [], []
        self.bytes_read = 0
        self.claimed = 0
        self.paced_bytes = 0
        self.exhausted = False

    def add(self, name, amount=1):
        with self.lock:
            self.counts[name] += amount

    def claim(self, wanted):
        with self.lock:
            count = min(wanted, BYTE_LIMIT - self.claimed)
            self.claimed += count
            if count == 0:
                self.exhausted = True
            return count

    def observed(self, reserved, actual, paced=False):
        with self.lock:
            self.claimed -= reserved - actual
            self.bytes_read += actual
            if paced:
                self.paced_bytes += actual

    def timing(self, name, value):
        with self.lock:
            (self.startup if name == "startup" else self.seeks).append(value)


def connection(parsed, address, timeout):
    cls = http.client.HTTPSConnection if parsed.scheme == "https" else http.client.HTTPConnection
    client = cls(parsed.hostname, parsed.port, timeout=timeout)
    def connect(_target, timeout, source_address=None):
        family, kind, protocol, _, target = address
        sock = socket.socket(family, kind, protocol)
        try:
            sock.settimeout(timeout)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8192)
            sock.connect(target)
            return sock
        except Exception:
            sock.close()
            raise
    client._create_connection = connect
    return client


def range_info(value):
    try:
        unit, rest = value.split(" ", 1)
        offsets, total = rest.split("/", 1)
        first, last = offsets.split("-", 1)
        return (int(first), int(last), int(total)) if unit == "bytes" else None
    except (ValueError, AttributeError):
        return None


def read_bounded(response, count, summary, paced=False):
    reserved = summary.claim(count)
    if reserved == 0:
        return b""
    try:
        data = response.read(reserved)
    except Exception:
        summary.observed(reserved, 0, paced)
        raise
    summary.observed(reserved, len(data), paced)
    return data


def request(parsed, address, path, header, deadline, summary, handle):
    for attempt in range(2):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        client = connection(parsed, address, min(8, remaining))
        response = None
        try:
            summary.add("requests")
            client.request("GET", path, headers={"Range": header})
            response = client.getresponse()
            retry = response.getheader("Retry-After")
            if retry is not None:
                summary.add("retry_after_seen")
            if response.status == 429:
                summary.add("rate_limited_429")
                return None
            if response.status == 503:
                local = response.getheader(LOCAL_BUSY_HEADER) == "busy"
                summary.add("busy_503" if local else "unavailable_503")
                try:
                    wait = int(retry)
                except (ValueError, TypeError):
                    wait = -1
                if local and attempt == 0 and 0 <= wait <= 5 and time.monotonic() + wait < deadline:
                    response.close()
                    client.close()
                    summary.add("local_retries")
                    time.sleep(wait)
                    continue
                return None
            if response.status not in (200, 206):
                summary.add("protocol_errors")
                return None
            result = handle(response)
            summary.add("successes")
            return result
        except (TimeoutError, OSError, http.client.HTTPException):
            summary.add("transport_errors")
            return None
        except ValueError:
            summary.add("protocol_errors")
            return None
        finally:
            if response is not None:
                response.close()
            client.close()
    return None


def client_session(args, parsed, address, credential, item, deadline, summary):
    if args.protocol == "jellyfin":
        route = "/Items/" + urllib.parse.quote(item, safe="") + "/Download"
        parameters = {"api_key": credential["token"]}
    else:
        route = "/rest/download"
        parameters = {"id": item, "u": credential["username"], "p": credential["password"], "v": "1.16.1", "c": "allstarr-load", "f": "json"}
    path = parsed.path.rstrip("/") + route + "?" + urllib.parse.urlencode(parameters)
    outcome = {"startup": False, "far_seeks": [False, False], "paced_window": False, "paced_eof": False}
    started = time.monotonic()
    def audio(response):
        media = (response.getheader("Content-Type") or "").split(";", 1)[0]
        if not (media.startswith("audio/") or media == "application/octet-stream"):
            raise ValueError()
    def startup(response):
        audio(response)
        content = range_info(response.getheader("Content-Range"))
        total = content[2] if content is not None else int(response.getheader("Content-Length", "0"))
        if response.status == 206 and (content is None or content != (0, total - 1, total) or total <= 0):
            raise ValueError()
        data = read_bounded(response, 4096, summary)
        if len(data) != 4096 or data.lstrip().startswith((b"<", b"{", b"[")):
            raise ValueError()
        summary.add("startup_successes")
        summary.timing("startup", (time.monotonic() - started) * 1000)
        return total
    total = request(parsed, address, path, "bytes=0-", deadline, summary, startup)
    if total is None:
        return outcome
    outcome["startup"] = True
    def seek(header, offset=None):
        began = time.monotonic()
        def handle(response):
            audio(response)
            bounds = range_info(response.getheader("Content-Range"))
            if response.status != 206 or bounds is None:
                raise ValueError()
            first, last, length = bounds
            expected = length - 8192 if offset is None else offset
            if length <= 16384 or offset is not None and length != total:
                raise ValueError()
            if first != expected or last != expected + 8191 or int(response.getheader("Content-Length", "-1")) != 8192:
                raise ValueError()
            if len(read_bounded(response, 8192, summary)) != 8192:
                raise ValueError()
            summary.add("seek_successes")
            summary.timing("seek", (time.monotonic() - began) * 1000)
            return length
        return request(parsed, address, path, header, deadline, summary, handle)
    if total == 0:
        total = seek("bytes=-8192")
    if total is None or total <= 16384:
        return outcome
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as seeks:
        futures = [seeks.submit(seek, f"bytes={offset}-{offset + 8191}", offset) for offset in (total * 4 // 5, total * 9 // 10)]
        outcome["far_seeks"] = [future.result() == total for future in futures]
    if not all(outcome["far_seeks"]):
        return outcome
    def paced(response):
        audio(response)
        bounds = range_info(response.getheader("Content-Range"))
        if response.status == 206 and bounds != (0, total - 1, total):
            raise ValueError()
        if int(response.getheader("Content-Length", str(total))) != total:
            raise ValueError()
        summary.add("paced_streams")
        next_read = time.monotonic()
        first_chunk = True
        while time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            response.fp.raw._sock.settimeout(min(8, remaining))
            data = read_bounded(response, 4096, summary, paced=True)
            if not data:
                summary.add("paced_eof")
                outcome["paced_eof"] = True
                return False
            if first_chunk and data.lstrip().lower().startswith((b"<!doctype html", b"<html", b"<?xml", b'{"')):
                raise ValueError()
            first_chunk = False
            next_read += 4096 / PACE
            time.sleep(max(0, min(next_read - time.monotonic(), deadline - time.monotonic())))
        return True
    outcome["paced_window"] = request(parsed, address, path, "bytes=0-", deadline, summary, paced) is True
    return outcome


def timings(values):
    if not values:
        return {"samples": 0}
    ordered = sorted(values)
    return {"samples": len(values), "median": round(statistics.median(values), 2),
            "p95": round(ordered[max(0, math.ceil(len(values) * .95) - 1)], 2), "maximum": round(max(values), 2)}


def main(argv=None, input_stream=None, output=None, environ=None):
    parser = SafeArgumentParser(description=__doc__)
    parser.add_argument("--run", action="store_true", help="Explicitly allow the bounded network run.")
    parser.add_argument("--input-stdin", action="store_true")
    parser.add_argument("--protocol", choices=("jellyfin", "subsonic"), default="jellyfin")
    parser.add_argument("--listeners", type=bounded_int(1, MAX_CLIENTS), default=5, help="Parallel clients; identity comes from the supplied credentials.")
    parser.add_argument("--duration-seconds", type=bounded_int(1, MAX_DURATION), default=30)
    args = parser.parse_args(argv)
    output = output or sys.stdout
    try:
        parsed, credentials, items = load_inputs(args, input_stream or sys.stdin, os.environ if environ is None else environ)
        if not args.run:
            print(json.dumps({"run": False, "clients": args.listeners, "credential_sets": len(credentials), "window_seconds": args.duration_seconds}), file=output)
            return 0
        address = resolve(parsed)
        summary = Summary()
        began = time.monotonic()
        deadline = began + args.duration_seconds
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.listeners) as clients:
            tasks = [clients.submit(client_session, args, parsed, address, credentials[index % len(credentials)], items[index % len(items)], deadline, summary)
                     for index in range(args.listeners)]
            outcomes = [task.result() for task in tasks]
        result = {"run": True, "protocol": args.protocol, "clients": args.listeners, "credential_sets": len(credentials),
                  "window_seconds": args.duration_seconds, "elapsed_seconds": round(time.monotonic() - began, 3),
                  "bytes_read": summary.bytes_read, "byte_limit": BYTE_LIMIT, "byte_budget_exhausted": summary.exhausted,
                  "paced_bytes_read": summary.paced_bytes, "paced_bytes_per_second_per_client": PACE,
                  "startup_ms": timings(summary.startup), "seek_ms": timings(summary.seeks), "counts": summary.counts, "client_outcomes": outcomes}
        print(json.dumps(result, sort_keys=True), file=output)
        return 0 if all(row["startup"] and all(row["far_seeks"]) and row["paced_window"] for row in outcomes) and not any(
            summary.counts[name] for name in ("rate_limited_429", "unavailable_503", "protocol_errors", "transport_errors")) and not summary.exhausted else 1
    except SafeInputError as error:
        print(json.dumps({"error": str(error)}), file=output)
        return 2
    except Exception:
        print(json.dumps({"error": "The bounded run could not complete."}), file=output)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
