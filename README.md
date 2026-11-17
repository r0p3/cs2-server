# Repserver

Privat CS2-server med Metamod:Source, CounterStrikeSharp och ett eget plugin. [PLAN.md](PLAN.md) beskriver kommande spellägen, röstning och Workshop-stöd.

## Första starten

Du behöver Docker med Compose v2, en Linux-server för x86-64 och minst 60 GB ledigt diskutrymme. Serverprocessen kör som uid/gid 1000 i containern.

```bash
install -m 600 .env.example .env
mkdir -p data/server data/state
```

Fyll i `.env` med en ny Steam-server-token och nya lösenord. Värdena som tidigare låg i Compose-filen bör bytas. Använd minst 8 bokstäver/siffror för spellösenordet och minst 16 för RCON. Serverimagen bygger sitt startkommando med skalexpansion, så lösenord och startkarta använder en begränsad teckenuppsättning. Om du ändrar servernamnet, använd bokstäver, siffror, mellanslag och bindestreck.

Om värdens användare har ett annat uid än 1000, justera ägarskapet för katalogerna så att containern kan skriva där. Vid nyinstallation räcker `sudo chown 1000:1000 data/server data/state`. Sätt administratörernas SteamID64 i `data/state/admins.txt`, ett ID per rad, och se till att filen är läsbar för uid 1000. Pluginet läser listan vid varje adminåtgärd.

Har du en befintlig installation, följ migreringen nedan innan du startar den nya containern. För en ny server kör du:

```bash
./scripts/update.sh
docker compose logs -f cs2
```

Verifiera att RepServer är laddad i loggen och kör `meta list` i serverkonsolen. Anslut som spelare, kontrollera att kommandolistan visas i chatten och kör `!status`. Första SteamCMD-hämtningen kan ta en stund. UDP exponeras för spelarna, medan TCP för RCON är bunden till localhost.

Om du testar bredvid den gamla servern, sätt `CS2_PUBLIC_PORT=27016` i testinstallationens `.env` och anslut på port 27016. Använd olika Compose-projektnamn om båda mapparna heter `cs2-server`. Sätt då till exempel `COMPOSE_PROJECT_NAME=cs2-server-test` i testinstallationens `.env` så att vanliga Compose-kommandon i den mappen riktas till testprojektet.

## Flytta befintlig serverdata

1. Ta en kopia av gamla Compose-inställningar och katalogen `/home/r0p3/data/gameservers/cs2-data`. Inventera egna cfg-filer, plugins och eventuella gamla startskript.
2. Stoppa den befintliga servern på värden. Kontrollera att `data/server` är tom och kör `rsync -a /home/r0p3/data/gameservers/cs2-data/ data/server/` som en användare med rätt att läsa källan och bevara ägarskapet.
3. Kontrollera kopian och skrivbehörigheten för uid 1000. Imagen innehåller RepServers eget `pre.sh`. Flytta över eventuella anpassningar från det gamla skriptet som du behöver behålla. Upstream skapar om `server.cfg` vid uppstart.
4. Kör `./scripts/update.sh`. Compose väljer containernamn utifrån projektnamnet. Om båda installationerna ska köras samtidigt måste de också använda olika värdportar.
5. Kontrollera anslutning, kartor och plugin. Behåll källkatalogen och backupen tills servern har provkörts.

## Kommandon

Vid anslutning visas alla kommandon i kortform. `!help` visar den fullständiga listan med syntax, beskrivning och adminmarkering.

| Kommando | Vem | Beteende |
| --- | --- | --- |
| `!help`, `!maps`, `!modes`, `!status` | Alla | Visa kommandon, tillåtna kartor, spellägen eller serverstatus |
| `!map de_mirage` | Admin | Byt till en karta i `config/maps.txt` |
| `!change-mode bot-dm` eller `!mode bot-dm` | Admin | Spara läget och starta om servern |
| `!mode bot-game t` | Admin | Spela som T mot CT-bots |
| `!live` | Admin | Avsluta uppvärmningen i bot-game |
| `!restart` eller `!omstart` | Admin | Starta om matchen |
| `!pause`, `!unpause` | Admin | Begär matchpaus eller återuppta matchen |
| `!kick_bot` | Admin | Ta bort alla bots och stäng av automatisk botpåfyllning på aktuell karta |
| `!add_bot_t`, `!add_bot_ct` | Admin | Lägg till en bot på T respektive CT om det finns plats |
| `!set_bot_t <antal>`, `!set_bot_ct <antal>` | Admin | Fyll på valt lag med bots upp till antalet du anger, så långt platserna räcker |

Motsvarande konsolkommandon har prefixet `css_`, till exempel `css_status`. Serverkonsolen har behörighet till alla kommandon.

Om automatisk botpåfyllning redan har fyllt servern, börja med `!kick_bot`. Kör sedan exempelvis `!set_bot_t 8` för att fylla på T, eller lägg till en bot i taget med `!add_bot_t`. Spelare och bots delar serverns 10 platser. `!set_bot_t` och `!set_bot_ct` kan öka antalet utan att ta bort befintliga bots. Lägesprofilens botregler laddas igen vid kartbyte.

## Spellägen

`bot-game` använder era matchregler och placerar bots på T. Välj `!mode bot-game t` för att spela på T-sidan mot CT-bots. Matchen börjar med er långa uppvärmning. Kör `!live` när det är dags att spela match.

`bot-dm` kör FFA Deathmatch med snabb respawn. `bot-dm-pistol` använder samma grund med pistol och kniv. Profilerna ligger i [config/modes/](config/modes/README.md).

Vid första start används `bot-game`. Ett aktivt lägesval sparas i `data/state/mode`, så att en serveromstart fortsätter i det valda läget. Vid lägesbyte startar Docker om servern och spelarna ansluter igen. Spelarna ser aktuellt spelläge och karta i chatten vid anslutning. För att återställa grundläget manuellt, skriv `bot-game` i `data/state/mode` och kör `docker compose restart cs2`.

När en vanlig karta laddas sparas den i `data/state/last-map`. Den laddas igen efter omstart, även vid lägesbyte. `CS2_STARTMAP` används när kartfilen saknas eller innehåller ett ogiltigt kartnamn. Ta bort `data/state/last-map` medan servern är stoppad om du vill använda startkartan från `.env` igen.

## Uppdatera

```bash
git pull --ff-only
./scripts/update.sh
```

Skriptet validerar Compose, bygger imagen före omstarten och återskapar containern. Pluginet, uppstartsskriptet och lägesprofilerna byggs in tillsammans. Efter en uppdatering av projektfilerna behöver du bygga om imagen. Data och lokalt tillstånd ligger kvar i `data/`. SteamCMD uppdaterar spelet under uppstart. Följ loggen och kontrollera pluginet efter uppdatering. Gör en backup av `data/state` och `.env` innan du uppdaterar en server du behöver kunna återställa.

CounterStrikeSharp och Metamod har fasta releaseversioner i Dockerfile. Basimagen använder en flytande tagg tills en image har provkörts. Sätt `CS2_BASE_IMAGE` till den verifierade digesten i `.env` när du har den. SteamCMD hanterar spelbuilden separat.

Röstning, Workshop och automatiserad backup återstår enligt [planen](PLAN.md).
