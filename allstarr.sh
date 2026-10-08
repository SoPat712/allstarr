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
  docker compose version >/dev/null
  [[ -f "$ROOT/.env" ]] || cp "$ROOT/.env.example" "$ROOT/.env"
  install -d -m 755 "$ROOT/.apple-provider/incoming"
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
    docker compose "${COMPOSE[@]}" pull allstarr
    if profiles | grep -qx spotify-lyrics; then
      docker compose "${COMPOSE[@]}" pull spotify-lyrics
    fi
  fi
  if [[ "$(deployment_mode)" == source ]]; then
    need git
    [[ -d "$ROOT/.git" ]] || die "source mode requires a Git checkout"
    git -c "safe.directory=$ROOT" diff --quiet &&
      git -c "safe.directory=$ROOT" diff --cached --quiet ||
      die "tracked source files have local changes; commit or stash them before updating"
    git -c "safe.directory=$ROOT" pull --ff-only
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

usage() {
  cat <<'EOF'
Usage: ./allstarr.sh COMMAND

  init [release|source]             Create config; default to release images
  mode [release|source]             Show or change the saved deployment mode
  up                                Start the saved deployment profile
  update                            Pull the saved release/source and safely recreate
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
SQLite data, managed music, provider sessions, or imported settings.
Use Settings > Maintenance for database backups and restore on restart.
Stop Allstarr before copying its data folder to another host.
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
  backup|restore|upgrade) die "use Settings > Maintenance for backups and restores; use update after saving a backup" ;;
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
