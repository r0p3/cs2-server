#!/usr/bin/env bash
# The upstream entrypoint sources this file after SteamCMD and before CS2.

# Upstream uses eval for launching CS2. Restrict values used in that command.
repserver_validate_env() {
    local invalid=0

    if [[ ! ${SRCDS_TOKEN:-} =~ ^[A-Fa-f0-9]{32}$ ]]; then
        echo "RepServer: SRCDS_TOKEN måste bestå av exakt 32 hextecken." >&2
        invalid=1
    fi
    if [[ ! ${CS2_PW:-} =~ ^[a-zA-Z0-9]{8,}$ ]]; then
        echo "RepServer: CS2_PW måste ha minst 8 tecken, endast A-Z, a-z och 0-9." >&2
        invalid=1
    fi
    if [[ ! ${CS2_RCONPW:-} =~ ^[a-zA-Z0-9]{16,}$ ]]; then
        echo "RepServer: CS2_RCONPW måste ha minst 16 tecken, endast A-Z, a-z och 0-9." >&2
        invalid=1
    fi
    if [[ ! ${CS2_STARTMAP:-} =~ ^[a-z0-9_]+$ ]]; then
        echo "RepServer: CS2_STARTMAP får endast innehålla a-z, 0-9 och understreck." >&2
        invalid=1
    fi
    if [[ ! ${CS2_MAXPLAYERS:-} =~ ^[1-9][0-9]?$ ]] || (( CS2_MAXPLAYERS < 2 || CS2_MAXPLAYERS > 64 )); then
        echo "RepServer: CS2_MAXPLAYERS måste vara ett heltal mellan 2 och 64." >&2
        invalid=1
    fi

    return "$invalid"
}

repserver_validate_env || exit 1
unset -f repserver_validate_env

repserver_install() {
    local game_dir="${STEAMAPPDIR}/game/csgo"
    local gameinfo="$game_dir/gameinfo.gi"
    local temporary

    [[ -w /var/lib/repserver ]] || return 1
    [[ -r "$gameinfo" && -r /opt/repserver/maps.txt ]] || return 1
    cp -R /opt/repserver/addons "$game_dir/" || return 1

    cp /opt/repserver/botprofile.vpk "$game_dir/repserver-botprofile.vpk" || return 1

    temporary=$(mktemp "$game_dir/.gameinfo.XXXXXX") || return 1
    if ! awk '
        $1 == "Game" && ($2 == "csgo/addons/metamod" || $2 == "csgo/repserver-botprofile.vpk") { next }
        { print }
        !inserted && $1 == "Game_LowViolence" && $2 == "csgo_lv" {
            print "            Game csgo/repserver-botprofile.vpk"
            print "            Game csgo/addons/metamod"
            inserted = 1
        }
        END { if (!inserted) exit 1 }
    ' "$gameinfo" > "$temporary" || ! chmod --reference="$gameinfo" "$temporary" ||
        ! mv -f "$temporary" "$gameinfo"; then
        rm -f "$temporary"
        return 1
    fi
}

if ! repserver_install; then
    echo "RepServer: kunde inte installera pluginet. Kontrollera gameinfo.gi och rättigheter för data/." >&2
    exit 1
fi
unset -f repserver_install

repserver_configure_mode() {
    local cfg_dir="${STEAMAPPDIR}/game/csgo/cfg"
    local mode_file=/var/lib/repserver/mode
    local competitive=bot_game deathmatch=bot_dm
    REPSERVER_ACTIVE_MODE=bot-game

    if [[ -f "$mode_file" ]]; then
        REPSERVER_ACTIVE_MODE=$(<"$mode_file") || return 1
    fi

    case "$REPSERVER_ACTIVE_MODE" in
        bot-game|bot-game-t)
            CS2_GAMEALIAS=competitive
            [[ "$REPSERVER_ACTIVE_MODE" != bot-game-t ]] || competitive=bot_game_t
            ;;
        bot-dm|bot-dm-pistol)
            CS2_GAMEALIAS=deathmatch
            [[ "$REPSERVER_ACTIVE_MODE" != bot-dm-pistol ]] || deathmatch=bot_dm_pistol
            ;;
        *)
            echo "RepServer: okänt läge i data/state/mode. Startar bot-game." >&2
            REPSERVER_ACTIVE_MODE=bot-game
            CS2_GAMEALIAS=competitive
            ;;
    esac

    mkdir -p "$cfg_dir/repserver" || return 1
    local profile
    for profile in bot_game bot_game_t bot_dm bot_dm_pistol; do
        if [[ ! -r "/opt/repserver/modes/$profile.cfg" ]]; then
            echo "RepServer: imagen saknar $profile.cfg. Bygg om med ./scripts/update.sh." >&2
            return 1
        fi
        cp "/opt/repserver/modes/$profile.cfg" "$cfg_dir/repserver/" || return 1
    done
    repserver_mode_include "$cfg_dir/gamemode_competitive_server.cfg" "$competitive" || return 1
    repserver_mode_include "$cfg_dir/gamemode_deathmatch_server.cfg" "$deathmatch" || return 1
    export REPSERVER_ACTIVE_MODE CS2_GAMEALIAS
    echo "RepServer: startar $REPSERVER_ACTIVE_MODE ($CS2_GAMEALIAS)."
}

# Preserve existing local settings, then apply the selected profile last.
repserver_mode_include() {
    local target="$1" profile="$2" temporary
    temporary=$(mktemp -- "${target}.XXXXXX") || return 1
    if ! {
        { [[ ! -f "$target" ]] || awk '!/^exec repserver\/[^[:space:]]+\.cfg \/\/ RepServer managed\r?$/' "$target"; } &&
            printf 'exec repserver/%s.cfg // RepServer managed\n' "$profile"
    } > "$temporary" || ! mv -f -- "$temporary" "$target"; then
        rm -f -- "$temporary"
        return 1
    fi
}

if ! repserver_configure_mode; then
    echo "RepServer: kunde inte installera lägesprofilerna." >&2
    exit 1
fi
unset -f repserver_configure_mode repserver_mode_include

repserver_restore_map() {
    local map_file=/var/lib/repserver/last-map
    local last_map

    if [[ -f "$map_file" ]]; then
        if last_map=$(<"$map_file") && [[ "$last_map" =~ ^[a-z0-9_]{1,64}$ ]]; then
            CS2_STARTMAP="$last_map"
        else
            echo "RepServer: kunde inte läsa en giltig karta från data/state/last-map. Använder startkartan." >&2
        fi
    fi

    export CS2_STARTMAP
    echo "RepServer: laddar kartan $CS2_STARTMAP."
}

repserver_restore_map
unset -f repserver_restore_map
