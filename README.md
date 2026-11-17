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

Verifiera `[RepServer] v0.1.0 laddad` i loggen och `meta list` i serverkonsolen. Anslut som spelare och kör `!status`. Första SteamCMD-hämtningen kan ta en stund. UDP exponeras för spelarna, medan TCP för RCON är bunden till localhost.

Om du testar bredvid den gamla servern, sätt `CS2_PUBLIC_PORT=27016` i testinstallationens `.env` och anslut på port 27016. Använd olika Compose-projektnamn om båda mapparna heter `cs2-server`. Sätt då till exempel `COMPOSE_PROJECT_NAME=cs2-server-test` i testinstallationens `.env` så att vanliga Compose-kommandon i den mappen riktas till testprojektet.

## Flytta befintlig serverdata

1. Ta en kopia av gamla Compose-inställningar och katalogen `/home/r0p3/data/gameservers/cs2-data`. Inventera egna cfg-filer, plugins och eventuella gamla startskript.
2. Stoppa den befintliga servern på värden. Kontrollera att `data/server` är tom och kör `rsync -a /home/r0p3/data/gameservers/cs2-data/ data/server/` som en användare med rätt att läsa källan och bevara ägarskapet.
3. Kontrollera kopian och skrivbehörigheten för uid 1000. Den nya Compose-konfigurationen monterar sitt eget `pre.sh`. Flytta över eventuella anpassningar från det gamla skriptet som du behöver behålla. Upstream skapar om `server.cfg` vid uppstart.
4. Kör `./scripts/update.sh`. Compose väljer containernamn utifrån projektnamnet. Om båda installationerna ska köras samtidigt måste de också använda olika värdportar.
5. Kontrollera anslutning, kartor och plugin. Behåll källkatalogen och backupen tills servern har provkörts.

## Kommandon

| Kommando | Vem | Beteende |
| --- | --- | --- |
| `!help`, `!maps`, `!status` | Alla | Visa kommandon, tillåtna kartor eller aktuell karta och pluginversion |
| `!map de_mirage` | Admin | Byt till en karta i `config/maps.txt` |
| `!restart` eller `!omstart` | Admin | Starta om matchen |
| `!pause`, `!unpause` | Admin | Begär matchpaus eller återuppta matchen |

Motsvarande konsolkommandon har prefixet `css_`, till exempel `css_status`. Serverkonsolen har behörighet till alla kommandon.

## Befintliga lägeskonfigurationer

Lägg era nuvarande `.cfg`-filer i [config/modes/](config/modes/README.md) så att vi kan återanvända dem för botlägena.

## Uppdatera

```bash
git pull --ff-only
./scripts/update.sh
```

Skriptet validerar Compose, bygger imagen före omstarten och återskapar containern. Data och lokalt tillstånd ligger kvar i `data/`. SteamCMD uppdaterar spelet under uppstart. Följ loggen och kontrollera pluginet efter uppdatering. Gör en backup av `data/state` och `.env` innan du uppdaterar en server du behöver kunna återställa.

CounterStrikeSharp och Metamod har fasta releaseversioner i Dockerfile. Basimagen använder en flytande tagg tills en image har provkörts. Sätt `CS2_BASE_IMAGE` till den verifierade digesten i `.env` när du har den. SteamCMD hanterar spelbuilden separat.

Spellägesbyte, pistolregler, röstning, Workshop och automatiserad backup återstår enligt [planen](PLAN.md).
