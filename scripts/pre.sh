#!/usr/bin/env bash
# The upstream entrypoint sources this file after SteamCMD and before CS2.

# Upstream uses eval for launching CS2. Restrict values used in that command.
repserver_validate_env() {
    local invalid=0

    if [[ ! ${SRCDS_TOKEN:-} =~ ^[A-Fa-f0-9]{32}$ ]]; then
        echo "RepServer: SRCDS_TOKEN must contain exactly 32 hexadecimal characters." >&2
        invalid=1
    fi
    if [[ ! ${CS2_PW:-} =~ ^[a-zA-Z0-9]{8,}$ ]]; then
        echo "RepServer: CS2_PW must contain at least 8 characters using A-Z, a-z and 0-9." >&2
        invalid=1
    fi
    if [[ ! ${CS2_RCONPW:-} =~ ^[a-zA-Z0-9]{16,}$ ]]; then
        echo "RepServer: CS2_RCONPW must contain at least 16 characters using A-Z, a-z and 0-9." >&2
        invalid=1
    fi
    if [[ ! ${CS2_STARTMAP:-} =~ ^[a-z0-9_]+$ ]]; then
        echo "RepServer: CS2_STARTMAP may contain only a-z, 0-9 and underscores." >&2
        invalid=1
    fi
    if [[ ! ${CS2_MAXPLAYERS:-} =~ ^[1-9][0-9]?$ ]] || (( CS2_MAXPLAYERS < 2 || CS2_MAXPLAYERS > 64 )); then
        echo "RepServer: CS2_MAXPLAYERS must be a whole number between 2 and 64." >&2
        invalid=1
    fi
    if [[ -n ${CS2_ROUND_SOUNDS_WORKSHOP_ID:-} && ! $CS2_ROUND_SOUNDS_WORKSHOP_ID =~ ^[1-9][0-9]{0,18}$ ]]; then
        echo "RepServer: CS2_ROUND_SOUNDS_WORKSHOP_ID must be a numeric Workshop item ID." >&2
        invalid=1
    fi

    return "$invalid"
}

repserver_validate_env || exit 1
unset -f repserver_validate_env

# The base image passes -maxplayers. Also override the game mode's default.
export CS2_ADDITIONAL_ARGS="${CS2_ADDITIONAL_ARGS:+$CS2_ADDITIONAL_ARGS }-maxplayers_override $CS2_MAXPLAYERS"

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
    echo "RepServer: Could not install the plugin. Check gameinfo.gi and permissions on data/." >&2
    exit 1
fi
unset -f repserver_install

repserver_install_round_sounds() {
    local game_dir="${STEAMAPPDIR}/game/csgo"
    local plugin=/opt/repserver/multiaddonmanager
    local loader="$game_dir/addons/metamod/repserver-round-sounds.vdf"
    local workshop_id=${CS2_ROUND_SOUNDS_WORKSHOP_ID:-}

    if [[ -z $workshop_id ]]; then
        rm -f -- "$loader"
        return
    fi

    if [[ ! -f "$game_dir/addons/metamod/multiaddonmanager.vdf" ]]; then
        cp -R "$plugin/addons/multiaddonmanager" "$game_dir/addons/" || return 1
        cp "$plugin/addons/metamod/multiaddonmanager.vdf" "$loader" || return 1
        if [[ ! -f "$game_dir/cfg/multiaddonmanager/multiaddonmanager.cfg" ]]; then
            mkdir -p "$game_dir/cfg/multiaddonmanager" || return 1
            cp "$plugin/cfg/multiaddonmanager/multiaddonmanager.cfg" "$game_dir/cfg/multiaddonmanager/" || return 1
        fi
    else
        rm -f -- "$loader" || return 1
    fi

    export CS2_ADDITIONAL_ARGS="$CS2_ADDITIONAL_ARGS +mm_add_client_addon $workshop_id"
    echo "RepServer: Clients will request round sounds from Workshop item $workshop_id."
}

if ! repserver_install_round_sounds; then
    echo "RepServer: Could not install MultiAddonManager for round sounds." >&2
    exit 1
fi
unset -f repserver_install_round_sounds

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
            echo "RepServer: Unknown mode in data/state/mode. Starting bot-game." >&2
            REPSERVER_ACTIVE_MODE=bot-game
            CS2_GAMEALIAS=competitive
            ;;
    esac

    mkdir -p "$cfg_dir/repserver" || return 1
    local profile
    for profile in bot_game bot_game_t bot_dm bot_dm_pistol; do
        if [[ ! -r "/opt/repserver/modes/$profile.cfg" ]]; then
            echo "RepServer: The image is missing $profile.cfg. Rebuild with ./scripts/update.sh." >&2
            return 1
        fi
        cp "/opt/repserver/modes/$profile.cfg" "$cfg_dir/repserver/" || return 1
    done
    repserver_mode_include "$cfg_dir/gamemode_competitive_server.cfg" "$competitive" || return 1
    repserver_mode_include "$cfg_dir/gamemode_deathmatch_server.cfg" "$deathmatch" || return 1
    export REPSERVER_ACTIVE_MODE CS2_GAMEALIAS
    echo "RepServer: Starting $REPSERVER_ACTIVE_MODE ($CS2_GAMEALIAS)."
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
    echo "RepServer: Could not install the game mode profiles." >&2
    exit 1
fi
unset -f repserver_configure_mode repserver_mode_include

repserver_restore_map() {
    local map_file=/var/lib/repserver/last-map
    local last_map

    if [[ -f "$map_file" ]]; then
        if last_map=$(<"$map_file") && [[ "$last_map" =~ ^[a-z0-9_]{1,64}$ ]] &&
            grep -Eq "^[[:space:]]*${last_map}[[:space:]]*$" /opt/repserver/maps.txt; then
            CS2_STARTMAP="$last_map"
        else
            echo "RepServer: data/state/last-map does not match a standard map in config/maps.txt. Using the startup map." >&2
        fi
    fi

    export CS2_STARTMAP
    echo "RepServer: Loading map $CS2_STARTMAP."
}

repserver_restore_map
unset -f repserver_restore_map
