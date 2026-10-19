#!/bin/sh
set -eu
export DISPLAY=:99
Xvfb "$DISPLAY" -screen 0 1280x900x24 -nolisten tcp -ac >/dev/null 2>&1 &
display_pid=$!
trap 'kill "$display_pid" 2>/dev/null || true' EXIT
attempt=0
while [ ! -S /tmp/.X11-unix/X99 ]; do
  kill -0 "$display_pid" 2>/dev/null || exit 1
  attempt=$((attempt + 1))
  [ "$attempt" -lt 50 ] || exit 1
  sleep 0.1
done
if [ "$#" -eq 0 ]; then set -- python -m session_verifier.server; fi
"$@" &
server_pid=$!
trap 'kill "$server_pid" "$display_pid" 2>/dev/null || true' EXIT TERM INT
wait "$server_pid"
