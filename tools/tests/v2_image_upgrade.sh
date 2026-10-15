#!/usr/bin/env bash
set -euo pipefail
# Isolated image qualification. Never use this harness in an installation directory.
[[ "${CI:-}" == true ]] || { echo 'Run only on a disposable CI runner.' >&2; exit 2; }
ROOT="$(cd "${BASH_SOURCE[0]%/*}/../.." && pwd)"
FIXTURE="$ROOT/allstarr.Tests/Fixtures/LegacyV2"
WORK="$(mktemp -d)"
PROJECT="v2-qualification-${GITHUB_RUN_ID:-$$}"
MOCK_PID=""
[[ -z "$(docker ps -aq --filter name='^allstarr$')" ]] || { echo 'An allstarr container already exists; refusing.' >&2; exit 2; }
cleanup() {
  local result=$?
  trap - EXIT
  if [[ "$result" != 0 ]] && declare -F compose >/dev/null; then
    compose logs --no-color --tail=120 allstarr || true
  fi
  docker compose --project-name "$PROJECT" --project-directory "$WORK" -f "$FIXTURE/docker-compose.yml" down --remove-orphans >/dev/null 2>&1 || true
  [[ -z "$MOCK_PID" ]] || kill "$MOCK_PID" 2>/dev/null || true
  rm -rf -- "$WORK" 2>/dev/null || true
  exit "$result"
}
trap cleanup EXIT
sha256sum "$FIXTURE/docker-compose.yml" "$FIXTURE/example.env" > "$WORK/fixture-sha256"
mkdir -p "$WORK/cache" "$WORK/downloads" "$WORK/kept"
cat > "$WORK/.env" <<'ENV'
BACKEND_TYPE=Jellyfin
JELLYFIN_URL=http://host.docker.internal:18096
ADMIN_BIND_ANY_IP=true
ADMIN_TRUSTED_SUBNETS=127.0.0.1/32,172.16.0.0/12,192.168.0.0/16
MUSIC_SERVICE=SquidWTF
REDIS_ENABLED=true
SPOTIFY_API_ENABLED=false
SPOTIFY_IMPORT_ENABLED=false
MUSICBRAINZ_ENABLED=false
DEBUG_REDACT_SENSITIVE_REQUEST_VALUES=false
ENV
cp "$WORK/.env" "$WORK/input.env"
printf 'legacy kept bytes\n' > "$WORK/kept/unidentified.flac"
printf 'legacy download bytes\n' > "$WORK/downloads/unidentified.flac"
printf 'legacy cache bytes\n' > "$WORK/cache/legacy-entry"
python3 -u - <<'PY' &
from http.server import BaseHTTPRequestHandler, HTTPServer
class Backend(BaseHTTPRequestHandler):
    def do_GET(self):
        body = b'{"ServerName":"native-fixture","Version":"10.11.0","Id":"fixture"}'
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args): pass
HTTPServer(('0.0.0.0', 18096), Backend).serve_forever()
PY
MOCK_PID=$!
# The old image reference and Compose input stay unchanged: this local build is never pushed.
docker build -t ghcr.io/sopat712/allstarr:latest "$ROOT"
if timeout 20s docker run --rm ghcr.io/sopat712/allstarr:latest > "$WORK/unmounted.log" 2>&1; then
  echo 'An unmounted container unexpectedly started.' >&2
  exit 1
fi
grep -q 'persistent local data folder' "$WORK/unmounted.log"
# Supply a host alias only for the fake backend; no storage, listener or application overrides.
cat > "$WORK/harness.yml" <<'YAML'
services:
  allstarr:
    extra_hosts:
      - host.docker.internal:host-gateway
YAML
compose() { docker compose --project-name "$PROJECT" --project-directory "$WORK" -f "$FIXTURE/docker-compose.yml" -f "$WORK/harness.yml" "$@"; }
compose up -d --no-deps allstarr
for attempt in $(seq 1 90); do
  if curl --fail --silent http://127.0.0.1:5274/health > "$WORK/health.json"; then break; fi
  sleep 2
done
curl --fail --silent http://127.0.0.1:5274/health > "$WORK/health.json"
curl --fail --silent http://127.0.0.1:5274/System/Info/Public | python3 -c 'import json,sys; assert json.load(sys.stdin)["ServerName"] == "native-fixture"'
curl --fail --silent http://127.0.0.1:5275/ > "$WORK/admin.html"
test -s "$WORK/admin.html"
test -s "$WORK/cache/allstarr-v3/allstarr.db"
test -s "$WORK/cache/allstarr-v3/keyring.json"
sudo python3 - "$WORK" <<'PY'
import pathlib, sqlite3, sys
root = pathlib.Path(sys.argv[1])
db = sqlite3.connect(root / 'cache/allstarr-v3/allstarr.db')
assert db.execute('select count(*) from onboarding_states').fetchone()[0] == 0
assert db.execute('select count(*) from legacy_env_imports').fetchone()[0] == 0
PY
compose exec -T allstarr sha256sum /app/cache/allstarr-v3/keyring.json > "$WORK/previous-keyring.sha256"
compose restart allstarr
for attempt in $(seq 1 90); do
  if curl --fail --silent http://127.0.0.1:5274/health >/dev/null; then break; fi
  sleep 2
done
curl --fail --silent http://127.0.0.1:5274/System/Info/Public | python3 -c 'import json,sys; assert json.load(sys.stdin)["ServerName"] == "native-fixture"'
cmp "$WORK/.env" "$WORK/input.env"
compose exec -T allstarr sha256sum /app/cache/allstarr-v3/keyring.json > "$WORK/current-keyring.sha256"
cmp "$WORK/current-keyring.sha256" "$WORK/previous-keyring.sha256"
test "$(cat "$WORK/kept/unidentified.flac")" = 'legacy kept bytes'
test "$(cat "$WORK/downloads/unidentified.flac")" = 'legacy download bytes'
test "$(cat "$WORK/cache/legacy-entry")" = 'legacy cache bytes'
sha256sum --check "$WORK/fixture-sha256"
echo 'Unchanged v2 layout: health, native relay, pending setup, persistent database/key ring and restart passed.'
