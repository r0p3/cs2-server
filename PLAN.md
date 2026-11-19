# CS2 server plan

Original design dated 2026-11-17. This is a historical proposal. See [README.md](README.md) for the current setup and commands.

## Goals and starting point

Run a private server where players can choose maps and bot modes from chat under controlled permissions. Keep code, configuration, and version choices in Git. Store persistent server data under the project directory so it survives container recreation.

The original Compose setup used `joedwards32/cs2` and stored server files at `/home/r0p3/data/gameservers/cs2-data`. It contained a pasted editor header, plaintext secrets, obsolete commented configuration, a bot quota of 20 with 10 slots, and no restart policy. Git history and the remote were unavailable for the original review.

## Stack

- Start with the existing base image and pin it to a digest once tested.
- Use Metamod:Source 2.x and CounterStrikeSharp with the bundled .NET runtime.
- Use one small C# plugin, RepServer, for commands, mode changes, voting, and state. Keep map, weapon, and match rules under one owner.
- Pin compatible versions of Metamod, CounterStrikeSharp, the SDK, and plugin dependencies with checksums or lock files. Verify them against the current CS2 build. [CounterStrikeSharp installation guide](https://docs.cssharp.dev/docs/guides/getting-started) · [Metamod](https://www.sourcemm.net/)

## Player commands and voting

The original proposal gave everyone access to status and votes for approved maps and modes. SteamID64 admins could apply changes directly. With one human present, that player's vote could pass immediately. Bots would have no vote.

| Proposed command | Behavior | Access |
| --- | --- | --- |
| `!help`, `!status`, `!maps`, `!modes` | Show help, status, maps, or game modes | Everyone |
| `!map de_mirage` | Load an approved map | Vote or admin |
| `!change-mode bot-dm-pistol`, `!mode bot-dm` | Change mode and reload as needed | Vote or admin |
| `!restart` | Restart the match in the current mode | Vote or admin |
| `!pause`, `!unpause` | Pause or resume at a valid round boundary | Vote or admin |
| `!workshop add <id_or_url>` | Validate and register a Workshop map | Admin |
| `!workshop list` | List maps and installation status | Everyone |
| `!map <workshop_alias>` | Load a registered Workshop map | Vote or admin |
| `!yes`, `!no` | Vote on the active question | Human players |

A match restart is a game command. A container restart belongs to the host. The plugin needs no Docker socket or shell execution.

Start with a simple majority of eligible humans at vote creation, a 20-second voting window, one active vote, and a 60-second cooldown. Define disconnect behavior and keep the vote threshold stable after creation. Announce outcomes and give players a countdown before map or mode changes. Give pauses a configurable time limit.

Validate map names against the catalog and modes against a fixed list. Extract a numeric ID from Workshop URLs before building a server command. Log who initiated an admin change and its result.

## Game modes

| Mode | Proposed rules |
| --- | --- |
| `bot-dm-pistol` | Free-for-all, fast respawn, pistols and knives. Apply the rules to bots too. |
| `bot-dm` | Free-for-all, fast respawn, configurable weapon pool and bots to fill slots. |
| `bot-game` | Team bomb scenario with economy, rounds, no mid-round respawn, and pause support. |

The initial target was 10 slots with Expert bots, filling to capacity as the map allows. Human players could pick a side. Pistol DM would start with the team default pistol and offer a pistol menu. The first version would reject pause requests in DM with a clear reply.

Configure each mode explicitly. Reset economy, respawns, friendly fire, weapon rules, round time, and bot settings between modes. Cover spawns, buys, pickups, bonus weapons, and bots when enforcing pistol restrictions.

Validate and serialize mode changes, announce the change, save the requested mode, apply the game type, reload the map, apply the profile after the engine defaults, and verify the result. Test the order against CS2. If changing game type requires a process restart, announce that players will need to reconnect.

## Workshop

Keep map registration separate from switching maps. Save the ID, alias, title, and status in a persistent local catalog. Validate that the item is an available CS2 map. Handle collections separately.

Test the server's Workshop commands before implementing prefetch. First load may need a download. Show status, set a timeout, and fall back to a known working map on failure. Mark a map ready after a successful load. Test bot navigation on Workshop maps. [Container documentation](https://github.com/joedwards32/CS2#steam-workshop)

## Persistent storage and migration

Mount `./data/server` at `/home/steam/cs2-dedicated` and `data/state` at the plugin's state path. Track the base map catalog, game profiles, and permission examples in Git. Keep secrets, game files, logs, downloads, and runtime data in ignored local paths. Updates must preserve admin IDs, Workshop additions, and the saved mode. Use stable IDs when combining map catalogs.

Check available disk space, make a backup, stop the old service, copy the data with ownership preserved, verify the copy, switch the mounts, and test startup. Keep the original until recovery has been verified. Identify the container UID/GID and grant only the required permissions. Store backups on a separate disk or host and test restoring to a separate directory.

## Startup and updates

Build the plugin and package pinned mods in an image stage outside the server mount. After SteamCMD runs, the startup hook installs the managed files and updates `gameinfo.gi` without adding duplicate Metamod entries. Preserve local data and fail startup if required installation fails.

The proposed update sequence was:

1. Lock against concurrent updates. Validate configuration without printing secrets and check free space.
2. Fetch the pinned base image and build before stopping the running server.
3. Notify players, stop cleanly, back up state and configuration, and retain the previous image and versions.
4. Recreate the service with `docker compose up -d --force-recreate cs2`.
5. Allow enough time for SteamCMD and Workshop, then verify the server and plugin respond and report the result.
6. Keep logs and backups on failure. Provide a documented recovery route and avoid restart loops after incompatible releases.

Compose recreation applies environment changes and keeps the data mounts. SteamCMD still updates the game at startup, so pinning the mods and image does not pin the game build. Rolling back an image or plugin is possible. Steam may no longer provide a compatible older game build. [Docker Compose restart](https://docs.docker.com/reference/cli/docker/compose/restart/) · [Docker Compose up](https://docs.docker.com/reference/cli/docker/compose/up/) · [Upstream image](https://github.com/joedwards32/CS2#how-to-use-this-image)

## Operations and acceptance

- Keep tokens and passwords in the ignored `.env`, rotate exposed values, and document precedence among Compose, game cfg, mode profiles, and saved state.
- Use a restart policy, a graceful stop period, and log rotation. A health check should allow for long startup. Docker's unhealthy status alone does not restart the container.
- Expose only required ports. Limit RCON to a trusted route and open SourceTV only if used.
- Back up local state, admin IDs, secrets, and custom files. Set retention and test recovery. Game files and the Workshop cache can usually be downloaded again.
- Validate Compose with test settings, build the plugin, and test permissions, input validation, voting, and state transitions in CI. Confirm live behavior in CS2.

The proposed delivery order was migration, a live compatibility prototype, mode profiles, player controls, Workshop state, then update and backup automation. Acceptance includes verifying all mode transitions, rejecting unauthorized actions, handling invalid or unavailable Workshop maps, and keeping persistent data across updates. Voting and automated backup remain future work.
