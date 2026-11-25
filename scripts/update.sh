#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

if [[ ! -d data/server || ! -d data/state ]]; then
    echo "Create the data directories or migrate existing server data as described in the README." >&2
    exit 1
fi

exec 9>data/.update.lock
flock -n 9 || { echo "A server update is already running." >&2; exit 1; }

docker compose config --quiet
docker compose build cs2 tracker
docker compose up -d --no-build --force-recreate cs2 tracker
echo "The container has started. Follow SteamCMD and plugin startup with docker compose logs -f cs2."
