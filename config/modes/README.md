# Lägesprofiler

| Val i chatten | Konfiguration | Regler |
| --- | --- | --- |
| `!mode bot-game` eller `!mode bot-game ct` | `bot_game.cfg` | Competitive med bots på T |
| `!mode bot-game t` | `bot_game_t.cfg` | Competitive med bots på CT |
| `!mode bot-dm` | `bot_dm.cfg` | FFA Deathmatch |
| `!mode bot-dm-pistol` | `bot_dm_pistol.cfg` | FFA Deathmatch med pistoler och kniv |

RepServer fyller från den egna botlistan upp till 10 deltagare tidigast fem sekunder efter att första spelaren anslutit helt. `CS2_MAXPLAYERS` i `.env` styr hur många platser servern har för spelare och bots, inklusive manuellt tillagda bots.

I båda matchprofilerna spelar bottarna på Expert (`bot_difficulty 3`) med fast svårighetsgrad. De får använda granater och fortsätter försöka spela bombmålet även om de har mänskliga lagkamrater. Köpgränsen är 3 700. [CS2:s konsolvariabler](https://cs2.poggu.me/dumped-data/convar-list/)

Docker bygger in filerna i imagen. Uppstartsskriptet kopierar dem till spelets `cfg/repserver/` och lägger till en `exec` i respektive spelläges serverkonfiguration. Lägg egna ändringar i dessa profiler och kör `./scripts/update.sh` efter uppdatering.
