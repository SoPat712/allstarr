#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "${BASH_SOURCE[0]%/*}" && pwd)"
PROFILE_FILE="$ROOT/.allstarr-profiles"
MODE_FILE="$ROOT/.allstarr-mode"

die() { echo "allstarr: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is required"; }

profiles() {
  local values=(standard)
  if [[ -f "$PROFILE_FILE" ]]; then
    while IFS= read -r value; do
      case "$value" in
        spotify|spotify-lyrics) values+=(spotify-lyrics) ;;
        apple) values+=("$value") ;;
      esac
    done < "$PROFILE_FILE"
  fi
  printf '%s\n' "${values[@]}" | awk '!seen[$0]++'
}

deployment_mode() {
  local mode="release"
  [[ -f "$MODE_FILE" ]] && read -r mode < "$MODE_FILE"
  case "$mode" in release|source) printf '%s\n' "$mode" ;; *) die "invalid deployment mode in .allstarr-mode" ;; esac
}

set_mode() {
  local mode="${1:-}"
  case "$mode" in release|source) printf '%s\n' "$mode" > "$MODE_FILE" ;; *) die "mode must be release or source" ;; esac
  echo "Deployment mode set to $mode. Run: ./allstarr.sh up"
}

compose_args() {
  COMPOSE=(-f "$ROOT/docker-compose.yml")
  while IFS= read -r profile; do
    case "$profile" in
      spotify-lyrics) COMPOSE+=(--profile spotify-lyrics) ;;
      apple) COMPOSE+=(--profile apple) ;;
    esac
  done < <(profiles)
}

start_stack() {
  docker compose "${COMPOSE[@]}" up -d --remove-orphans --wait --wait-timeout 180
}

remember_profile() {
  local wanted="$1" temporary
  [[ "$wanted" == spotify ]] && wanted=spotify-lyrics
  touch "$PROFILE_FILE"
  temporary="$(mktemp)"
  awk '{ if ($0 == "spotify") $0 = "spotify-lyrics"; if (!seen[$0]++) print }' "$PROFILE_FILE" > "$temporary"
  mv "$temporary" "$PROFILE_FILE"
  grep -qxF "$wanted" "$PROFILE_FILE" 2>/dev/null || printf '%s\n' "$wanted" >> "$PROFILE_FILE"
}

forget_profile() {
  local unwanted="$1" temporary
  [[ "$unwanted" == spotify ]] && unwanted=spotify-lyrics
  temporary="$(mktemp)"
  if [[ -f "$PROFILE_FILE" ]]; then
    if [[ "$unwanted" == spotify-lyrics ]]; then
      grep -Ev '^(spotify|spotify-lyrics)$' "$PROFILE_FILE" > "$temporary" || true
    else
      grep -vxF "$unwanted" "$PROFILE_FILE" > "$temporary" || true
    fi
  fi
  mv "$temporary" "$PROFILE_FILE"
}

init() {
  need docker
  need openssl
  docker compose version >/dev/null
  [[ -f "$ROOT/.env" ]] || cp "$ROOT/.env.example" "$ROOT/.env"
  install -d -m 700 "$ROOT/secrets"
  install -d -m 755 "$ROOT/downloads" "$ROOT/kept" "$ROOT/.apple-provider/incoming"
  if [[ ! -s "$ROOT/secrets/postgres-password.txt" ]]; then
    umask 077
    openssl rand -base64 36 > "$ROOT/secrets/postgres-password.txt"
  fi
  if [[ ! -s "$ROOT/secrets/allstarr-keyring.json" ]]; then
    umask 077
    local key
    key="$(openssl rand -base64 32)"
    printf '{"activeKeyId":"key-1","keys":{"key-1":"%s"}}\n' "$key" > "$ROOT/secrets/allstarr-keyring.json"
  fi
  chmod 600 "$ROOT/secrets/postgres-password.txt" "$ROOT/secrets/allstarr-keyring.json"
  touch "$PROFILE_FILE"
  [[ -f "$MODE_FILE" ]] || printf '%s\n' "${1:-release}" > "$MODE_FILE"
  deployment_mode >/dev/null
  echo "Allstarr is initialized. Edit .env, then run: ./allstarr.sh up"
}

