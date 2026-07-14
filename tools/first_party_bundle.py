#!/usr/bin/env python3
"""Build and verify deterministic offline first-party provider bundles."""
from __future__ import annotations
import argparse, hashlib, json, re, shutil, struct, sys, tempfile, zipfile
from pathlib import Path

SHA = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
SEMVER = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")
ROOT_FILES = {"manifest.json", "index.js", "README.md", "LICENSE", "LICENSE.md"}
FIXED_TIME = (1980, 1, 1, 0, 0, 0)

class BundleError(ValueError): pass
def read_json(path: Path):
    try: value=json.loads(path.read_text("utf-8"))
    except (OSError,json.JSONDecodeError) as exc: raise BundleError(f"cannot read {path}: {exc}") from exc
    if not isinstance(value,dict): raise BundleError(f"{path} must contain an object")
    return value
def sha(data: bytes): return hashlib.sha256(data).hexdigest()
def files(root: Path):
    result=[]
    for item in root.rglob("*"):
        if item.is_symlink(): raise BundleError(f"symbolic links are forbidden: {item}")
        if item.is_file():
            rel=item.relative_to(root).as_posix()
            if rel not in ROOT_FILES and not rel.startswith("assets/"): raise BundleError(f"SDK-v1 layout rejects {rel}")
            result.append((rel,item))
    return sorted(result,key=lambda pair:pair[0])
def content_sha(root: Path):
    digest=hashlib.sha256()
    for rel,path in files(root):
        name=rel.encode(); data=path.read_bytes(); digest.update(struct.pack(">i",len(name))); digest.update(name); digest.update(struct.pack(">q",len(data))); digest.update(data)
    return digest.hexdigest()
def validate_package(root: Path, expected_id: str | None = None):
    manifest=read_json(root/"manifest.json")
    required={"id","version","sdkVersion","entryPoint","capabilities","permissions"}
    if not required.issubset(manifest): raise BundleError(f"{root}: manifest is incomplete")
    if not ID.fullmatch(str(manifest["id"])) or expected_id is not None and expected_id!=manifest["id"]: raise BundleError(f"{root}: stable provider id does not match directory")
    if not SEMVER.fullmatch(str(manifest["version"])) or manifest["sdkVersion"]!="1" or manifest["entryPoint"]!="index.js": raise BundleError(f"{root}: invalid SDK/version/entry point")
    if not (root/"index.js").is_file() or not isinstance(manifest["capabilities"],list) or not manifest["capabilities"]: raise BundleError(f"{root}: package has no entry point/capability")
    files(root); return manifest
def source_lock(path: Path, provider: str):
    value=read_json(path)
    if value.get("schemaVersion")!=1 or value.get("providerId")!=provider: raise BundleError(f"{path}: source lock identity is invalid")
    source=value.get("source",{})
    if not isinstance(source,dict) or not str(source.get("repository","")).startswith("https://") or not COMMIT.fullmatch(str(source.get("commit",""))) or not SHA.fullmatch(str(source.get("contentSha256",""))): raise BundleError(f"{path}: immutable source provenance is incomplete")
    rollback=value.get("rollback")
    if rollback is not None and (not isinstance(rollback,dict) or not SEMVER.fullmatch(str(rollback.get("version",""))) or not SHA.fullmatch(str(rollback.get("archiveSha256",""))) or not SHA.fullmatch(str(rollback.get("contentSha256","")))): raise BundleError(f"{path}: rollback metadata is invalid")
    return value
def archive(root: Path, output: Path):
    output.parent.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(output,"w",compression=zipfile.ZIP_DEFLATED,compresslevel=9,strict_timestamps=True) as bundle:
        for rel,path in files(root):
            info=zipfile.ZipInfo(rel,FIXED_TIME); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o100644<<16; info.create_system=3
            bundle.writestr(info,path.read_bytes(),compress_type=zipfile.ZIP_DEFLATED,compresslevel=9)
def build(root: Path, output: Path, version: str):
    providers=root/"first-party/providers"; locks=root/"first-party/sources"; output.mkdir(parents=True,exist_ok=True)
    packages=[]
    for directory in sorted((p for p in providers.iterdir() if p.is_dir()),key=lambda p:p.name) if providers.exists() else []:
        manifest=validate_package(directory, directory.name); provenance=source_lock(locks/f"{directory.name}.lock.json",directory.name)
        package_content=content_sha(directory)
        if provenance["source"]["contentSha256"] != package_content: raise BundleError(f"{directory.name}: extracted package differs from its source lock")
        target=output/f"{directory.name}-{manifest['version']}.zip"; archive(directory,target)
        packages.append({"id":directory.name,"version":manifest["version"],"archiveFile":target.name,"archiveSha256":sha(target.read_bytes()),"contentSha256":package_content,"source":provenance["source"],"rollback":provenance.get("rollback"),"rollbackCompatibility":provenance.get("rollbackCompatibility"),"activation":"blocked-built-in-switchover-required"})
    lock={"schemaVersion":1,"bundleVersion":version,"sdkVersion":"1","packages":packages}
    (output/"bundle.lock.json").write_text(json.dumps(lock,sort_keys=True,separators=(",",":"))+"\n","utf-8")
    return lock
