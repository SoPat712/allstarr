#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${root}"

hash_stream() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum | awk '{ print $1 }'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 | awk '{ print $1 }'
    else
        printf '%s\n' 'A SHA-256 utility (sha256sum or shasum) is required.' >&2
        exit 1
    fi
}

hash_file() {
    local file="$1"
    test -f "${file}" || {
        printf 'Required release input is missing: %s\n' "${file}" >&2
        exit 1
    }
    hash_stream < "${file}"
}

hash_set() {
    local label="$1"
    shift
    test "$#" -gt 0 || {
        printf 'Release input set is empty: %s\n' "${label}" >&2
        exit 1
    }

    local file
    for file in "$@"; do
        printf '%s  %s\n' "$(hash_file "${file}")" "${file}"
    done | LC_ALL=C sort | hash_stream
}

json_string() {
    local value="$1"
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//$'\n'/\\n}
    printf '"%s"' "${value}"
}

version="$(sed -n 's/.*Version = "\([^"]*\)";.*/\1/p' allstarr/AppVersion.cs)"
test -n "${version}" || {
    printf '%s\n' 'Could not read the canonical version from allstarr/AppVersion.cs.' >&2
    exit 1
}

commit="unavailable"
tag="unavailable"
dirty="null"
if command -v git >/dev/null 2>&1 && git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    commit="$(git rev-parse HEAD)"
    tag="$(git describe --tags --exact-match 2>/dev/null || printf 'untagged')"
    if test -n "$(git status --porcelain --untracked-files=no)"; then
        dirty="true"
    else
        dirty="false"
    fi
fi

migration_files=()
while IFS= read -r file; do migration_files+=("${file}"); done < <(
    find allstarr/Core/Storage/Migrations -type f -name '*.cs' -print | LC_ALL=C sort
)

compose_files=()
while IFS= read -r file; do compose_files+=("${file}"); done < <(
    find . -maxdepth 1 -type f -name 'docker-compose*.yml' -print | sed 's#^./##' | LC_ALL=C sort
)

extension_lock_files=(
    first-party/dist/bundle.lock.json
    first-party/sources/apple-musickit.lock.json
    first-party/sources/deezer.lock.json
    first-party/sources/spotify.lock.json
)

apple_lock_files=(
    first-party/sources/apple-musickit.lock.json
    tools/apple-provider/source-lock.json
    sidecars/apple-gateway/uv.lock
)

migration_digest="$(hash_set migrations "${migration_files[@]}")"
compose_digest="$(hash_set compose "${compose_files[@]}")"
extension_digest="$(hash_set extensions "${extension_lock_files[@]}")"
apple_digest="$(hash_set apple "${apple_lock_files[@]}")"
package_lock_digest="$(hash_file package-lock.json)"

generated_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"

cat <<EOF
{
  "schemaVersion": 1,
  "generatedAt": $(json_string "${generated_at}"),
  "applicationVersion": $(json_string "${version}"),
  "git": {
    "commit": $(json_string "${commit}"),
    "tag": $(json_string "${tag}"),
    "trackedFilesDirty": ${dirty}
  },
  "digests": {
    "databaseMigrationsSha256": $(json_string "${migration_digest}"),
    "composeFilesSha256": $(json_string "${compose_digest}"),
    "firstPartyExtensionLocksSha256": $(json_string "${extension_digest}"),
    "appleGatewayLocksSha256": $(json_string "${apple_digest}"),
    "webUiPackageLockSha256": $(json_string "${package_lock_digest}")
  }
}
EOF
