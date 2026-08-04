#!/usr/bin/env python3

import io
import json
import unittest

from live_subsonic_smoke import (
    MAX_BODY_BYTES,
    SmokeError,
    compatible_variable_response,
    envelope,
    first_id,
    json_root,
    normalize_base_url,
    playlist_matches,
    public_shape,
    read_bounded,
    stateful_steps,
)


class LiveSubsonicSmokeTests(unittest.TestCase):
    def test_envelopes_are_parsed_without_private_payload_output(self):
        body = b'{"subsonic-response":{"status":"failed","error":{"code":40,"message":"secret"}}}'
        observed = envelope(body, "application/json")
        self.assertEqual(("failed", 40), (observed.status, observed.error_code))
        self.assertEqual("failed", json_root(body)["status"])
        xml = envelope(
            b'<subsonic-response status="ok"><license valid="true"/></subsonic-response>',
            "text/xml",
        )
        self.assertTrue(xml.succeeded)

    def test_body_limit_rejects_one_extra_byte(self):
        self.assertEqual(
            MAX_BODY_BYTES, len(read_bounded(io.BytesIO(b"x" * MAX_BODY_BYTES)))
        )
        with self.assertRaises(SmokeError):
            read_bounded(io.BytesIO(b"x" * (MAX_BODY_BYTES + 1)))

    def test_base_url_rejects_embedded_credentials_and_queries(self):
        self.assertEqual(
            "http://127.0.0.1:4533", normalize_base_url("http://127.0.0.1:4533/")
        )
        for value in (
            "http://user:secret@host",
            "https://host?token=secret",
            "file:///tmp/server",
        ):
            with self.subTest(value=value), self.assertRaises(SmokeError):
                normalize_base_url(value)

    def test_dynamic_selection_and_public_shape_do_not_retain_values(self):
        self.assertEqual("song-1", first_id([{"id": "song-1"}]))
        shape = public_shape({"song": {"id": "private-id", "title": "private title"}})
        self.assertNotIn("private", json.dumps(shape))

        direct = {
            "album": {"id": "local-album", "song": [{"id": "local-song"}]}
        }
        enriched = {
            "album": {
                "id": "local-album",
                "song": [{"id": "local-song"}, {"id": "external-song"}],
            }
        }
        self.assertTrue(compatible_variable_response("album", direct, enriched))
        self.assertFalse(compatible_variable_response("album", direct, {"album": {}}))

    def test_cleanup_requires_exact_returned_id_and_name(self):
        root = {"playlist": {"id": "playlist-1", "name": "allstarr-smoke-run"}}
        self.assertTrue(playlist_matches(root, "playlist-1", {"allstarr-smoke-run"}))
        self.assertFalse(playlist_matches(root, "playlist-2", {"allstarr-smoke-run"}))
        self.assertFalse(
            playlist_matches(root, "playlist-1", {"someone-elses-playlist"})
        )
        self.assertEqual(
            (
                "create",
                "add",
                "reread",
                "rename",
                "reorder",
                "empty",
                "delete-exact-id",
                "verify-delete",
            ),
            stateful_steps(),
        )


if __name__ == "__main__":
    unittest.main()