def verify(output: Path):
    lock=read_json(output/"bundle.lock.json")
    if lock.get("schemaVersion")!=1 or lock.get("sdkVersion")!="1" or not SEMVER.fullmatch(str(lock.get("bundleVersion",""))): raise BundleError("bundle lock header is invalid")
    seen=set()
    for item in lock.get("packages",[]):
        if item.get("id") in seen: raise BundleError("bundle contains a duplicate provider id")
        seen.add(item.get("id")); path=output/str(item.get("archiveFile",""))
        source=item.get("source",{})
        if not ID.fullmatch(str(item.get("id",""))) or not SEMVER.fullmatch(str(item.get("version",""))) or not isinstance(source,dict) or not str(source.get("repository","")).startswith("https://") or not COMMIT.fullmatch(str(source.get("commit",""))) or source.get("contentSha256") != item.get("contentSha256") or item.get("activation") != "blocked-built-in-switchover-required": raise BundleError(f"immutable provenance is invalid for {item.get('id')}")
        rollback=item.get("rollback")
        if rollback is None and item.get("rollbackCompatibility") != "initial-package-no-prior-version": raise BundleError(f"initial rollback boundary is missing for {item.get('id')}")
        if rollback is not None and (not SEMVER.fullmatch(str(rollback.get("version",""))) or not SHA.fullmatch(str(rollback.get("archiveSha256",""))) or not SHA.fullmatch(str(rollback.get("contentSha256","")))): raise BundleError(f"rollback metadata is invalid for {item.get('id')}")
        if not path.is_file() or sha(path.read_bytes())!=item.get("archiveSha256"): raise BundleError(f"archive digest mismatch for {item.get('id')}")
        with tempfile.TemporaryDirectory() as temp:
            with zipfile.ZipFile(path) as package:
                names=package.namelist()
                if names!=sorted(names) or len(names)!=len(set(names)): raise BundleError(f"archive order/identity invalid for {item.get('id')}")
                for info in package.infolist():
                    mode=(info.external_attr>>16)&0o170000
                    if info.date_time!=FIXED_TIME or info.filename.startswith("/") or ".." in Path(info.filename).parts or mode not in (0,0o100000): raise BundleError(f"archive metadata/path invalid for {item.get('id')}")
                    if info.file_size > 128*1024*1024: raise BundleError(f"archive entry is too large for {item.get('id')}")
                package.extractall(temp)
            manifest=validate_package(Path(temp), str(item.get("id")))
            if manifest["id"]!=item.get("id") or manifest["version"]!=item.get("version") or content_sha(Path(temp))!=item.get("contentSha256"): raise BundleError(f"content identity mismatch for {item.get('id')}")
    extras={p.name for p in output.glob("*.zip")}-{str(p["archiveFile"]) for p in lock["packages"]}
    if extras: raise BundleError("bundle contains unlocked package archives")
    return lock
def main(argv=None):
    parser=argparse.ArgumentParser(); parser.add_argument("--root",type=Path,default=Path.cwd()); sub=parser.add_subparsers(dest="cmd",required=True)
    b=sub.add_parser("build"); b.add_argument("--output",type=Path,default=Path("first-party/dist")); b.add_argument("--bundle-version",required=True)
    v=sub.add_parser("verify"); v.add_argument("--output",type=Path,default=Path("first-party/dist"))
    p=sub.add_parser("plan"); p.add_argument("--output",type=Path,default=Path("first-party/dist")); p.add_argument("--mode",choices=("core","aio"),required=True)
    args=parser.parse_args(argv); root=args.root.resolve(); output=(root/args.output).resolve()
    try:
        if args.cmd=="build": print(json.dumps(build(root,output,args.bundle_version),sort_keys=True))
        elif args.cmd=="verify": print(json.dumps(verify(output),sort_keys=True))
        else:
            packages=[] if args.mode=="core" else verify(output)["packages"]
            print(json.dumps({"mode":args.mode,"packages":[{"id":p["id"],"version":p["version"],"archiveSha256":p["archiveSha256"]} for p in packages]},sort_keys=True))
        return 0
    except (BundleError,OSError,zipfile.BadZipFile) as exc: print(f"error: {exc}",file=sys.stderr); return 2
if __name__=="__main__": raise SystemExit(main())
