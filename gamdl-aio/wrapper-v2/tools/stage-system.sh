#!/bin/bash
# stage-system.sh - Verify and copy committed Android system binaries into
# rootfs/system/. These binaries (linker64 + bionic + a few AOSP libs) are
# committed under vendor/android-system/<arch>/ with SHA-256 pins in
# LIBS_VERSION.json.
#
# Usage:
#   tools/stage-system.sh [--arch <x86_64|arm64-v8a>] [--rootfs <path>] [--ignore-hash]
#
# Defaults: --arch x86_64, --rootfs <repo>/rootfs
set -euo pipefail

SCRIPT_DIR="${BASH_SOURCE[0]%/*}"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIBS_VERSION="$REPO_ROOT/LIBS_VERSION.json"

ARCH="x86_64"
ROOTFS="$REPO_ROOT/rootfs"
IGNORE_HASH=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)        ARCH="$2";   shift 2 ;;
        --rootfs)      ROOTFS="$2"; shift 2 ;;
        --ignore-hash) IGNORE_HASH=1; shift ;;
        -h|--help)
            sed -n '2,10p' "$0"
            exit 0
            ;;
        *) echo "unknown arg: $1" >&2; exit 2 ;;
    esac
done

VENDOR="$REPO_ROOT/vendor/android-system/$ARCH"
if [[ ! -d "$VENDOR" ]]; then
    echo "stage-system: vendor dir does not exist: $VENDOR" >&2
    echo "  (current arch '$ARCH' may not have committed binaries yet)" >&2
    exit 4
fi

for c in jq install; do
    command -v "$c" >/dev/null || { echo "stage-system: $c is required" >&2; exit 3; }
done

sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        echo "stage-system: sha256sum or shasum is required" >&2
        return 3
    fi
}

# `jq.exe` on msys2/MSVC emits CRLF on Windows; strip CR defensively before
# iterating, otherwise paths get a stray \r appended and every lookup fails.
EXPECTED=()
while IFS= read -r rel; do
    EXPECTED+=("$rel")
done < <(jq -r --arg arch "$ARCH" '.android_system[$arch] | keys[]' "$LIBS_VERSION" | tr -d '\r')
[[ ${#EXPECTED[@]} -gt 0 ]] || { echo "stage-system: no .android_system.$ARCH section in LIBS_VERSION.json" >&2; exit 4; }

ok=0
fail=0
for rel in "${EXPECTED[@]}"; do
    src="$VENDOR/$rel"
    if [[ ! -f "$src" ]]; then
        echo "stage-system: missing committed file: vendor/android-system/$ARCH/$rel" >&2
        fail=$((fail+1))
        continue
    fi
    if [[ "$IGNORE_HASH" -eq 0 ]]; then
        expect="$(jq -r --arg arch "$ARCH" --arg p "$rel" '.android_system[$arch][$p]' "$LIBS_VERSION" | tr -d '\r')"
        actual="$(sha256_file "$src")"
        if [[ "$expect" != "$actual" ]]; then
            echo "stage-system: SHA-256 mismatch on $rel" >&2
            echo "  expected: $expect" >&2
            echo "  actual:   $actual" >&2
            fail=$((fail+1))
            continue
        fi
    fi
    case "$rel" in
        bin/*)   mode=0755 ;;
        lib64/*) mode=0644 ;;
        *)       mode=0644 ;;
    esac
    dest="$ROOTFS/system/$rel"
    mkdir -p "$(dirname "$dest")"
    install -m "$mode" "$src" "$dest"
    ok=$((ok+1))
done

if [[ "$IGNORE_HASH" -eq 1 ]]; then
    echo "stage-system: $ok copied, $fail failed (arch=$ARCH rootfs=$ROOTFS hash=ignored)"
else
    echo "stage-system: $ok ok, $fail failed (arch=$ARCH rootfs=$ROOTFS)"
fi
[[ $fail -eq 0 ]]
