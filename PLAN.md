# Plan för CS2-servern

Plan framtagen 2026-11-17. Detta är ett genomförandeförslag; serverkonfigurationen är ännu inte ändrad.

## Mål och utgångspunkt

En privat server där spelare kan välja karta och botläge från chatten, med kontrollerade behörigheter. Kod, konfiguration och versionsval ska gå att uppdatera med Git. Beständig driftdata ska ligga under projektmappen och överleva att containern återskapas.

Nuvarande `docker-compose.yml` använder `joedwards32/cs2` och lagrar serverfiler i `/home/r0p3/data/gameservers/cs2-data`. Den innehåller en inklistrad nano-rubrik före YAML, hemligheter i klartext, en äldre utkommenterad konfiguration och botkvot 20 med maxplayers 10. Aktiv tjänst saknar restart-policy. Git-metadata är inte åtkomlig som ett fungerande repository i denna arbetsmiljö; historik och remote har därför inte granskats.

## Teknikval

- Behåll befintlig containerbas inledningsvis, men lås den till testad image-digest.
- Använd Metamod:Source 2.x och CounterStrikeSharp med medföljande .NET-runtime.
- Skapa ett litet eget C#-plugin, `RepServer`, för kommandon, lägesbyten, röstning och tillstånd. Undvik flera plugins som samtidigt styr vapen, kartor och matchflöde.
- Lås kompatibla versioner av Metamod, CounterStrikeSharp, SDK och pluginberoenden med checksummor och låsfil. Välj exakta versioner vid implementation och verifiera dem mot aktuell CS2-build.

