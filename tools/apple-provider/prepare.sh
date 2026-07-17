#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
LOCK="$ROOT/tools/apple-provider/source-lock.json"
OUTPUT="$ROOT/.apple-provider/wrapper-v2"
ARCH=x86_64
PACKAGE=""
STAGED_LIBS=""

usage() {
  echo "Usage: $0 [--apk FILE | --apkm FILE | --staged-libs DIR] [--arch x86_64|arm64-v8a] [--output DIR]" >&2
}

while (($#)); do
  case "$1" in
    --apk|--apkm) PACKAGE=${2:?missing package path}; shift 2 ;;
    --staged-libs) STAGED_LIBS=${2:?missing staged library directory}; shift 2 ;;
    --arch) ARCH=${2:?missing architecture}; shift 2 ;;
    --output) OUTPUT=${2:?missing output directory}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; exit 2 ;;
  esac
done

[[ "$ARCH" == x86_64 || "$ARCH" == arm64-v8a ]] || { echo "Unsupported architecture: $ARCH" >&2; exit 2; }
[[ -n "$PACKAGE" || -n "$STAGED_LIBS" ]] || { usage; exit 2; }
[[ ! ( -n "$PACKAGE" && -n "$STAGED_LIBS" ) ]] || { echo "Choose a package or --staged-libs, not both." >&2; exit 2; }
for command in git jq unzip; do command -v "$command" >/dev/null || { echo "Missing required command: $command" >&2; exit 1; }; done

repository=$(jq -r '.wrapper.repository' "$LOCK")
tag=$(jq -r '.wrapper.tag' "$LOCK")
commit=$(jq -r '.wrapper.commit' "$LOCK")
if [[ -e "$OUTPUT" ]]; then
  [[ -d "$OUTPUT/.git" ]] || { echo "Output exists and is not a wrapper-v2 checkout: $OUTPUT" >&2; exit 1; }
else
  mkdir -p "$(dirname "$OUTPUT")"
  git clone --filter=blob:none --branch "$tag" --single-branch "$repository" "$OUTPUT"
fi

git -C "$OUTPUT" fetch --force --tags origin "refs/tags/$tag:refs/tags/$tag"
tag_commit=$(git -C "$OUTPUT" rev-list -n 1 "$tag")
[[ "$tag_commit" == "$commit" ]] || { echo "wrapper-v2 tag $tag resolved to unexpected commit $tag_commit" >&2; exit 1; }
git -C "$OUTPUT" checkout --detach "$commit"
[[ -f "$OUTPUT/LIBS_VERSION.json" ]] || { echo "Pinned wrapper source lacks LIBS_VERSION.json" >&2; exit 1; }

if [[ -n "$PACKAGE" ]]; then
  [[ -f "$PACKAGE" ]] || { echo "Package not found: $PACKAGE" >&2; exit 1; }
  bash "$OUTPUT/tools/extract-libs.sh" --bundle "$PACKAGE" --arch "$ARCH"
else
  [[ -d "$STAGED_LIBS" ]] || { echo "Staged library directory not found: $STAGED_LIBS" >&2; exit 1; }
  mkdir -p "$OUTPUT/rootfs/system/lib64"
  while IFS= read -r library; do
    source="$STAGED_LIBS/$library"
    [[ -f "$source" ]] || { echo "Missing pinned staged library: $library" >&2; exit 1; }
    install -m 0644 "$source" "$OUTPUT/rootfs/system/lib64/$library"
  done < <(jq -r --arg arch "$ARCH" '.libs[$arch] | keys[]' "$OUTPUT/LIBS_VERSION.json")
fi

bash "$OUTPUT/tools/stage-system.sh" --arch "$ARCH"

sha256_file() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }
verify_group() {
  local group=$1 base=$2
  while IFS=$'\t' read -r relative expected; do
    file="$base/$relative"
    [[ -f "$file" ]] || { echo "Missing pinned file: $relative" >&2; exit 1; }
    actual=$(sha256_file "$file")
    [[ "$actual" == "$expected" ]] || { echo "Hash mismatch for $relative" >&2; exit 1; }
  done < <(jq -r --arg arch "$ARCH" --arg group "${group#.}" '.[$group][$arch] | to_entries[] | [.key,.value] | @tsv' "$OUTPUT/LIBS_VERSION.json")
}

verify_group android_system "$OUTPUT/rootfs/system"
verify_group libs "$OUTPUT/rootfs/system/lib64"
printf 'Prepared verified wrapper-v2 %s (%s) at %s\n' "$tag" "$tag_commit" "$OUTPUT"
printf 'Next: run ./allstarr.sh up, then finish Apple login in the WebUI.\n'
