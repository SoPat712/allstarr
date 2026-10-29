"""Generate the frozen origin/dev comparator without git or a second checkout."""
import hashlib
import json
import pathlib
import re
import sys


fixtures = pathlib.Path(__file__).resolve().parents[1] / "Fixtures" / "Matching"
destination = pathlib.Path(sys.argv[1]).resolve()
destination.mkdir(parents=True, exist_ok=True)
manifest = json.loads((fixtures / "baseline-manifest.json").read_text())
sources = {}
for entry in manifest["files"]:
    data = (fixtures / entry["asset"]).read_bytes()
    if hashlib.sha256(data).hexdigest() != entry["sha256"]:
        raise ValueError(f"Frozen baseline fingerprint mismatch: {entry['asset']}")
    sources[pathlib.PurePosixPath(entry["path"]).stem] = data.decode("utf-8-sig")

engine = sources["TrackMatchDecisionEngine"]
engine = engine[:engine.index("public enum TrackMatchReviewState")] + engine[engine.index("public sealed class TrackMatchDecisionEngine\n"):]
rules = sources["TrackMatchRules"]
renames = {
    "TrackMatchDecisionEngine": "ReplayBaselineEngine",
    "FuzzyMatcher": "ReplayBaselineFuzzyMatcher",
    "TrackSnapshotMetadata": "ReplayBaselineSnapshotMetadata",
}
# Freeze the engine's index, authority helpers, rule classes and signal helpers too.
for source in (engine, rules):
    for name in re.findall(r"\b(?:class|enum|interface|record)\s+(?:struct\s+)?(\w+)", source):
        renames.setdefault(name, "ReplayBaseline" + name)

def renamed(source):
    return re.sub(r"\b(" + "|".join(map(re.escape, renames)) + r")\b",
                  lambda match: renames[match.group()], source)

for name, source in {
    "ReplayBaselineEngine": engine,
    "ReplayBaselineRules": rules,
    "ReplayBaselineFuzzyMatcher": sources["FuzzyMatcher"],
    "ReplayBaselineSnapshotMetadata": sources["TrackSnapshotMetadata"],
}.items():
    (destination / (name + ".cs")).write_text(
        "// Generated from origin/dev " + manifest["commit"] + "; see baseline-manifest.json.\n" + renamed(source))