prepare_apple() {
  local input="${1:-}" arch="${2:-}" runtime="linux/amd64"
  [[ -f "$ROOT/.env" ]] || die "run ./allstarr.sh init before enabling providers"
  if [[ "$input" == "x86_64" || "$input" == "arm64-v8a" ]]; then
    arch="$input"
    input=""
  fi
  if [[ -z "$arch" ]]; then
    case "$(uname -m)" in
      x86_64|amd64) arch="x86_64" ;;
      arm64|aarch64) arch="arm64-v8a"; runtime="linux/arm64" ;;
      *) die "could not detect Apple architecture; pass x86_64 or arm64-v8a explicitly" ;;
    esac
  fi
  if [[ -z "$input" ]]; then
    local candidate
    for candidate in "$ROOT"/.apple-provider/incoming/*.apk "$ROOT"/.apple-provider/incoming/*.apkm; do
      if [[ -f "$candidate" && ( -z "$input" || "$candidate" -nt "$input" ) ]]; then
        input="$candidate"
      fi
    done
  fi
  [[ -n "$input" && ( -f "$input" || -d "$input" ) ]] || die "no staged Apple package found; upload an .apk/.apkm in Integrations > Services > Apple Music – GAMDL first"
  case "$arch" in
    x86_64) ;;
    arm64-v8a) runtime=linux/arm64 ;;
    *) die "Apple architecture must be x86_64 or arm64-v8a" ;;
  esac
  if [[ -d "$input" ]]; then
    bash "$ROOT/tools/apple-provider/prepare.sh" --staged-libs "$input" --arch "$arch"
  else
    bash "$ROOT/tools/apple-provider/prepare.sh" --apkm "$input" --arch "$arch"
  fi
  if grep -q '^APPLE_WRAPPER_TARGET_ARCH=' "$ROOT/.env"; then
    sed -i.bak "s|^APPLE_WRAPPER_TARGET_ARCH=.*|APPLE_WRAPPER_TARGET_ARCH=$arch|" "$ROOT/.env"
    sed -i.bak "s|^APPLE_WRAPPER_RUNTIME_PLATFORM=.*|APPLE_WRAPPER_RUNTIME_PLATFORM=$runtime|" "$ROOT/.env"
    rm -f "$ROOT/.env.bak"
  else
    printf '\nAPPLE_WRAPPER_TARGET_ARCH=%s\nAPPLE_WRAPPER_RUNTIME_PLATFORM=%s\n' "$arch" "$runtime" >> "$ROOT/.env"
  fi
  remember_profile apple
  compose_args
  docker compose "${COMPOSE[@]}" build apple-wrapper apple-gateway
  echo "Apple provider source, verified native libraries, and local images are ready. Run: ./allstarr.sh up"
}

install_apple() {
  prepare_apple "$@"
  up
}

validate_deployment_files() {
  local env_file="$ROOT/.env"
  [[ -f "$env_file" ]] || die "missing .env; run ./allstarr.sh init first"
  awk '
    /^[[:space:]]*($|#)/ { next }
    !/^[A-Za-z_][A-Za-z0-9_]*=/ {
      printf "Invalid .env line %d: expected KEY=value\n", NR > "/dev/stderr"
      bad = 1
      next
    }
    {
      key = $0
      sub(/=.*/, "", key)
      if (seen[key]++) {
        printf "Duplicate .env key on line %d: %s\n", NR, key > "/dev/stderr"
        bad = 1
      }
    }
    END { exit bad ? 1 : 0 }
  ' "$env_file"
  if [[ -f "$PROFILE_FILE" ]]; then
    while IFS= read -r profile; do
      case "$profile" in
        ""|apple|spotify|spotify-lyrics) ;;
        *) die "unsupported saved profile '$profile'; use apple or spotify-lyrics" ;;
      esac
    done < "$PROFILE_FILE"
  fi
}

