# Lägesprofiler

| Val i chatten | Konfiguration | Regler |
| --- | --- | --- |
| `!mode bot-game` eller `!mode bot-game ct` | `bot_game.cfg` | Competitive med bots på T |
| `!mode bot-game t` | `bot_game_t.cfg` | Competitive med bots på CT |
| `!mode bot-dm` | `bot_dm.cfg` | FFA Deathmatch |
| `!mode bot-dm-pistol` | `bot_dm_pistol.cfg` | FFA Deathmatch med pistoler och kniv |

De två befintliga matchfilerna behåller sina matchregler. Botkvoten är anpassad till serverns 10 platser. `bot_quota_mode fill` sköter tillägget av bots.

Docker bygger in filerna i imagen. Uppstartsskriptet kopierar dem till spelets `cfg/repserver/` och lägger till en `exec` i respektive spelläges serverkonfiguration. Lägg egna ändringar i dessa profiler och kör `./scripts/update.sh` efter uppdatering.
