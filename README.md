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

For a side-by-side test, use different ports, such as `CS2_PUBLIC_PORT=27016` and `TRACKER_PORT=27001`. If the two checkouts have the same directory name, also set a unique `COMPOSE_PROJECT_NAME` in the test installation's `.env`.

## RepServer Tracker

The tracker is part of the Docker Compose stack. Open `http://localhost:27000` on the host or forward port 27000 over SSH. For shared access, point an HTTPS reverse proxy at the tracker and set `REPSERVER_TRACKER_URL` in `.env` to the URL players will use. The tracker listens on loopback by default. Set `TRACKER_PORT` to change the host port and the port in the default `!trackeradmin` link.

- **Live Match** shows the current map, mode, score, lineup, and match stats. **Players** and **Bot Hall of Fame** show lifetime stats gathered after installation. Warmup events are excluded. A 1v5+ clutch requires a human to survive and win as their team's last player after facing at least five living opponents. At 10, 25, 50, 100, 250, and 500 bot kills, then at every multiple of 1,000, chat names the player and the bot that marked the milestone.
- **Map Records** keeps separate records for each pair of Steam accounts, map, side, and challenge. Start with two humans on one team and the full bot roster on the other. Wait for bot fill to finish before going live, or restart after the roster is ready. Each eligible live start adds an attempt, including attempts that are later abandoned. A roster change voids the result. **2v10 Win** is a regular win against ten bots. **2v10 16-0** uses `!mode 16`, `!16t`, or `!16ct`. **2vMax** ranks wins by bot count first. Use `!mode max` for the map’s maximum roster. Regular matches set up manually against eleven or more bots also qualify. Tied bot counts rank by round margin, then by fewer rounds conceded. `!record` shows the record for the current lineup and mode.
- A bot spoils a 16-0 when it makes the decisive elimination, bomb play, or hostage rescue in the first round the human side loses during a 16-0 challenge or an eligible 2v10 attempt. A clock expiry has no individual credit.
- **Autobuy** runs in CS2. Equip what you want and type `!autobuy` while alive. This saves the buyable gear you are carrying for your current side. At the start of later competitive rounds, autobuy purchases missing gear only if you can afford the full remaining cost. T and CT loadouts are saved separately. Use `!autobuy off` to pause purchases for both sides and `!autobuy on` to resume with your saved gear. Saving gear also enables autobuy.
- **Admin** uses `data/state/admins.txt`. Type `!trackeradmin` in CS2 to get a personal web admin link that expires after 12 hours. The admin must be connected to the game to change maps or modes, control the match, start a 16-0 challenge, or manage bots. The tracker serves match stats and records to anyone with access to the dashboard.

Tracker history and loadouts live under `data/state/tracker/` and persist across updates. Back up this directory with the rest of `data/state`.

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
| `!mode win`, `!mode 16`, `!mode max` | Admin | Set up the opposing bot roster for a duo match |
| `!live` | Admin | End warmup in competitive modes |
| `!autobuy [on\|off]` | Everyone | Save your current gear or enable or disable autobuy for both sides |
| `!trackeradmin` | Admin | Get a personal link to the web admin panel |
| `!16ct`, `!16t` | Admin | Set up a 16–0 challenge with ten bots on the opposing team |
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

Use `!mode win` for a regular match against 10 bots, `!mode 16` for a 16–0 challenge against 10 bots, or `!mode max` to fill the opposing side up to the map’s spawn, server slot, and bot roster limits. Your current team is the player side by default. Add `t` or `ct` to choose a side explicitly, as in `!mode max t`. All active players must join the player side. The selected mode stays active across map changes and server restarts. Switching from Deathmatch restarts the server into competitive mode.

During warmup, type `ready` or `!ready`, or choose **Match readiness → Mark me as ready**. Use `unready` or `!unready` to clear your marker. Each change is announced in chat with the ready count, and a small HUD line shows your own status and the ready count. Hold **Tab** to see `[READY]` before the names of ready players, or use `!readystatus` for the list in chat. Warmup ends and the markers clear once all human players, including spectators, are ready. An admin can end warmup manually with `!live`.

`bot-dm` runs free-for-all Deathmatch with fast respawns. `bot-dm-pistol` uses the same rules with pistols and knives. See [the mode profiles](config/modes/README.md).

