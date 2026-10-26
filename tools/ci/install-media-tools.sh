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
sudo apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 update
sudo apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 install -y --no-install-recommends ffmpeg
ffmpeg -version
ffprobe -version