CounterStrikeSharps installationsguide beskriver kombinationen med Metamod 2.x. Det är den stack jag rekommenderar här i stället för att bygga på äldre SourceMod/SourcePawn-plugins. [Installationsguide](https://docs.cssharp.dev/docs/guides/getting-started), [Metamods information om Source 2-grenen](https://www.sourcemm.net/).

## Spelarkommandon

Föreslagen policy: alla spelare får läsa status och starta omröstningar om godkända kartor/lägen. Administratörer identifieras med SteamID64 och kan verkställa direkt. På en server med bara en mänsklig spelare kan samma röstningsflöde godkännas direkt. Bots deltar inte.

| Kommando | Beteende | Behörighet |
| --- | --- | --- |
| `!help` | Visa kommandon och hur röstning fungerar | Alla |
| `!status` | Visa läge, karta, bots och pågående åtgärd | Alla |
| `!maps` | Lista tillgängliga kartor | Alla |
| `!modes` | Lista de tre lägena | Alla |
| `!map de_mirage` | Byt till godkänd karta | Omröstning/admin |
| `!change-mode bot-dm-pistol` | Byt läge och ladda om kartan vid behov | Omröstning/admin |
| `!mode bot-dm` | Kort alias för `!change-mode` | Omröstning/admin |
| `!restart` / `!omstart` | Återställ matchen i aktuellt läge | Omröstning/admin |
| `!pause` | Pausa match i `bot-game` vid tillåten rundgräns | Omröstning/admin |
| `!unpause` | Återuppta pausad match | Omröstning/admin |
| `!workshop add <id eller URL>` | Validera och registrera Workshop-karta | Admin |
| `!workshop list` | Visa kartor och installationsstatus | Alla |
| `!map <workshop-alias>` | Ladda registrerad Workshop-karta | Omröstning/admin |
| `!yes` / `!no` | Rösta på aktuell fråga | Mänskliga spelare |

`restart` betyder matchomstart. Containeromstart görs via driftkommandot på värden. Pluginet behöver ingen Docker-socket eller möjlighet att köra godtyckliga shellkommandon.

Röstning: enkel majoritet av de röstberättigade människorna vid start, 20 sekunders tidsgräns, en omröstning åt gången och 60 sekunders cooldown. Bestäm och testa hantering av frånkopplingar; sänk inte nämnaren mitt i en omröstning. Servern meddelar resultat och ger nedräkning före kart-/lägesbyte. Paus får en konfigurerbar maxtid så att servern inte lämnas pausad.

Validera kartnamn mot katalog och lägen mot fast lista. Tolka Workshop-URL som numeriskt ID; skicka aldrig rå chatttext till serverkonsolen. Logga vem som initierade administrativa ändringar och resultatet.

## Spellägen

Antagande: `bot-game` betyder rundbaserad competitive-liknande träning med bots som fyller lediga platser. Mänskliga spelare får välja lag. Ett särskilt människor-mot-bots-läge kan läggas till senare.

| Läge | Regler |
| --- | --- |
| `bot-dm-pistol` | FFA deathmatch, snabb respawn, pistoler och kniv, inga granater eller primärvapen; bots följer samma vapenregler |
| `bot-dm` | FFA deathmatch, snabb respawn, konfigurerbar vapenpool och bots som fyller upp servern |
| `bot-game` | Lagbaserat bombscenario, ekonomi och rundor, ingen respawn mitt i rundan, paus/unpause |

Första standard: 10 spelplatser, botnivå 3, fill till totalt 10 deltagare när kartan tillåter det. Pistol-DM börjar med lagets standardpistol och valbar pistol via meny. Paus i DM avvisas med begripligt svar i första versionen.

Varje läge får en explicit konfigurationsprofil. Ett gemensamt baslager återställer bland annat ekonomi, respawn, friendly fire, vapenregler, rundtid och botinställningar så att gamla värden inte läcker mellan lägen. Pistolbegränsning måste även täcka spawn, köp, upplockning, bonusvapen och bots; enbart en bot-cvar räcker inte.

Lägesbytet är en kontrollerad operation: validera → lås andra byten → meddela → spara önskat läge → sätt speltyp/läge → kartomladdning → applicera profil efter motorns standardkonfiguration → verifiera → spara aktivt läge. Verifiera exakt ordning mot aktuell CS2-build. Om byte av speltyp kräver processomstart ska det vara en uttrycklig, testad fallback och meddela spelarna att återanslutning kan behövas.

## Workshop

Separera att lägga till en karta från att byta till den. Spara ID, alias, namn och status i beständig lokal katalog. Kontrollera att objektet är en tillgänglig CS2-karta; collections hanteras separat eller avvisas i första versionen.

Utred och provkör aktuell servers Workshop-kommandon i första tekniska etappen innan automatisk förhämtning utlovas. Första laddningen kan behöva hämta kartan. Visa status, använd timeout och återgå till känd fungerande standardkarta vid fel. Märk kartor som redo först efter lyckad laddning. Kontrollera att bots kan navigera på testkartan; alla Workshop-kartor fungerar inte bra med bots.

Containerns upstream beskriver Workshop-stödet som experimentellt. [Containerdokumentation](https://github.com/joedwards32/CS2#steam-workshop).

## Struktur och beständig data

```text
docker-compose.yml
Dockerfile                      # bygg plugin och paketera låsta beroenden
.env.example                    # enbart platshållare
.gitignore
README.md
versions.lock.json
config/
  server.cfg
  modes/                        # basprofil och tre lägesprofiler
  maps.json                     # versionshanterad grundlista
  permissions.example.json
src/RepServer/
tests/
scripts/
  pre.sh                        # synkronisera hanterade filer efter speluppdatering
  update.sh
  backup.sh
  restore.sh
docs/
  operations.md
.env                            # lokal, ignorerad av Git
data/                           # lokal, ignorerad av Git
  server/                       # CS2-filer, Workshop-cache och spelloggar
  state/                        # aktivt läge, dynamisk kartlista, admins, auditlogg
backups/                        # lokala snapshots, ignorerade av Git
```

Byt bind mount till `./data/server:/home/steam/cs2-dedicated`. Montera även `data/state` på en uttrycklig sökväg för pluginets data. Versionshanterade standarder och lokalt tillstånd har olika ägare: uppdatering får inte skriva över admins, dynamiskt tillagda kartor eller sparat läge. Definiera sammanslagning av grundkartlista och lokala tillägg via stabilt ID.

Git innehåller återbyggbar kod, konfiguration och versionsval. Spelfiler, loggar, hemligheter, nedladdade kartor och runtime-data ligger på disk under repot men committas inte. Backup ska dessutom kopieras till en annan disk eller värd; en lokal mapp skyddar inte mot diskhaveri.

Migrering: inventera befintliga filer och diskutrymme → gör backup → stoppa tjänsten → kopiera befintlig datakatalog med bevarade rättigheter till `data/server` → verifiera kopian → byt mount → starta och testa. Behåll originalkatalogen tills återställning har verifierats. Fastställ uid/gid för vald image och skrivbehörigheter utan `chmod 777`.

## Uppstart och uppdatering

Föreslagen egen image bygger pluginet i ett separat byggsteg och innehåller versionslåsta modpaket utanför den monterade serverkatalogen. Ett uppstartsskript kopierar bara hanterade filer därifrån till servern och tar bort tidigare hanterade pluginfiler som inte längre ingår. Det får inte rensa användardata.

Verifiera upstream-entrypointens ordning: SteamCMD-uppdatering först, därefter installation/synkronisering av mods och konfiguration, sedan spelstart. Använd upstream `pre.sh` om ordningen och felhanteringen passar, annars en dokumenterad wrapper. Metamod-raden i `gameinfo.gi` ska kontrolleras och läggas till idempotent efter varje speluppdatering. Säkerställ att fel i installationen stoppar starten och inte lämnar en server som tyst saknar pluginet.

Målet för vardagsdrift efter implementation:

```bash
git pull --ff-only
./scripts/update.sh
```

`update.sh` ska:

1. Låsa mot parallella uppdateringar, validera miljö/konfiguration utan att skriva ut hemligheter och kontrollera diskutrymme.
2. Hämta den låsta basimagen och bygga plugin/ny image innan servern stoppas. Avbryt vid byggfel.
3. Meddela anslutna spelare, stoppa kontrollerat och ta konsekvent backup av tillstånd och konfiguration. Bevara föregående image och versionsmanifest.
4. Köra motsvarigheten till `docker compose up -d --force-recreate cs2` med den nybyggda imagen. Vanligt `up` utan recreate räcker inte alltid för ändrade bind-monterade filer.
5. Vänta med generös timeout för SteamCMD/Workshop och verifiera både serverrespons och pluginets version/status. Skriv ut tydligt resultat och felsökningskommando.
6. Vid fel: behåll loggar och backup, rapportera felet och erbjud dokumenterad återställning. Undvik automatisk omstartsloop vid inkompatibel speluppdatering.

`docker compose restart` applicerar inte ändrade miljövariabler eller Compose-inställningar. `up` kan återskapa containern med bibehållna mounts. [Docker restart](https://docs.docker.com/reference/cli/docker/compose/restart/), [Docker up](https://docs.docker.com/reference/cli/docker/compose/up/).

Containern uppdaterar enligt upstream spelet vid start. Låsning av mods och image gör därför inte själva Steam-builden reproducerbar. Uppdatering betyder senaste repo-valda, testade modversioner och den CS2-version Steam tillhandahåller; inte godtyckligt senaste plugin vid varje boot. Rollback av plugin/image är möjlig, men en äldre CS2-build kan inte garanteras vara tillgänglig eller kompatibel. [Upstream](https://github.com/joedwards32/CS2#how-to-use-this-image).

## Driftkvalitet

- Flytta tokens och lösenord till ignorerad `.env`. Byt värden som har delats eller checkats in; granska Git-historik när den är åtkomlig. Skriv inte ut värdena i loggar eller dokumentation.
- Rensa nano-rubriken och gamla konfigurationsblock, välj en konfigurationskälla per inställning och dokumentera prioriteten mellan Compose, motorns cfg-filer, lägesprofiler och sparat tillstånd.
- Lägg till `restart: unless-stopped`, rimlig stopptid och roterande Docker-/pluginloggar. Healthcheck ska skilja lång uppstart från fel; Docker-statusen unhealthy startar inte automatiskt om en container.
- Exponera bara använda portar. Aktivera SourceTV-port när SourceTV används. Begränsa RCON-åtkomst i värdens nätverksregler efter behov.
- Ta backup av lokalt tillstånd, admins, hemligheter och egna filer med begränsade filrättigheter. Spelfiler och Workshop-cache är återhämtningsbara och behöver normalt inte ingå i varje backup. Sätt retention och prova återställning till separat datakatalog.
- CI: bygg plugin, kontrollera Compose med testvärden, validera JSON/lägesprofiler och kör tester för behörigheter, inmatningsvalidering, röstning och tillståndsövergångar. Komplettera med verkliga speltester; CI ensam bevisar inte CS2-kompatibilitet.

## Genomförandeordning och acceptans

1. **Grund och migrering:** rensa Compose, separera hemligheter, skapa repo-struktur och migrerings-/backupinstruktioner. Klart när ombyggd container återanvänder data och originalet kan återställas.
2. **Teknisk prototyp:** installera låst Metamod/CounterStrikeSharp, ladda minimalt plugin och verifiera `meta list`, kommandon, lägesbyten, paus och Workshop på den aktuella spelbuilden. Dokumentera vilka API:er/cvars som faktiskt fungerar innan funktionerna byggs färdigt.
3. **Tre spellägen:** implementera profiler, botantal, pistolregler och sparat läge. Klart när alla sex riktade byten mellan olika lägen fungerar utan kvarhängande inställningar, även efter kartbyte och containeromstart.
4. **Spelarverktyg:** lägg till hjälp, kartval, omstart, paus/unpause, behörigheter och röstning. Klart när obehöriga inte kan kringgå röstning och samtidiga kommandon hanteras förutsägbart.
5. **Workshop och tillstånd:** lägg till registrering via ID/URL, katalog, status, timeout och fallback. Testa ogiltigt ID, otillgänglig karta, lång nedladdning och återstart efter tillägg.
6. **Leveransflöde:** färdigställ update/backup/restore, CI och README. Klart när `git pull --ff-only` följt av `./scripts/update.sh` tar in en ändrad konfiguration och pluginversion, behåller lokala data och ger verifierbart hälsobesked.

Ingen del kräver webbpanel, extern databas eller fler driftstjänster i första versionen. Nästa praktiska steg är etapp 1–2; prototypen avgör detaljer som beror på aktuell CS2-build.
