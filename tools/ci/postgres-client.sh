#!/usr/bin/env bash
set -euo pipefail

image='postgres:18.4-alpine3.23@sha256:996d0920e4ff9df1fc19dacb904492f3c1ec0ec1cc338f0ad7123be7731c5f5e'

if [[ "${1:-}" == install ]]; then
  : "${RUNNER_TEMP:?RUNNER_TEMP is required}"
  : "${GITHUB_PATH:?GITHUB_PATH is required}"
  install_dir="${RUNNER_TEMP}/postgres-18-client"
  mkdir -p "${install_dir}"
  ln -sf "$(realpath "${BASH_SOURCE[0]}")" "${install_dir}/pg_dump"
  ln -sf "$(realpath "${BASH_SOURCE[0]}")" "${install_dir}/pg_restore"
  echo "${install_dir}" >> "${GITHUB_PATH}"
  "${install_dir}/pg_dump" --version
  "${install_dir}/pg_restore" --version
  exit
fi

tool="$(basename "$0")"
[[ "${tool}" == pg_dump || "${tool}" == pg_restore ]] || exit 64
docker run --rm --network host --user "$(id -u):$(id -g)" \
  --volume /tmp:/tmp --volume /etc/passwd:/etc/passwd:ro --volume /etc/group:/etc/group:ro \
  --env PGPASSWORD --env HOME=/tmp \
  "${image}" "${tool}" "$@"