Use `!16ct` to play CT against ten T bots, or `!16t` to play T against ten CT bots. The command removes bots from the side you play on and fills the other side. It preserves the current match and warmup state. The challenge counts from the next live round. If the opposing team wins a round or the round is drawn, the match restarts from 0–0. A 16–0 win for the selected side completes the challenge. The challenge also ends on map change.

After each live competitive round, chat shows one line per human participant with kills, deaths, assists, headshot percentage, and damage. The number in parentheses after the round's headshot percentage is how much the match percentage changed. With ten opposing bots, five bot kills or 500 damage meet the green thresholds. A defuse also shows how much time was left on the bomb.

The first startup uses `bot-game`. The selected mode is saved in `data/state/mode`. Switching between competitive and Deathmatch restarts CS2 under Docker, and players must reconnect. To reset the default manually, write `bot-game` to `data/state/mode` and run `docker compose restart cs2`.

Loading a standard map listed in `config/maps.txt` saves it to `data/state/last-map`. The server restores that map after a restart, including after a session on a Workshop map. If the saved map is absent from the list, startup uses `CS2_STARTMAP`. To reset the saved map, remove `data/state/last-map` while the server is stopped.

## Round sounds

At the end of a live round, RepServer plays one clip based on the closest any human player came to killing the entire opposing team. Against ten opponents, ten kills play `ace`, nine play `ace-stealer`, and eight play `acestealelele`.

### Automatic download

With MultiAddonManager loaded and a published Workshop ID, run this in the **server console** or the admin panel's **Console** tab. Replace `1234567890` with the number after `id=` in the Workshop URL.

```text
mm_add_client_addon 1234567890
```

Players who are already connected need to reconnect. Steam delivers the addon to each client. If you specifically want a copy on the **server disk**, run `mm_download_addon 1234567890`. Client playback uses the first command. [MultiAddonManager commands](https://github.com/Source2ZE/MultiAddonManager/blob/main/README.md)

To make the client setting persist across restarts, add the real ID to the server checkout's `.env` and run the update script from that checkout.

```dotenv
CS2_ROUND_SOUNDS_WORKSHOP_ID=1234567890
```

```bash
./scripts/update.sh
docker compose logs --tail=100 cs2
```

Look for `Clients will request round sounds from Workshop item` in the logs. In the server console, `meta list` should list MultiAddonManager, and `mm_client_extra_addons` should show the ID. To test the automatic download, reconnect from a client that has only the Workshop copy available, then run `play sounds/repserver/ace.vsnd` in the **player's game console**.

### Publish the sound package once

The [round sounds VPK](assets/workshop/repserver-round-sounds.vpk) is ready to upload. It gets an ID when a Steam account publishes it to the CS2 Workshop. To publish with SteamCMD, create a VDF outside `assets/workshop` with the following contents. Replace the example path with the absolute path to this project's `assets/workshop` folder.

```text
"workshopitem"
{
    "appid" "730"
    "publishedfileid" "0"
    "contentfolder" "/absolute/path/to/cs2-server/assets/workshop"
    "visibility" "3"
    "title" "RepServer Round Sounds"
    "description" "Round-end sounds for RepServer."
}
```

Log in interactively to SteamCMD with the Steam account that will own the item, then run `workshop_build_item "/absolute/path/to/round-sounds.vdf"`. SteamCMD writes the item ID to `publishedfileid` after publishing. Open the item's Workshop page and accept the Workshop agreement if Steam prompts you. Visibility `3` is unlisted and accessible to anyone with the link. Keep the ID in the VDF when uploading later versions. [SteamCMD Workshop publishing](https://partner.steamgames.com/doc/features/workshop/implementation#SteamCmd) [Workshop visibility](https://partner.steamgames.com/doc/api/ISteamRemoteStorage#ERemoteStoragePublishedFileVisibility)

For local testing, extract the [sound pack](assets/repserver-round-sounds.zip) into CS2's `game/csgo` directory. To update the Workshop VPK after replacing a WAV, run `python3 scripts/build-round-sounds.py assets/sounds assets/workshop/repserver-round-sounds.vpk`.

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

The script validates Compose, builds both images, and recreates the game server and tracker containers. Data and state remain in `data/`. SteamCMD updates CS2 during startup. Watch the logs and verify the plugin loaded. Back up `data/state` and `.env` before updating a server you need to restore.

CounterStrikeSharp and Metamod versions are pinned in the Dockerfile. The base image uses a floating tag by default. Set `CS2_BASE_IMAGE` to a tested image digest in `.env` to pin that image.
