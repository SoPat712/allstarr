#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "${BASH_SOURCE[0]%/*}/.." && pwd)"
MODE_FILE="$ROOT/.allstarr-mode"
die() { echo "develop: $*" >&2; exit 1; }
mode=source
[[ ! -f "$MODE_FILE" ]] || read -r mode < "$MODE_FILE"
case "$mode" in release|source) ;; *) die "invalid mode in .allstarr-mode" ;; esac
command="${1:-help}"
shift || true
cd "$ROOT"
case "$command" in
  mode)
    if [[ $# -eq 0 ]]; then echo "$mode"; exit 0; fi
    case "$1" in release|source) printf '%s\n' "$1" > "$MODE_FILE" ;; *) die "mode must be release or source" ;; esac
    echo "Deployment mode set to $1. Run: ./tools/develop.sh up"
    exit 0
    ;;
  up|update) ;;
  *) echo "Usage: ./tools/develop.sh {mode [release|source]|up|update}"; exit 2 ;;
esac
if [[ "$mode" == release ]]; then exec bash "$ROOT/allstarr.sh" "$command"; fi
COMPOSE=(-f "$ROOT/docker-compose.yml" -f "$ROOT/tools/docker-compose.source.yml")
if [[ -f "$ROOT/.allstarr-profiles" ]]; then
  while IFS= read -r profile; do
    case "$profile" in
      apple) COMPOSE+=(--profile apple) ;;
      sessions)
        COMPOSE+=(--profile sessions)
        export SESSION_VERIFIER_URL="${SESSION_VERIFIER_URL:-http://session-verifier:8000}"
        ;;
      ""|spotify|spotify-lyrics) ;;
      *) die "unsupported saved profile '$profile'" ;;
    esac
  done < "$ROOT/.allstarr-profiles"
fi
docker compose "${COMPOSE[@]}" config --quiet
if [[ "$command" == update ]]; then
  git -c "safe.directory=$ROOT" rev-parse --is-inside-work-tree >/dev/null || die "source updates require a Git checkout"
  git -c "safe.directory=$ROOT" diff --quiet &&
    git -c "safe.directory=$ROOT" diff --cached --quiet || die "tracked source files have local changes; commit or stash them before updating"
  [[ -n "$(git -c "safe.directory=$ROOT" symbolic-ref --quiet --short HEAD)" ]] || die "source updates require a tracked branch"
  git -c "safe.directory=$ROOT" pull --ff-only
fi
docker compose "${COMPOSE[@]}" build allstarr
if [[ -f "$ROOT/.allstarr-profiles" ]] && grep -qx apple "$ROOT/.allstarr-profiles"; then
  docker compose "${COMPOSE[@]}" build apple-gateway
fi
if [[ -f "$ROOT/.allstarr-profiles" ]] && grep -qx sessions "$ROOT/.allstarr-profiles"; then
  docker compose "${COMPOSE[@]}" build session-verifier
fi
printf '%s\n' "$mode" > "$MODE_FILE"
docker compose "${COMPOSE[@]}" up -d --remove-orphans --wait --wait-timeout 180
docker compose "${COMPOSE[@]}" ps