up() {
  validate_deployment_files
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if [[ "$(deployment_mode)" == source ]]; then
    docker compose "${COMPOSE[@]}" build allstarr
  fi
  start_stack
  docker compose "${COMPOSE[@]}" ps
}

update() {
  validate_deployment_files
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if [[ "$(deployment_mode)" == release ]]; then
    docker compose "${COMPOSE[@]}" pull postgres allstarr
    if profiles | grep -qx spotify-lyrics; then
      docker compose "${COMPOSE[@]}" pull spotify-lyrics
    fi
  fi
  if [[ "$(deployment_mode)" == source ]]; then
    need git
    [[ -d "$ROOT/.git" ]] || die "source mode requires a Git checkout"
    git diff --quiet && git diff --cached --quiet ||
      die "tracked source files have local changes; commit or stash them before updating"
    git pull --ff-only
    docker image prune --force
    docker builder prune --force --min-free-space 8GB
    docker compose "${COMPOSE[@]}" build allstarr
    if profiles | grep -qx apple; then
      docker compose "${COMPOSE[@]}" build apple-gateway
    fi
  elif profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" build apple-gateway
  fi
  start_stack
  docker compose "${COMPOSE[@]}" ps
}

create_state_archive() {
  local output_dir="$1" staging archive runtime_image host_uid host_gid archive_paths
  local -a optional_volume_mounts=()
  output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd)"
  chmod 700 "$output_dir"
  staging="$(mktemp -d "$output_dir/.allstarr-export.XXXXXX")"
  archive="$output_dir/allstarr-upgrade-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
  runtime_image="$(docker compose "${COMPOSE[@]}" images -q postgres | head -1)"
  [[ -n "$runtime_image" ]] || die "the Postgres image must exist before state can be exported"
  host_uid="$(id -u)"
  host_gid="$(id -g)"
  archive_paths="volume-state volume-cache volume-postgres"
  while IFS='|' read -r volume_name archive_path; do
    if docker volume inspect "$volume_name" >/dev/null 2>&1; then
      optional_volume_mounts+=(-v "$volume_name:/$archive_path:ro")
      archive_paths+=" $archive_path"
    fi
  done <<'EOF'
allstarr_apple-gateway-data|volume-apple-gateway
allstarr_apple-wrapper-session|volume-apple-wrapper-session
EOF

  docker run --rm --read-only \
    -e HOST_UID="$host_uid" -e HOST_GID="$host_gid" \
    -e ARCHIVE_PATHS="$archive_paths" \
    -v allstarr_allstarr-state:/volume-state:ro \
    -v allstarr_allstarr-cache:/volume-cache:ro \
    -v allstarr_postgres-data:/volume-postgres:ro \
    "${optional_volume_mounts[@]}" \
    -v "$staging:/export" \
    "$runtime_image" sh -c '
      tar -czf /export/volume-data.tar.gz -C / $ARCHIVE_PATHS &&
      chown "$HOST_UID:$HOST_GID" /export/volume-data.tar.gz &&
      chmod 600 /export/volume-data.tar.gz
    '

  tar -cf "$staging/deployment-files.tar" --ignore-failed-read \
    .env .allstarr-profiles .allstarr-mode secrets .apple-provider
  printf '%s\n' \
    'Allstarr portable upgrade export' \
    "Created: $(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    'Includes: configuration, encryption keyring, provider profiles, Postgres, mappings, playlist caches, durable application state, and Apple provider/session volumes when present.' \
    'Does not include downloaded or kept music; those host folders remain where the user mounted them.' \
    > "$staging/README.txt"
  tar -czf "$archive" -C "$staging" README.txt deployment-files.tar volume-data.tar.gz
  chmod 600 "$archive"
  rm -r "$staging"
  printf '%s\n' "$archive"
}

