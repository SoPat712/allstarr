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
        apple|aio) values+=("$value") ;;
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
  [[ "$(deployment_mode)" == source ]] && COMPOSE+=(-f "$ROOT/docker-compose.dev.yml")
  while IFS= read -r profile; do
    case "$profile" in
      spotify-lyrics) COMPOSE+=(-f "$ROOT/docker-compose.spotify-lyrics.yml") ;;
      apple) COMPOSE+=(-f "$ROOT/docker-compose.apple.yml") ;;
      aio) COMPOSE+=(-f "$ROOT/docker-compose.aio.yml") ;;
    esac
  done < <(profiles)
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
  install -d -m 755 "$ROOT/downloads" "$ROOT/kept"
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
  local input="${1:-}" arch="${2:-x86_64}" runtime="linux/amd64"
  [[ -f "$ROOT/.env" ]] || die "run ./allstarr.sh init before enabling providers"
  [[ -n "$input" && ( -f "$input" || -d "$input" ) ]] || die "usage: ./allstarr.sh prepare-apple APK_OR_STAGED_LIBS [x86_64|arm64-v8a]"
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

up() {
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if [[ "$(deployment_mode)" == source ]]; then
    docker compose "${COMPOSE[@]}" build allstarr
  fi
  docker compose "${COMPOSE[@]}" up -d --remove-orphans
  docker compose "${COMPOSE[@]}" ps
}

update() {
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if [[ "$(deployment_mode)" == release ]]; then
    docker compose "${COMPOSE[@]}" pull --ignore-buildable
  fi
  if [[ "$(deployment_mode)" == source ]]; then
    docker compose "${COMPOSE[@]}" build allstarr
    if profiles | grep -qx apple; then
      docker compose "${COMPOSE[@]}" build apple-gateway
    fi
  elif profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" build apple-gateway
  fi
  docker compose "${COMPOSE[@]}" up -d --remove-orphans
  docker compose "${COMPOSE[@]}" ps
}

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  init [release|source]             Create config; default to release images
  mode [release|source]             Show or change the saved deployment mode
  up                                Start the saved deployment profile
  update                            Pull reviewed images and safely recreate
  status                            Show containers and the saved profile
  logs [service]                    Follow redacted container logs
  enable spotify-lyrics|aio         Add an optional saved profile
  disable spotify-lyrics|apple|aio  Remove an optional profile on next up
  prepare-apple INPUT [ARCH]        Verify an APK/APKM or staged libs; enable Apple
  down                              Stop containers without deleting data

The deployment mode is saved in .allstarr-mode. Release mode pulls reviewed
images; source mode builds the checked-out commit using docker-compose.dev.yml.
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
  up) up ;;
  update) update ;;
  status) compose_args; echo "Mode: $(deployment_mode)"; echo "Profiles: $(profiles | paste -sd, -)"; docker compose "${COMPOSE[@]}" ps ;;
  logs) compose_args; docker compose "${COMPOSE[@]}" logs --tail=200 -f "$@" ;;
  enable)
    case "${1:-}" in
      spotify|spotify-lyrics|aio) remember_profile "$1" ;;
      apple) die "use prepare-apple for Apple so its libraries are verified first" ;;
      *) die "choose spotify-lyrics or aio" ;;
    esac
    echo "Profile enabled. Run: ./allstarr.sh up"
    ;;
  disable)
    case "${1:-}" in spotify|spotify-lyrics|apple|aio) forget_profile "$1" ;; *) die "choose spotify-lyrics, apple, or aio" ;; esac
    echo "Profile disabled. Run ./allstarr.sh up to apply; stored data is preserved."
    ;;
  down) compose_args; docker compose "${COMPOSE[@]}" down ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
