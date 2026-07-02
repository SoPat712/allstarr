#!/bin/bash
# extract-libs.sh - Extract Apple native libraries from an APKMirror .apkm bundle
# or a standalone arch split .apk, and verify each .so against LIBS_VERSION.json.
#
# The bundle/APK file itself is not hashed; only extracted libraries are checked.
#
# Usage:
#   extract-libs.sh --bundle <path-to-.apkm|.apk> [--arch <x86_64|arm64-v8a>] [--out <dir>] [--ignore-hash]
#
# Options:
#   --arch <x86_64|arm64-v8a>    Which arch's libs to extract (default x86_64)
#   --out  <directory>           Where to drop the .so files (default: <repo>/rootfs/system/lib64)
#   --ignore-hash                Copy expected files without SHA-256 verification
set -euo pipefail

SCRIPT_DIR="${BASH_SOURCE[0]%/*}"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIBS_VERSION="$REPO_ROOT/LIBS_VERSION.json"

BUNDLE=""
ARCH="x86_64"
OUT=""
SENTINEL="libandroidappmusic.so"
IGNORE_HASH=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --bundle)      BUNDLE="$2"; shift 2 ;;
        --arch)        ARCH="$2";   shift 2 ;;
        --out)         OUT="$2";    shift 2 ;;
        --ignore-hash) IGNORE_HASH=1; shift ;;
        -h|--help)
            sed -n '2,13p' "$0"
            exit 0
            ;;
        *) echo "unknown arg: $1" >&2; exit 2 ;;
    esac
done

if [[ -z "$BUNDLE" ]]; then
    echo "extract-libs: missing --bundle <path-to-.apkm|.apk>" >&2
    exit 2
fi
if [[ ! -f "$BUNDLE" ]]; then
    echo "extract-libs: not a file: $BUNDLE" >&2
    exit 2
fi
if [[ -z "$OUT" ]]; then
    OUT="$REPO_ROOT/rootfs/system/lib64"
fi

case "$ARCH" in
    x86_64)    PREFERRED_SPLIT="split_config.x86_64.apk"    ;;
    arm64-v8a) PREFERRED_SPLIT="split_config.arm64_v8a.apk" ;;
    *) echo "extract-libs: unsupported arch '$ARCH'" >&2; exit 2 ;;
esac

for c in jq unzip install; do
    command -v "$c" >/dev/null || { echo "extract-libs: $c is required" >&2; exit 3; }
done

sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        echo "extract-libs: sha256sum or shasum is required" >&2
        return 3
    fi
}

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Return 0 when $1 lists lib/<abi>/$SENTINEL inside the zip at $2.
apk_has_sentinel() {
    local lib_dir="$1"
    local apk="$2"
    unzip -Z1 "$apk" 2>/dev/null | tr -d '\r' | grep -qF "${lib_dir}/${SENTINEL}"
}

# Print lib/<abi> when $SENTINEL is present (first match), else return 1.
detect_lib_dir_in_apk() {
    local apk="$1"
    local so_path
    so_path="$(
        unzip -Z1 "$apk" 2>/dev/null | tr -d '\r' |
            grep -E "^lib/[^/]+/${SENTINEL}\$" |
            head -n1
    )"
    if [[ -z "$so_path" ]]; then
        return 1
    fi
    echo "${so_path%/$SENTINEL}"
}

# Prefer lib dirs that match the requested --arch, then any lib/* with the sentinel.
pick_lib_dir_for_arch() {
    local apk="$1"
    local -a candidates=()
    case "$ARCH" in
        x86_64)    candidates=("lib/x86_64" "lib/x86") ;;
        arm64-v8a) candidates=("lib/arm64-v8a" "lib/arm64") ;;
    esac
    local dir
    for dir in "${candidates[@]}"; do
        if apk_has_sentinel "$dir" "$apk"; then
            echo "$dir"
            return 0
        fi
    done
    detect_lib_dir_in_apk "$apk"
}

# Extract one zip member from $1 into a unique dir under $TMP; print the file path.
extract_zip_member() {
    local zip="$1"
    local member="$2"
    local dest="$TMP/$(echo "$member" | tr '/: ' '___')"
    mkdir -p "$dest"
    unzip -qq "$zip" "$member" -d "$dest"
    local base
    base="$(basename "$member")"
    local found
    found="$(find "$dest" -name "$base" -type f | head -n1)"
    if [[ -z "$found" || ! -f "$found" ]]; then
        echo "extract-libs: failed to extract $member from $zip" >&2
        return 1
    fi
    echo "$found"
}

# Score a zip member name for arch relevance (higher = better).
split_name_score() {
    local name="$1"
    local base="${name##*/}"
    local score=0
    case "$ARCH" in
        x86_64)
            [[ "$base" == "$PREFERRED_SPLIT" ]] && score=100
            [[ "$base" == *x86_64* || "$base" == *x86-64* ]] && score=$((score + 50))
            [[ "$base" == *x86* ]] && score=$((score + 10))
            ;;
        arm64-v8a)
            [[ "$base" == "$PREFERRED_SPLIT" ]] && score=100
            [[ "$base" == *arm64_v8a* || "$base" == *arm64-v8a* || "$base" == *arm64* ]] && score=$((score + 50))
            ;;
    esac
    [[ "$base" == base.apk ]] && score=$((score - 20))
    echo "$score"
}