backup_state() {
  local output_dir="${1:-$ROOT/allstarr-backups}" restart_after="${2:-true}" was_running result archive
  compose_args
  was_running="$(docker compose "${COMPOSE[@]}" ps --status running -q | head -1)"
  echo "Stopping Allstarr briefly so every database and cache file is consistent..."
  docker compose "${COMPOSE[@]}" stop
  set +e
  archive="$(create_state_archive "$output_dir")"
  result=$?
  set -e
  if [[ "$restart_after" == true && -n "$was_running" ]]; then
    start_stack
  fi
  [[ $result -eq 0 ]] || die "state export failed; the stopped services were left unchanged"
  echo "Portable upgrade export created: $archive"
}

upgrade() {
  backup_state "${1:-$ROOT/allstarr-backups}" false
  update
}

validate_restore_archive() {
  local archive="$1" staging="$2" entry
  [[ -f "$archive" ]] || die "backup archive not found: $archive"
  case "$archive" in *.tar.gz|*.tgz) ;; *) die "restore requires an Allstarr .tar.gz backup" ;; esac

  while IFS= read -r entry; do
    case "$entry" in README.txt|deployment-files.tar|volume-data.tar.gz) ;;
      *) die "backup contains an unexpected top-level entry: $entry" ;;
    esac
  done < <(tar -tzf "$archive")
  tar -xzf "$archive" -C "$staging"
  [[ -s "$staging/deployment-files.tar" && -s "$staging/volume-data.tar.gz" ]] ||
    die "backup is incomplete; deployment-files.tar and volume-data.tar.gz are required"
  ! tar -tvf "$staging/deployment-files.tar" | awk '$1 ~ /^[lh]/ { found=1 } END { exit !found }' ||
    die "backup deployment files may not contain links"
  ! tar -tvzf "$staging/volume-data.tar.gz" | awk '$1 ~ /^[lh]/ { found=1 } END { exit !found }' ||
    die "backup volume data may not contain links"

  while IFS= read -r entry; do
    entry="${entry#./}"
    case "$entry" in
      .env|.allstarr-profiles|.allstarr-mode|secrets|secrets/*|.apple-provider|.apple-provider/*) ;;
      *) die "backup contains an unsafe deployment path: $entry" ;;
    esac
  done < <(tar -tf "$staging/deployment-files.tar")
  while IFS= read -r entry; do
    entry="${entry#./}"
    case "$entry" in
      volume-state|volume-state/*|volume-cache|volume-cache/*|volume-postgres|volume-postgres/*|volume-apple-gateway|volume-apple-gateway/*|volume-apple-wrapper-session|volume-apple-wrapper-session/*) ;;
      *) die "backup contains an unsafe volume path: $entry" ;;
    esac
  done < <(tar -tzf "$staging/volume-data.tar.gz")
}

restore_state() {
  local archive="${1:-}" confirmation="${2:-}" staging runtime_image was_running rollback_dir restore_paths
  local -a optional_volume_mounts=()
  [[ -n "$archive" ]] || die "usage: ./allstarr.sh restore BACKUP.tar.gz --confirm-replace"
  [[ "$confirmation" == "--confirm-replace" ]] ||
    die "restore replaces this installation's config, secrets, database, mappings, and caches; rerun with --confirm-replace"
  need docker
  need tar
  archive="$(cd "$(dirname "$archive")" && pwd)/$(basename "$archive")"
  staging="$(mktemp -d)"
  trap 'rm -rf "$staging"' EXIT
  validate_restore_archive "$archive" "$staging"

  compose_args
  runtime_image="$(docker compose "${COMPOSE[@]}" images -q postgres | head -1)"
  [[ -n "$runtime_image" ]] || die "initialize or pull the Allstarr stack before restoring"
  was_running="$(docker compose "${COMPOSE[@]}" ps --status running -q | head -1)"
  rollback_dir="$ROOT/allstarr-backups/pre-restore"
  echo "Creating a rollback backup of the current installation..."
  backup_state "$rollback_dir" false

  echo "Restoring configuration, encrypted accounts, databases, mappings, and caches..."
  tar -xf "$staging/deployment-files.tar" -C "$ROOT"
  chmod 600 "$ROOT/.env" "$ROOT/secrets/postgres-password.txt" "$ROOT/secrets/allstarr-keyring.json" 2>/dev/null || true
  restore_paths="/volume-state /volume-cache /volume-postgres"
  while IFS='|' read -r archive_path volume_name; do
    if tar -tzf "$staging/volume-data.tar.gz" | grep -q "^$archive_path\\(/\\|$\\)"; then
      optional_volume_mounts+=(-v "$volume_name:/$archive_path")
      restore_paths+=" /$archive_path"
    fi
  done <<'EOF'
volume-apple-gateway|allstarr_apple-gateway-data
volume-apple-wrapper-session|allstarr_apple-wrapper-session
EOF
  docker run --rm --read-only \
    -e RESTORE_PATHS="$restore_paths" \
    -v allstarr_allstarr-state:/volume-state \
    -v allstarr_allstarr-cache:/volume-cache \
    -v allstarr_postgres-data:/volume-postgres \
    "${optional_volume_mounts[@]}" \
    -v "$staging:/restore:ro" \
    "$runtime_image" sh -c '
      find $RESTORE_PATHS -mindepth 1 -delete &&
      tar -xzf /restore/volume-data.tar.gz -C /
    '

  if [[ -n "$was_running" ]]; then
    compose_args
    start_stack
  fi
  echo "Restore complete. A rollback backup of the replaced installation is in: $rollback_dir"
  rm -rf "$staging"
  trap - EXIT
}

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  init [release|source]             Create config; default to release images
  mode [release|source]             Show or change the saved deployment mode
  up                                Start the saved deployment profile
  update                            Pull the saved release/source and safely recreate
  upgrade [OUTPUT_DIR]              Export all user state, then update and restart
  backup [OUTPUT_DIR]               Export config, secrets, databases, mappings, and caches
  restore BACKUP --confirm-replace  Restore a portable backup; saves current state first
  status                            Show containers and the saved profile
  logs [service]                    Follow redacted container logs
  enable spotify-lyrics             Add an optional saved profile
  disable spotify-lyrics|apple      Remove an optional profile on next up
  prepare-apple [INPUT] [ARCH]      Verify an APK/APKM or staged libs; enable Apple
  install-apple [ARCH]              Build and start Apple from the WebUI-staged package
  down                              Stop containers without deleting data

The deployment mode is saved in .allstarr-mode. Release mode pulls reviewed
images; source mode fast-forwards its tracked branch, then builds the local image.
Optional profiles are saved in .allstarr-profiles. No command deletes volumes,
Postgres data, managed music, provider sessions, or imported settings.
EOF
}

command="${1:-help}"
shift || true
cd "$ROOT"
case "$command" in
  init)
    case "${1:-release}" in release|source) init "${1:-release}" ;; *) die "init mode must be release or source" ;; esac
    ;;
  mode)
    if [[ $# -eq 0 ]]; then deployment_mode; else set_mode "$1"; fi
    ;;
  prepare-apple) prepare_apple "$@" ;;
  install-apple) install_apple "$@" ;;
  up) up ;;
  update) update ;;
  upgrade) upgrade "$@" ;;
  backup) backup_state "${1:-$ROOT/allstarr-backups}" true ;;
  restore) restore_state "$@" ;;
  status) compose_args; echo "Mode: $(deployment_mode)"; echo "Profiles: $(profiles | paste -sd, -)"; docker compose "${COMPOSE[@]}" ps ;;
  logs) compose_args; docker compose "${COMPOSE[@]}" logs --tail=200 -f "$@" ;;
  enable)
    case "${1:-}" in
      spotify|spotify-lyrics) remember_profile "$1" ;;
      apple) die "use prepare-apple for Apple so its libraries are verified first" ;;
      *) die "choose spotify-lyrics" ;;
    esac
    echo "Profile enabled. Run: ./allstarr.sh up"
    ;;
  disable)
    case "${1:-}" in spotify|spotify-lyrics|apple) forget_profile "$1" ;; *) die "choose spotify-lyrics or apple" ;; esac
    echo "Profile disabled. Run ./allstarr.sh up to apply; stored data is preserved."
    ;;
  down) compose_args; docker compose "${COMPOSE[@]}" down ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
