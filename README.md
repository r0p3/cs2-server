# RepServer

A private CS2 server with Metamod:Source, CounterStrikeSharp, and the RepServer plugin. [PLAN.md](PLAN.md) records the original design and future work.

## First start

You need Docker with Compose v2, an x86-64 Linux host, and at least 60 GB of free disk space. The server process runs as UID/GID 1000 inside the container.

```bash
install -m 600 .env.example .env
mkdir -p data/server data/state
```

Fill in `.env` with a new Steam game server token, a game password of at least 8 letters or digits, and an RCON password of at least 16 letters or digits. The base image uses shell expansion to start the server. Use letters, digits, spaces, and hyphens in the server name.

Make sure the container can write to both data directories. On a fresh installation, run `sudo chown 1000:1000 data/server data/state` if they have a different owner. To grant in-game admin access, add one SteamID64 per line to `data/state/admins.txt` and make the file readable by UID 1000. The plugin reloads that list for each admin action.

For a fresh installation, start the server with:

```bash
./scripts/update.sh
docker compose logs -f cs2
```

Check the startup log and run `meta list` in the server console. Join the server, look for the command list in chat, and try `!status`. The first SteamCMD download can take a while. Compose exposes UDP to players and binds RCON TCP to localhost.

For a side-by-side test, use a different port, such as `CS2_PUBLIC_PORT=27016`. If the two checkouts have the same directory name, also set a unique `COMPOSE_PROJECT_NAME` in the test installation's `.env`.

## Remote admin panel

Copy [scripts/cs2-admin.py](scripts/cs2-admin.py) to your admin computer. You need Python 3.9+ with Tkinter and an SSH client. On Debian or Ubuntu, Tkinter is available as `python3-tk`.

1. Update the server with `./scripts/update.sh` so it runs the English plugin and reports Workshop results.
2. On your admin computer, open a terminal and start an SSH tunnel. Replace `user@server` with your SSH login and host. The last port must match `CS2_PUBLIC_PORT` on the server.

   ```sh
   ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:27016:127.0.0.1:27015 user@server
   ```

3. Leave the tunnel running and launch the panel in another terminal.

   ```sh
   python3 cs2-admin.py
   ```

   On Windows, you can use `py cs2-admin.py`. Enter local port `27016` and the value of `CS2_RCONPW`, then select **Check connection**.

The panel can change maps, add Workshop maps by URL or ID, pause and resume the match, end warmup, restart a match, change modes, manage bots, list or kick players, and send announcements. The **Console** tab runs other CS2 server commands and shows their output. Use SSH on the host for Docker operations such as starting a stopped container or rebuilding the image.

## Migrate existing server data

1. Back up the old Compose settings and the existing server directory, previously `/home/r0p3/data/gameservers/cs2-data`. Record custom cfg files, plugins, and startup scripts.
2. Stop the old server. Check that `data/server` is empty, then run `rsync -a /home/r0p3/data/gameservers/cs2-data/ data/server/` with permissions to read the source and preserve ownership.
3. Verify the copy and check that UID 1000 has write access. The image supplies its own `pre.sh`. Port any custom behavior you still need. The base image regenerates `server.cfg` at startup.
4. Run `./scripts/update.sh`. Compose derives the container name from the project name. Assign different host ports if both installations will run at once.
5. Verify connections, maps, and the plugin. Keep the source directory and backup until the new server has been tested.

## In-game commands

Type `!` by itself or use `!menu` after spawning. A panel appears near the top left and holds your player still while it is open. Move with **W/S**, select with **E**, and go back or close with **R**. The panel uses a separate game entity so the warmup timer can stay visible.

Everyone can see server status. Admins also have menus for maps, game modes, match controls, and bots. The **Bots** menu lets you choose a team and set the target count before starting a fill. Some map options are also available to all players.

The server shows a command summary when you join. Run `!help` for syntax, descriptions, and admin requirements.

| Command | Access | Behavior |
| --- | --- | --- |
| `!` or `!menu` | Everyone | Open the in-game menu |
| `!help`, `!maps`, `!modes`, `!status` | Everyone | Show commands, maps, game modes, or server status |
| `!map <name>` | Everyone for Workshop maps, admins for standard maps | Change to a map by name |
| `!add_map <workshop_url> [name]` | Everyone | Save a Workshop map to the catalog |
| `!change-mode bot-dm` or `!mode bot-dm` | Admin | Save the mode and restart the server |
| `!mode bot-game t` | Admin | Play on T against CT bots |
| `!live` | Admin | End warmup in competitive modes |
| `!16_0` | Admin | Start or stop the CT 16–0 challenge on the current map |
| `!ready`, `!unready`, `!readystatus` | Everyone | Set or clear your ready marker or show readiness in chat |
| `!restart` | Admin | Restart the match |
| `!pause`, `!unpause` | Admin | Request a match pause or resume the match |
| `!kick_bot` | Admin | Remove all bots and disable automatic fill on the current map |
| `!bot_add_t [count]`, `!bot_add_ct [count]` | Admin | Add bots up to capacity. Defaults to one. `!add_bot_t` and `!add_bot_ct` also work. |
| `!set_bot_t <count>`, `!set_bot_ct <count>` | Admin | Fill the chosen team up to the requested count as space allows |
| `!bot_roster <count>` | Everyone | Preview names and roles for a given bot count |

