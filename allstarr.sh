#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "${BASH_SOURCE[0]%/*}" && pwd)"
PROFILE_FILE="$ROOT/.allstarr-profiles"

die() { echo "allstarr: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is required"; }

profiles() {
  local value
  if [[ -f "$PROFILE_FILE" ]]; then
    while IFS= read -r value; do
      case "$value" in
        apple) printf '%s\n' "$value" ;;
      esac
    done < "$PROFILE_FILE"
  fi
}

require_release() {
  local mode=release
  [[ ! -f "$ROOT/.allstarr-mode" ]] || read -r mode < "$ROOT/.allstarr-mode"
  [[ "$mode" == release ]] || die "source mode moved to ./tools/develop.sh; use that script or run ./tools/develop.sh mode release"
}

compose_args() {
  COMPOSE=(-f "$ROOT/docker-compose.yml")
  while IFS= read -r profile; do
    case "$profile" in
      apple) COMPOSE+=(--profile apple) ;;
    esac
  done < <(profiles)
}

start_stack() {
  docker compose "${COMPOSE[@]}" up -d --remove-orphans --wait --wait-timeout 180
}

remember_profile() {
  local wanted="$1" temporary
  touch "$PROFILE_FILE"
  temporary="$(mktemp)"
  awk '$0 != "spotify" && $0 != "spotify-lyrics" && !seen[$0]++' "$PROFILE_FILE" > "$temporary"
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
  docker compose version >/dev/null
  [[ -f "$ROOT/.env" ]] || cp "$ROOT/.env.example" "$ROOT/.env"
  echo "Optional deployment defaults are in .env. Set BACKEND_TYPE and JELLYFIN_URL or SUBSONIC_URL if backend selection is unavailable in the dashboard."
  echo "Start with: docker compose up -d"
}

prepare_apple() {
  validate_deployment_files
  local input="${1:-}" arch="${2:-}" runtime="linux/amd64"
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
    need jq
    local candidate data_path
    compose_args
    data_path="$(docker compose "${COMPOSE[@]}" config --format json |
      jq -r '.services.allstarr.volumes[] | select(.target == "/app/data" and .type == "bind") | .source')"
    [[ -n "$data_path" ]] || data_path="$ROOT/.no-bind-data"
    for candidate in "$data_path"/apple-upload/*.apk "$data_path"/apple-upload/*.apkm \
      "$ROOT"/.apple-provider/incoming/*.apk "$ROOT"/.apple-provider/incoming/*.apkm; do
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
  local temporary
  temporary="$(mktemp "$ROOT/.apple-env.XXXXXX")"
  if [[ -f "$ROOT/.env" ]]; then
    awk '!/^APPLE_WRAPPER_(TARGET_ARCH|RUNTIME_PLATFORM)=/' "$ROOT/.env" > "$temporary"
  fi
  printf '\nAPPLE_WRAPPER_TARGET_ARCH=%s\nAPPLE_WRAPPER_RUNTIME_PLATFORM=%s\n' "$arch" "$runtime" >> "$temporary"
  mv "$temporary" "$ROOT/.env"
  compose_args
  docker compose "${COMPOSE[@]}" --profile apple build apple-wrapper apple-gateway
  remember_profile apple
  echo "Apple source, verified native libraries, and local images are ready."
}

install_apple() {
  prepare_apple "$@"
  if [[ -f "$ROOT/.allstarr-mode" ]] && grep -qx source "$ROOT/.allstarr-mode"; then
    bash "$ROOT/tools/develop.sh" up
  else
    up
  fi
}

validate_deployment_files() {
  local env_file="$ROOT/.env"
  if [[ -f "$env_file" ]]; then
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
  fi
  if [[ -f "$PROFILE_FILE" ]]; then
    while IFS= read -r profile; do
      case "$profile" in
        ""|apple) ;;
        spotify|spotify-lyrics) echo "Spotify lyrics runs inside Allstarr. Configure its account in Integrations > Accounts." >&2 ;;
        *) die "unsupported saved profile '$profile'; use apple" ;;
      esac
    done < "$PROFILE_FILE"
  fi
}

up() {
  require_release
  validate_deployment_files
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" build apple-gateway
  fi
  start_stack
  docker compose "${COMPOSE[@]}" ps
}

update() {
  require_release
  validate_deployment_files
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  docker compose "${COMPOSE[@]}" pull allstarr
  if profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" build apple-gateway
  fi
  start_stack
  docker compose "${COMPOSE[@]}" ps
}

disable_apple() {
  validate_deployment_files
  compose_args
  docker compose "${COMPOSE[@]}" --profile apple stop apple-gateway apple-wrapper
  docker compose "${COMPOSE[@]}" --profile apple rm -f apple-gateway apple-wrapper
  forget_profile apple
  echo "Apple disabled. Stored data, sessions, and accounts are preserved."
}

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  enable apple [INPUT] [ARCH]       Verify staged libraries, build and start Apple
  disable apple                     Stop and remove Apple containers; preserve sessions
  update                            Pull the configured image and recreate saved profiles
  status                            Show containers and saved profiles
  logs [service]                    Follow container logs
  up                                Start saved profiles without pulling an image
  down                              Stop saved profiles without deleting data
  init                              Create optional .env defaults for backend setup
  upgrade                           Show backup and in-place upgrade guidance
  prepare-apple [INPUT] [ARCH]       Verify and build Apple without starting it
  install-apple [INPUT] [ARCH]       Alias for enable apple

Plain docker compose up -d starts the core service. Optional profiles are saved
in .allstarr-profiles for this script. Source builds use ./tools/develop.sh.
Use Settings > Maintenance for database backups and restore on restart.
Stop Allstarr before copying its data folder. No command deletes data volumes.
EOF
}

command="${1:-help}"
shift || true
cd "$ROOT"
case "$command" in
  init) [[ $# -eq 0 ]] || die "source builds moved to ./tools/develop.sh"; init ;;
  prepare-apple) prepare_apple "$@" ;;
  install-apple) install_apple "$@" ;;
  up) up ;;
  update) update ;;
  upgrade) die "save a stopped-folder backup, then use update for this layout or your existing Compose commands for v2; review legacy settings in Settings > Setup" ;;
  status) compose_args; echo "Profiles: $(profiles | paste -sd, -)"; docker compose "${COMPOSE[@]}" ps ;;
  logs) compose_args; docker compose "${COMPOSE[@]}" logs --tail=200 -f "$@" ;;
  enable)
    [[ "${1:-}" == apple ]] || die "only apple is an optional profile; Spotify lyrics runs inside Allstarr"
    shift
    install_apple "$@"
    ;;
  disable)
    case "${1:-}" in
      apple) disable_apple ;;
      spotify|spotify-lyrics) forget_profile "$1" ;;
      *) die "choose apple" ;;
    esac
    ;;
  down) compose_args; docker compose "${COMPOSE[@]}" down ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
