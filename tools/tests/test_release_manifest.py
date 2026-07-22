import json
import pathlib
import re
import subprocess
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]


class ReleaseManifestTests(unittest.TestCase):
    def test_manifest_is_machine_readable_and_uses_canonical_version(self) -> None:
        completed = subprocess.run(
            ["bash", "tools/create-release-manifest.sh"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        manifest = json.loads(completed.stdout)

        app_version_source = (ROOT / "allstarr" / "AppVersion.cs").read_text(
            encoding="utf-8"
        )
        version_match = re.search(r'Version\s*=\s*"([^"]+)"', app_version_source)
        self.assertIsNotNone(version_match)
        self.assertEqual(version_match.group(1), manifest["applicationVersion"])
        self.assertEqual(1, manifest["schemaVersion"])

        self.assertRegex(manifest["git"]["commit"], r"^[0-9a-f]{40}$")
        self.assertIsInstance(manifest["git"]["trackedFilesDirty"], bool)

        expected_digests = {
            "databaseMigrationsSha256",
            "composeFilesSha256",
            "firstPartyExtensionLocksSha256",
            "appleGatewayLocksSha256",
            "webUiPackageLockSha256",
        }
        self.assertEqual(expected_digests, set(manifest["digests"]))
        for digest in manifest["digests"].values():
            self.assertRegex(digest, r"^[0-9a-f]{64}$")


if __name__ == "__main__":
    unittest.main()
