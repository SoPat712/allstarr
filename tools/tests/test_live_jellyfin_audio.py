#!/usr/bin/env python3
"""Offline checks for the bounded full-song live qualification helpers."""

import tempfile
import unittest
from pathlib import Path

from live_jellyfin_audio import audio_url, flac_samples, parse_headers


class LiveJellyfinAudioTests(unittest.TestCase):
    def test_audio_url_rejects_non_http_and_invalid_ids(self):
        self.assertEqual(
            audio_url("https://example.test", "ext-apple-download-song-123", "user-1"),
            "https://example.test/Audio/ext-apple-download-song-123/stream?static=true&UserId=user-1",
        )
        for base, song in (("file:///tmp", "song"), ("https://example.test", "../song")):
            with self.subTest(base=base, song=song), self.assertRaises(ValueError):
                audio_url(base, song, "user-1")
        for base in ("https://example.test/?token=secret", "https://user:pass@example.test/"):
            with self.subTest(base=base), self.assertRaises(ValueError):
                audio_url(base, "song", "user-1")

    def test_flac_samples_parses_plain_and_prefixed_duration(self):
        sample_rate = 44_100
        total_samples = 132_300
        packed = (sample_rate << 44) | (1 << 41) | (15 << 36) | total_samples
        header = b"fLaC\x00\x00\x00\x22" + b"\x00" * 10 + packed.to_bytes(8, "big")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "audio.flac")
            path.write_bytes(header + b"frames")
            self.assertEqual((sample_rate, total_samples, 0), flac_samples(path))
            path.write_bytes(b"ID3\x04\x00\x00\x00\x00\x00\x00" + header + b"frames")
            self.assertEqual((sample_rate, total_samples, 10), flac_samples(path))

    def test_flac_samples_rejects_invalid_stream(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "audio.flac")
            path.write_bytes(b"not flac")
            with self.assertRaises(AssertionError):
                flac_samples(path)

    def test_headers_reject_redirects(self):
        self.assertEqual(
            parse_headers("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n"),
            {"content-length": "4"},
        )
        with self.assertRaises(AssertionError):
            parse_headers("HTTP/1.1 302 Found\r\n\r\nHTTP/1.1 200 OK\r\n\r\n")


if __name__ == "__main__":
    unittest.main()
