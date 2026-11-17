# Befintliga lägeskonfigurationer

Lägg de `.cfg`-filer ni redan kör här. Behåll filnamnen och ta med eventuella andra cfg-filer som de laddar med `exec`. Om en fil innehåller lösenord eller Steam-token, ta bort de raderna innan du lägger filen här.

Skriv gärna i samma mapp eller i chatten vilken fil ni kör för varje läge:

| Läge | Vad vi letar efter |
| --- | --- |
| `bot-dm-pistol` | Er nuvarande pistol-DM-konfiguration |
| `bot-dm` | Er vanliga DM-konfiguration |
| `bot-game` | Er rundbaserade matchkonfiguration mot bots |

Ta även med hur ni startar varje läge om det kräver ett separat serverkommando, särskilda startflaggor eller en Workshop-karta. Det hjälper oss att bevara både reglerna och laddningsordningen när spellägesbytet byggs.

Filerna i den här mappen är underlag för implementationen. De aktiveras när vi kopplar varje profil till spellägesbytet.