# From an .apkm, pick the inner .apk that actually ships native libs for --arch.
resolve_apk_from_apkm() {
    local bundle="$1"
    local -a members=()
    local member_name
    while IFS= read -r member_name; do
        members+=("$member_name")
    done < <(unzip -Z1 "$bundle" 2>/dev/null | tr -d '\r' | grep -E '\.apk$' || true)
    if [[ ${#members[@]} -eq 0 ]]; then
        echo "extract-libs: bundle contains no .apk members: $bundle" >&2
        return 1
    fi

    local best_member="" best_score=-1 best_apk="" best_lib=""
    local member score lib_dir apk_path
    for member in "${members[@]}"; do
        score="$(split_name_score "$member")"
        [[ "$score" -le 0 ]] && continue
        apk_path="$(extract_zip_member "$bundle" "$member")" || continue
        lib_dir="$(pick_lib_dir_for_arch "$apk_path" || true)"
        if [[ -z "$lib_dir" ]]; then
            continue
        fi
        if [[ "$score" -gt "$best_score" ]]; then
            best_score="$score"
            best_member="$member"
            best_apk="$apk_path"
            best_lib="$lib_dir"
        fi
    done

    if [[ -z "$best_apk" ]]; then
        # Last resort: any inner .apk that contains the sentinel (any lib/*).
        for member in "${members[@]}"; do
            apk_path="$(extract_zip_member "$bundle" "$member")" || continue
            lib_dir="$(detect_lib_dir_in_apk "$apk_path" || true)"
            if [[ -n "$lib_dir" ]]; then
                best_member="$member"
                best_apk="$apk_path"
                best_lib="$lib_dir"
                break
            fi
        done
    fi

    if [[ -z "$best_apk" ]]; then
        echo "extract-libs: no .apk in bundle contains $SENTINEL under lib/<abi>/ (arch=$ARCH)" >&2
        echo "extract-libs: members seen:" >&2
        printf '  %s\n' "${members[@]}" >&2
        return 1
    fi

    if [[ "$best_member" != "$PREFERRED_SPLIT" ]]; then
        echo "extract-libs: using $best_member (not $PREFERRED_SPLIT; that split has no native libs)" >&2
    fi
    if [[ "$best_lib" != "lib/$ARCH" ]]; then
        echo "extract-libs: using lib dir $best_lib inside $(basename "$best_apk")" >&2
    fi
    APK="$best_apk"
    APK_LIB_DIR="$best_lib"
}

case "$BUNDLE" in
*.[aA][pP][kK][mM])
    resolve_apk_from_apkm "$BUNDLE"
    ;;
*.[aA][pP][kK])
    APK="$BUNDLE"
    APK_LIB_DIR="$(pick_lib_dir_for_arch "$APK" || true)"
    if [[ -z "$APK_LIB_DIR" ]]; then
        echo "extract-libs: $APK has no lib/<abi>/$SENTINEL (wrong split or --arch?)" >&2
        echo "extract-libs: if this is an .apkm, pass the .apkm file instead of a single split" >&2
        exit 6
    fi
    if [[ "$APK_LIB_DIR" != "lib/$ARCH" ]]; then
        echo "extract-libs: using lib dir $APK_LIB_DIR" >&2
    fi
    ;;
*)
    echo "extract-libs: expected .apkm or .apk extension: $BUNDLE" >&2
    exit 2
    ;;
esac

mkdir -p "$OUT"
LIB_TMP="$TMP/libs"
mkdir -p "$LIB_TMP"
unzip -qq "$APK" "$APK_LIB_DIR/*" -d "$LIB_TMP"

# `jq.exe` on msys2/MSVC emits CRLF on Windows; strip CR defensively before
# iterating, otherwise lib names get a stray \r appended and every lookup fails.
EXPECTED_LIBS=()
while IFS= read -r so; do
    EXPECTED_LIBS+=("$so")
done < <(jq -r --arg arch "$ARCH" '.libs[$arch] | keys[]' "$LIBS_VERSION" | tr -d '\r')
[[ ${#EXPECTED_LIBS[@]} -gt 0 ]] || {
    echo "extract-libs: no libs pin for arch '$ARCH' in LIBS_VERSION.json" >&2
    exit 4
}

ok=0
fail=0
for so in "${EXPECTED_LIBS[@]}"; do
    src="$LIB_TMP/$APK_LIB_DIR/$so"
    if [[ ! -f "$src" ]]; then
        echo "extract-libs: missing in APK: $so" >&2
        fail=$((fail+1))
        continue
    fi
    if [[ "$IGNORE_HASH" -eq 0 ]]; then
        expect="$(jq -r --arg arch "$ARCH" --arg so "$so" '.libs[$arch][$so]' "$LIBS_VERSION" | tr -d '\r')"
        actual="$(sha256_file "$src")"
        if [[ "$expect" != "$actual" ]]; then
            echo "extract-libs: SHA-256 mismatch on $so" >&2
            echo "  expected: $expect" >&2
            echo "  actual:   $actual" >&2
            fail=$((fail+1))
            continue
        fi
    fi
    install -m 0644 "$src" "$OUT/$so"
    ok=$((ok+1))
done

if [[ "$IGNORE_HASH" -eq 1 ]]; then
    echo "extract-libs: $ok copied, $fail failed (arch=$ARCH out=$OUT from $(basename "$APK") $APK_LIB_DIR hash=ignored)"
else
    echo "extract-libs: $ok ok, $fail failed (arch=$ARCH out=$OUT from $(basename "$APK") $APK_LIB_DIR)"
fi
[[ $fail -eq 0 ]]
