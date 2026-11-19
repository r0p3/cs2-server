# Game mode profiles

| Mode command | Config | Rules |
| --- | --- | --- |
| `!mode bot-game` or `!mode bot-game ct` | `bot_game.cfg` | Competitive with bots on T |
| `!mode bot-game t` | `bot_game_t.cfg` | Competitive with bots on CT |
| `!mode bot-dm` | `bot_dm.cfg` | Free-for-all Deathmatch |
| `!mode bot-dm-pistol` | `bot_dm_pistol.cfg` | Free-for-all Deathmatch with pistols and knives |

RepServer adds 10 roster bots at least five seconds after the first player finishes connecting. In the default match profile, they join T. `CS2_MAXPLAYERS` in `.env` sets the combined limit for humans and bots.

Both competitive profiles use Expert difficulty (`bot_difficulty 3`). RepServer bot profiles use `Skill = 100`, `ReactionTime = 0.14`, and separate weapon preferences for rifle and sniper roles. Bots can use grenades and continue playing the bomb objective with human teammates. The buy threshold is 3,700. [CS2 console variables](https://cs2.poggu.me/dumped-data/convar-list/)

Docker packages these files in the image. At startup they are copied into the game's `cfg/repserver/` directory and loaded by an `exec` in each game mode's server config. Edit these profiles and run `./scripts/update.sh` to apply changes.