Console equivalents use the `css_` prefix, such as `css_status`. The server console has admin access.

Bind commands in the CS2 client console, for example `bind "F8" "say !restart"` or `bind "F9" "say !ready"`. Use the same command spelling as in chat. Admin commands still require in-game admin access.

Edit [config/bot-names.txt](config/bot-names.txt) to change the built-in roster. Use one `Name | rifle` or `Name | sniper` per line, with names up to 31 UTF-8 bytes. Run `./scripts/update.sh` to build the roster into the server's bot profile. You can inspect the names and roles under **Bots → Bot names**. Snipers prefer the AWP and SSG 08. Rifle bots keep their normal weapon preferences and drop sniper rifles they pick up. Pistol DM uses its own weapon rules.

Set priority in [config/bot-priority.txt](config/bot-priority.txt) by listing existing names, one per line. A fill aims for one sniper per five bots across all active bots while prioritizing configured names within their role. If a player already uses a bot's name, the plugin spawns the bot with an alternate name and then applies its configured display name. If CS2 fails to add a bot to the requested team, the plugin tries other spawn names for that role. Each failed spawn name is skipped for the rest of that fill.

RepServer adds 10 roster bots after a human joins T or CT and five seconds have passed since the last connection. In `bot-game`, the bots join T. Bot commands wait while a player connects. Use `!kick_bot` to cancel and clear the roster or `!set_bot_t 20` to raise the target on T. The default 25 slots accommodate four players, 20 bots, and one reserved slot where the map and engine allow it.

The fill commands only add bots. A fill started by a player stops if that player disconnects. Switching maps reapplies the mode's bot rules.

## Game modes

`bot-game` uses competitive rules and places bots on T. Use `!mode bot-game t` to play on T against CT bots. In either competitive profile, both players and bots receive a defuse kit when they spawn. CS2's bomb rules allow CT to defuse.

During warmup, type `!ready` or choose **Match readiness → Mark me as ready**. Hold **Tab** to see `[READY]` before the names of ready players. Use `!unready` to clear your marker or `!readystatus` to see the list in chat. Warmup ends and the markers clear once all human players, including spectators, are ready. An admin can end warmup manually with `!live`.

`bot-dm` runs free-for-all Deathmatch with fast respawns. `bot-dm-pistol` uses the same rules with pistols and knives. See [the mode profiles](config/modes/README.md).

Use `!16_0` as an admin during a competitive game to start the CT 16–0 challenge on the current map. The command starts a fresh match. If T wins a round or the round is drawn, the match restarts from 0–0. A 16–0 CT win completes the challenge. Use `!16_0` again to turn it off. The challenge also ends on map change.

The first startup uses `bot-game`. The selected mode is saved in `data/state/mode`. Changing modes restarts CS2 under Docker, and players must reconnect. To reset the default manually, write `bot-game` to `data/state/mode` and run `docker compose restart cs2`.

Loading a standard map listed in `config/maps.txt` saves it to `data/state/last-map`. The server restores that map after a restart, including after a session on a Workshop map. If the saved map is absent from the list, startup uses `CS2_STARTMAP`. To reset the saved map, remove `data/state/last-map` while the server is stopped.

## Workshop maps

Paste the map's Steam Workshop URL into the in-game command. RepServer normally derives the alias from the Workshop title. For example, **Black Gold** becomes `black_gold`. Add an optional alias after the URL to choose it yourself.

```text
!add_map <steam_workshop_url>
!add_map <steam_workshop_url> black_gold
!map black_gold
```

Once saved, the map is available under **! → Change map** and in the desktop panel. CS2 downloads it when someone chooses to play it. The catalog lives in `data/state/workshop-maps.json` and survives restarts and rebuilds. [Steamworks Workshop API](https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage#GetPublishedFileDetails)

## Updates

```bash
git pull --ff-only
./scripts/update.sh
```

The script validates Compose, builds the image, and recreates the container with the new plugin, startup script, and game mode profiles. Data and state remain in `data/`. SteamCMD updates CS2 during startup. Watch the logs and verify the plugin loaded. Back up `data/state` and `.env` before updating a server you need to restore.

CounterStrikeSharp and Metamod versions are pinned in the Dockerfile. The base image uses a floating tag by default. Set `CS2_BASE_IMAGE` to a tested image digest in `.env` to pin that image.
