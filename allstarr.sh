#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "${BASH_SOURCE[0]%/*}" && pwd)"
PROFILE_FILE="$ROOT/.allstarr-profiles"

die() { echo "allstarr: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is required"; }

profiles() {
  local values=(standard)
  if [[ -f "$PROFILE_FILE" ]]; then
    while IFS= read -r value; do
      case "$value" in spotify|apple|aio) values+=("$value") ;; esac
    done < "$PROFILE_FILE"
  fi
  printf '%s\n' "${values[@]}"
}

compose_args() {
  COMPOSE=(-f "$ROOT/docker-compose.yml")
  while IFS= read -r profile; do
    case "$profile" in
      spotify) COMPOSE+=(-f "$ROOT/docker-compose.spotify-lyrics.yml") ;;
      apple) COMPOSE+=(-f "$ROOT/docker-compose.apple.yml") ;;
      aio) COMPOSE+=(-f "$ROOT/docker-compose.aio.yml") ;;
    esac
  done < <(profiles)
}

remember_profile() {
  local wanted="$1"
  touch "$PROFILE_FILE"
  grep -qxF "$wanted" "$PROFILE_FILE" 2>/dev/null || printf '%s\n' "$wanted" >> "$PROFILE_FILE"
}

forget_profile() {
  local unwanted="$1" temporary
  temporary="$(mktemp)"
  if [[ -f "$PROFILE_FILE" ]]; then
    grep -vxF "$unwanted" "$PROFILE_FILE" > "$temporary" || true
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
  echo "Allstarr is initialized. Edit .env, then run: ./allstarr.sh up"
}

prepare_apple() {
  local bundle="${1:-}" arch="${2:-x86_64}" runtime="linux/amd64"
  [[ -f "$ROOT/.env" ]] || die "run ./allstarr.sh init before enabling providers"
  [[ -n "$bundle" && -f "$bundle" ]] || die "usage: ./allstarr.sh prepare-apple /path/to/apple-music.apk[m] [x86_64|arm64-v8a]"
  case "$arch" in
    x86_64) ;;
    arm64-v8a) runtime=linux/arm64 ;;
    *) die "Apple architecture must be x86_64 or arm64-v8a" ;;
  esac
  bash "$ROOT/tools/apple-provider/prepare.sh" --apkm "$bundle" --arch "$arch"
  if grep -q '^APPLE_WRAPPER_TARGET_ARCH=' "$ROOT/.env"; then
    sed -i.bak "s|^APPLE_WRAPPER_TARGET_ARCH=.*|APPLE_WRAPPER_TARGET_ARCH=$arch|" "$ROOT/.env"
    sed -i.bak "s|^APPLE_WRAPPER_RUNTIME_PLATFORM=.*|APPLE_WRAPPER_RUNTIME_PLATFORM=$runtime|" "$ROOT/.env"
    rm -f "$ROOT/.env.bak"
  else
    printf '\nAPPLE_WRAPPER_TARGET_ARCH=%s\nAPPLE_WRAPPER_RUNTIME_PLATFORM=%s\n' "$arch" "$runtime" >> "$ROOT/.env"
  fi
  remember_profile apple
  echo "Apple provider source and verified native libraries are ready. Run: ./allstarr.sh up"
}

up() {
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  if profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" up -d --build --remove-orphans
  else
    docker compose "${COMPOSE[@]}" up -d --remove-orphans
  fi
  docker compose "${COMPOSE[@]}" ps
}

update() {
  compose_args
  docker compose "${COMPOSE[@]}" config --quiet
  docker compose "${COMPOSE[@]}" pull --ignore-buildable
  if profiles | grep -qx apple; then
    docker compose "${COMPOSE[@]}" build apple-wrapper apple-gateway
  fi
  docker compose "${COMPOSE[@]}" up -d --remove-orphans
  docker compose "${COMPOSE[@]}" ps
}

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  init                              Create .env, directories, and secrets
  up                                Start the saved deployment profile
  update                            Pull reviewed images and safely recreate
  status                            Show containers and the saved profile
  logs [service]                    Follow redacted container logs
  enable spotify|aio                Add an optional saved profile
  disable spotify|apple|aio         Remove an optional profile on next up
  prepare-apple FILE [ARCH]         Verify/stage a legal APK/APKM and enable Apple
  down                              Stop containers without deleting data

Optional profiles are saved in .allstarr-profiles. No command deletes volumes,
Postgres data, managed music, provider sessions, or imported settings.
EOF
}

command="${1:-help}"
shift || true
cd "$ROOT"
case "$command" in
  init) init ;;
  up) up ;;
  update) update ;;
  status) compose_args; echo "Profiles: $(profiles | paste -sd, -)"; docker compose "${COMPOSE[@]}" ps ;;
  logs) compose_args; docker compose "${COMPOSE[@]}" logs --tail=200 -f "$@" ;;
  enable)
    case "${1:-}" in
      spotify|aio) remember_profile "$1" ;;
      apple) die "use prepare-apple for Apple so its libraries are verified first" ;;
      *) die "choose spotify or aio" ;;
    esac
    echo "Profile enabled. Run: ./allstarr.sh up"
    ;;
  disable)
    case "${1:-}" in spotify|apple|aio) forget_profile "$1" ;; *) die "choose spotify, apple, or aio" ;; esac
    echo "Profile disabled. Run ./allstarr.sh up to apply; stored data is preserved."
    ;;
  down) compose_args; docker compose "${COMPOSE[@]}" down ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
