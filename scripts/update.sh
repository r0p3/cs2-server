#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

if [[ ! -d data/server || ! -d data/state ]]; then
    echo "Skapa datakatalogerna eller kopiera befintlig serverdata enligt README först." >&2
    exit 1
fi

exec 9>data/.update.lock
flock -n 9 || { echo "En serveruppdatering pågår redan." >&2; exit 1; }

docker compose config --quiet
docker compose build cs2
docker compose up -d --no-build --force-recreate cs2
echo "Containern har startats. Följ SteamCMD och pluginets start med docker compose logs -f cs2."
