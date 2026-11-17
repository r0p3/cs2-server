#!/usr/bin/env bash
# The upstream entrypoint sources this file after SteamCMD and before CS2.
repserver_install() {
    local game_dir="${STEAMAPPDIR}/game/csgo"
    local gameinfo="$game_dir/gameinfo.gi"
    local temporary

    [[ -w /var/lib/repserver ]] || return 1
    [[ -r "$gameinfo" && -r /opt/repserver/maps.txt ]] || return 1
    cp -R /opt/repserver/addons "$game_dir/" || return 1

    if ! grep -Eq '^[[:space:]]*Game[[:space:]]+csgo/addons/metamod([[:space:]]|$)' "$gameinfo"; then
        temporary=$(mktemp "$game_dir/.gameinfo.XXXXXX") || return 1
        if ! awk '
            { print }
            !inserted && $1 == "Game_LowViolence" && $2 == "csgo_lv" {
                print "            Game csgo/addons/metamod"
                inserted = 1
            }
            END { if (!inserted) exit 1 }
        ' "$gameinfo" > "$temporary" || ! chmod --reference="$gameinfo" "$temporary" ||
            ! mv -f "$temporary" "$gameinfo"; then
            rm -f "$temporary"
            return 1
        fi
    fi
}

if ! repserver_install; then
    echo "RepServer: kunde inte installera pluginet. Kontrollera gameinfo.gi och rättigheter för data/." >&2
    exit 1
fi
unset -f repserver_install

# Upstream uses eval for launching CS2. Restrict values used in that command.
if [[ ! ${SRCDS_TOKEN:-} =~ ^[A-Fa-f0-9]{32}$ ||
      ! ${CS2_PW:-} =~ ^[a-zA-Z0-9]{8,}$ ||
      ! ${CS2_RCONPW:-} =~ ^[a-zA-Z0-9]{16,}$ ||
      ! ${CS2_STARTMAP:-} =~ ^[a-z0-9_]+$ ]]; then
    echo "RepServer: ogiltigt token, startkarta eller lösenordsformat. Se .env.example och README." >&2
    exit 1
fi
