import hashlib, json, subprocess, sys, tempfile, unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/"tools"))
import first_party_bundle as bundle

class FirstPartyBundleTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.root=Path(self.temp.name); self.output=self.root/"dist"
        provider=self.root/"first-party/providers/fixture"; provider.mkdir(parents=True)
        (provider/"manifest.json").write_text(json.dumps({"id":"fixture","displayName":"Fixture","version":"1.2.3","sdkVersion":"1","entryPoint":"index.js","capabilities":[{"kind":"Metadata","hooks":["search"],"accountScopes":["Global"]}],"permissions":[]}),encoding="utf-8")
        (provider/"index.js").write_text("exports.search = () => [];\n",encoding="utf-8")
        (provider/"README.md").write_text("Fixture\n",encoding="utf-8")
        source=self.root/"first-party/sources"; source.mkdir(parents=True)
        (source/"fixture.lock.json").write_text(json.dumps({"schemaVersion":1,"providerId":"fixture","source":{"repository":"https://example.test/fixture","commit":"a"*40,"contentSha256":bundle.content_sha(provider)},"rollback":{"version":"1.2.2","archiveSha256":"c"*64,"contentSha256":"d"*64}}),encoding="utf-8")
    def tearDown(self): self.temp.cleanup()
    def test_build_is_deterministic_and_verify_preserves_provenance_rollback(self):
        first=bundle.build(self.root,self.output,"2.0.0"); archive=(self.output/first["packages"][0]["archiveFile"]).read_bytes()
        first_hash=hashlib.sha256(archive).hexdigest(); second=bundle.build(self.root,self.output,"2.0.0")
        self.assertEqual(first_hash,second["packages"][0]["archiveSha256"])
        verified=bundle.verify(self.output); package=verified["packages"][0]
        self.assertEqual("a"*40,package["source"]["commit"]); self.assertEqual("1.2.2",package["rollback"]["version"])
        self.assertEqual("blocked-built-in-switchover-required",package["activation"])
    def test_tamper_and_unlocked_archive_are_rejected(self):
        lock=bundle.build(self.root,self.output,"2.0.0"); path=self.output/lock["packages"][0]["archiveFile"]
        path.write_bytes(path.read_bytes()+b"tamper")
        with self.assertRaises(bundle.BundleError): bundle.verify(self.output)
        bundle.build(self.root,self.output,"2.0.0"); (self.output/"unlocked.zip").write_bytes(b"x")
        with self.assertRaises(bundle.BundleError): bundle.verify(self.output)
    def test_core_plan_omits_packages_and_aio_requires_verified_bundle(self):
        bundle.build(self.root,self.output,"2.0.0"); tool=ROOT/"tools/first_party_bundle.py"
        core=subprocess.run([sys.executable,str(tool),"--root",str(self.root),"plan","--output","dist","--mode","core"],capture_output=True,text=True,check=True)
        aio=subprocess.run([sys.executable,str(tool),"--root",str(self.root),"plan","--output","dist","--mode","aio"],capture_output=True,text=True,check=True)
        self.assertEqual([],json.loads(core.stdout)["packages"]); self.assertEqual(["fixture"],[x["id"] for x in json.loads(aio.stdout)["packages"]])
        (self.output/"fixture-1.2.3.zip").write_bytes(b"bad")
        failed=subprocess.run([sys.executable,str(tool),"--root",str(self.root),"plan","--output","dist","--mode","aio"],capture_output=True,text=True)
        self.assertEqual(2,failed.returncode)
    def test_package_layout_and_source_lock_are_strict(self):
        provider=self.root/"first-party/providers/fixture"; (provider/"provenance.json").write_text("{}")
        with self.assertRaises(bundle.BundleError): bundle.build(self.root,self.output,"2.0.0")
    def test_compose_core_omits_bundle_and_aio_mounts_verified_offline_bundle(self):
        core=(ROOT/"docker-compose.yml").read_text("utf-8")
        aio=(ROOT/"docker-compose.aio.yml").read_text("utf-8")
        self.assertNotIn("first-party-bundle",core)
        self.assertNotIn("gamdl-aio:",aio)
        self.assertIn("./first-party/dist:/app/first-party-bundle:ro",aio)
        self.assertIn("Extensions__FirstPartyBundleLockPath:",aio)

if __name__=="__main__": unittest.main()
