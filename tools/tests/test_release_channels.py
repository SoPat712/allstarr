import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]


def workflow_script(name: str) -> str:
    workflow = (ROOT / ".github/workflows/docker.yml").read_text()
    step = re.search(
        r"(?ms)^      - name: " + re.escape(name) + r"\n(.*?)(?=^      - name:|\Z)",
        workflow,
    )
    if step is None:
        raise AssertionError(f"Missing workflow step: {name}")
    return textwrap.dedent(step.group(1).split("        run: |\n", 1)[1])


class ReleaseChannelTests(unittest.TestCase):
    def classify(self, version: str, ref_type: str, ref_name: str):
        script = workflow_script("Read canonical application version")
        with tempfile.TemporaryDirectory() as directory:
            temporary = pathlib.Path(directory)
            (temporary / "allstarr").mkdir()
            (temporary / "allstarr/AppVersion.cs").write_text(
                f'public const string Version = "{version}";\n'
            )
            output = temporary / "outputs"
            result = subprocess.run(
                ["bash", "-c", script],
                cwd=temporary,
                env=dict(
                    os.environ,
                    GITHUB_REF_TYPE=ref_type,
                    GITHUB_REF_NAME=ref_name,
                    GITHUB_OUTPUT=str(output),
                ),
                text=True,
                capture_output=True,
            )
            values = dict(
                line.split("=", 1)
                for line in output.read_text().splitlines()
                if "=" in line
            ) if output.exists() else {}
        return result, values

    def test_matching_release_tag_selects_stable(self):
        result, values = self.classify("3.1.0", "tag", "v3.1.0")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("3.1.0", values.get("version"))
        self.assertEqual("true", values.get("stable-release"))

    def test_branch_builds_do_not_move_stable(self):
        for branch in ("dev", "main", "beta"):
            with self.subTest(branch=branch):
                result, values = self.classify("3.1.0", "branch", branch)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("false", values.get("stable-release"))

    def test_preview_or_noncanonical_numeric_tags_do_not_move_stable(self):
        for version in ("3.1.0-beta.2", "3.1.0-rc.1", "3.1.0+build.1", "03.1.0"):
            with self.subTest(version=version):
                result, values = self.classify(version, "tag", "v" + version)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("false", values.get("stable-release"))

    def test_mismatched_version_tag_cannot_publish(self):
        result, values = self.classify("3.1.0", "tag", "v3.1.1")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual("true", values.get("stable-release"))


class StablePromotionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(["bash", "-c", 'test "${BASH_VERSINFO[0]}" -ge 4'])
        if result.returncode:
            raise unittest.SkipTest("The publication workflow requires the runner's Bash 4 or newer.")

    def publish(self, promote: bool, unverified: bool = False):
        digest = "sha256:" + "a" * 64
        tags = ["ghcr.io/example/music:3.1.0", "ghcr.io/example/music:v3.1.0"]
        with tempfile.TemporaryDirectory() as directory:
            temporary = pathlib.Path(directory)
            calls = temporary / "calls"
            promoted = temporary / "promoted"
            docker = temporary / "docker"
            docker.write_text("#!" + sys.executable + "\n" + textwrap.dedent("""\
                import json,os,pathlib,sys
                arguments=sys.argv[1:]
                with pathlib.Path(os.environ['CALLS']).open('a') as output:
                    output.write(json.dumps(arguments)+'\\n')
                if arguments[:3]==['buildx','imagetools','inspect']:
                    image=arguments[3]
                    if image.endswith(':stable'):
                        if not pathlib.Path(os.environ['PROMOTED']).exists(): sys.exit(1)
                    digest=os.environ['PUBLISHED_DIGEST']
                    if os.environ['UNVERIFIED']=='true' and image.endswith(':v3.1.0'):
                        digest='sha256:'+'b'*64
                    print('Digest: '+digest)
                elif arguments[:4]==['buildx','imagetools','create','--tag']:
                    assert arguments[4]=='ghcr.io/example/music:stable'
                    assert arguments[5]=='ghcr.io/example/music@'+os.environ['PUBLISHED_DIGEST']
                    pathlib.Path(os.environ['PROMOTED']).write_text(arguments[5])
                else:
                    sys.exit(1)
                """))
            docker.chmod(0o755)
            sleep = temporary / "sleep"
            sleep.write_text("#!/bin/sh\nexit 0\n")
            sleep.chmod(0o755)
            result = subprocess.run(
                ["bash", "-c", workflow_script("Verify published manifest digest")],
                cwd=temporary,
                env=dict(
                    os.environ,
                    PATH=str(temporary) + os.pathsep + os.environ["PATH"],
                    PUBLISHED_DIGEST=digest,
                    PUBLISHED_TAGS="\n".join(tags),
                    PROMOTE_STABLE="true" if promote else "false",
                    CALLS=str(calls),
                    PROMOTED=str(promoted),
                    UNVERIFIED="true" if unverified else "false",
                ),
                capture_output=True,
                text=True,
            )
            log = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            source = promoted.read_text() if promoted.exists() else None
        return result, log, source, digest, tags

    def test_verified_release_promotes_the_same_digest_after_all_tags_verify(self):
        result, calls, source, digest, tags = self.publish(True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("ghcr.io/example/music@" + digest, source)
        create = next(index for index, call in enumerate(calls) if call[2] == "create")
        self.assertEqual(tags, [call[3] for call in calls[:create]])
        self.assertEqual("ghcr.io/example/music:stable", calls[-1][3])

    def test_unverified_version_cannot_move_stable(self):
        result, calls, source, _, _ = self.publish(True, unverified=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIsNone(source)
        self.assertFalse(any(call[2] == "create" for call in calls))

    def test_branch_or_preview_does_not_promote_even_after_verification(self):
        result, calls, source, _, _ = self.publish(False)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIsNone(source)
        self.assertFalse(any(call[2] == "create" for call in calls))


if __name__ == "__main__":
    unittest.main()
