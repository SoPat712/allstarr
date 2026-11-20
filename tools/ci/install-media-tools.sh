#!/usr/bin/env bash
set -euo pipefail

sudo python3 - /etc/apt <<'PY'
from pathlib import Path
import re
import sys

root = Path(sys.argv[1])
directory = root / "sources.list.d"
for path in [root / "sources.list", root / "apt-mirrors.txt", *directory.glob("*.list"), *directory.glob("*.sources")]:
    if not path.is_file():
        continue
    original = path.read_text()
    updated = re.sub(r"https?://azure\.archive\.ubuntu\.com/ubuntu", "https://archive.ubuntu.com/ubuntu", original)
    if updated != original:
        path.write_text(updated)
PY
# apt on a hosted runner now and then stalls without failing, and the step ran out its eight minutes. A try that
# does not finish in two minutes is stopped and made again.
apt_get() {
  local try
  for try in 1 2 3; do
    if sudo timeout --kill-after=10 120 apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 "$@"; then
      return 0
    fi
    echo "apt-get $1 did not finish (try $try of 3)" >&2
    sudo dpkg --configure -a || true
    sleep 5
  done
  return 1
}
apt_get update
apt_get install -y --no-install-recommends ffmpeg
ffmpeg -version
ffprobe -version
