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
        apple|sessions) printf '%s\n' "$value" ;;
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
      sessions)
        COMPOSE+=(--profile sessions)
        export SESSION_VERIFIER_URL="${SESSION_VERIFIER_URL:-http://session-verifier:8000}"
        ;;
    esac
  done < <(profiles)
}

preserve_existing_data() {
  [[ -z "${ALLSTARR_DATA_PATH+x}" ]] || return 0
  if [[ -f "$ROOT/.env" ]] && grep -q '^ALLSTARR_DATA_PATH=' "$ROOT/.env"; then
    return 0
  fi

  local project volume volumes entries="" image status
  project="$(docker compose "${COMPOSE[@]}" config | awk '/^name: / { gsub(/"/, "", $2); print $2 }')"
  [[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || die "could not resolve the Compose project name; set ALLSTARR_DATA_PATH explicitly"
  volume="${project}_allstarr-data"
  volumes="$(docker volume ls --filter "name=^${volume}$" --format '{{.Name}}')" || die "could not list data volumes; set ALLSTARR_DATA_PATH explicitly before starting"
  [[ "$volumes" == "$volume" ]] || return 0
  docker volume inspect "$volume" >/dev/null 2>&1 || die "could not inspect $volume; set ALLSTARR_DATA_PATH explicitly before starting"

  if [[ -d "$ROOT/data" ]]; then
    entries="$(find "$ROOT/data/." -mindepth 1 -maxdepth 1 -print -quit)" || die "could not inspect ./data; set ALLSTARR_DATA_PATH explicitly"
  elif [[ -e "$ROOT/data" ]]; then
    die "./data is not a directory; set ALLSTARR_DATA_PATH explicitly"
  fi
  if [[ -n "$entries" ]]; then
    image="$(docker compose "${COMPOSE[@]}" config --images allstarr)"
    [[ -n "$image" && "$image" != *$'\n'* ]] || die "could not resolve the Allstarr image to inspect $volume; set ALLSTARR_DATA_PATH explicitly"
    status=0
    docker run --rm --pull never --network none --read-only \
      --mount "type=volume,src=$volume,dst=/existing,readonly,volume-nocopy" \
      --entrypoint /bin/sh "$image" -c \
      'entries="$(find /existing -mindepth 1 -maxdepth 1 -print -quit)" || exit 2; if [ -n "$entries" ]; then exit 1; fi' || status=$?
    case "$status" in
      0) return 0 ;;
      1) die "both ./data and $volume contain data; set ALLSTARR_DATA_PATH explicitly before starting" ;;
      *) die "could not inspect $volume with the local Allstarr image; set ALLSTARR_DATA_PATH explicitly before starting" ;;
    esac
  fi

  printf '\nALLSTARR_DATA_PATH=allstarr-data\n' >> "$ROOT/.env"
  echo "Retaining existing Allstarr data volume $volume."
}

start_stack() {
  preserve_existing_data
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
        ""|apple|sessions) ;;
        spotify|spotify-lyrics) echo "Spotify lyrics runs inside Allstarr. Configure its account in Integrations > Accounts." >&2 ;;
        *) die "unsupported saved profile '$profile'; use apple or sessions" ;;
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
  if profiles | grep -qx sessions; then
    docker compose "${COMPOSE[@]}" build session-verifier
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
  if profiles | grep -qx sessions; then
    docker compose "${COMPOSE[@]}" build session-verifier
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

enable_sessions() {
  validate_deployment_files
  remember_profile sessions
  if [[ -f "$ROOT/.allstarr-mode" ]] && grep -qx source "$ROOT/.allstarr-mode"; then
    bash "$ROOT/tools/develop.sh" up
  else
    up
  fi
}

disable_sessions() {
  compose_args
  docker compose "${COMPOSE[@]}" --profile sessions stop session-verifier
  docker compose "${COMPOSE[@]}" --profile sessions rm -f session-verifier
  forget_profile sessions
  export SESSION_VERIFIER_URL=""
  if [[ -f "$ROOT/.allstarr-mode" ]] && grep -qx source "$ROOT/.allstarr-mode"; then
    bash "$ROOT/tools/develop.sh" up
  else
    up
  fi
}

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  enable apple [INPUT] [ARCH]       Verify staged libraries, build and start Apple
  disable apple                     Stop and remove Apple containers; preserve sessions
  enable sessions                   Build and start automatic extension sign-in verification
  disable sessions                  Stop automatic verification; retain saved sign-ins
  update                            Pull the configured image and recreate saved profiles
  status                            Show containers and saved profiles
  logs [service]                    Follow container logs
  up                                Start saved profiles without pulling an image
  down                              Stop saved profiles without deleting data
  prepare-apple [INPUT] [ARCH]       Verify and build Apple without starting it
  install-apple [INPUT] [ARCH]       Alias for enable apple

Plain docker compose up -d starts the core service. Optional profiles are saved
in .allstarr-profiles for this script. Source builds use ./tools/develop.sh.
Use Settings > Backups for database backups and restore on restart.
Stop Allstarr before copying its data folder. No command deletes data volumes.
EOF
}

command="${1:-help}"
shift || true
cd "$ROOT"
case "$command" in
  prepare-apple) prepare_apple "$@" ;;
  install-apple) install_apple "$@" ;;
  up) up ;;
  update) update ;;
  status) compose_args; echo "Profiles: $(profiles | paste -sd, -)"; docker compose "${COMPOSE[@]}" ps ;;
  logs) compose_args; docker compose "${COMPOSE[@]}" logs --tail=200 -f "$@" ;;
  enable)
    case "${1:-}" in
      apple) shift; install_apple "$@" ;;
      sessions) enable_sessions ;;
      *) die "choose apple or sessions; Spotify lyrics runs inside Allstarr" ;;
    esac
    ;;
  disable)
    case "${1:-}" in
      apple) disable_apple ;;
      sessions) disable_sessions ;;
      spotify|spotify-lyrics) forget_profile "$1" ;;
      *) die "choose apple or sessions" ;;
    esac
    ;;
  down) compose_args; docker compose "${COMPOSE[@]}" down ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
