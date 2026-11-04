# HTTP API-kontrakt til opgave #33

Denne kontrakt beskriver de fire HTTP-kald, som Frederik skal bruge til at oprette og styre en tur fra frontenden. JSON-formatet følger den eksisterende mock i `frontend/app.js`.

**Status:** Alle fire endpoints er implementeret og automatisk HTTP-testet. Frontenden bruger fortsat mock-data; tilkoblingen følger i opgave #35, og Oplà-integrationen følger de efterfølgende sprintopgaver. Opgave #33 er først afsluttet, når ændringerne er merget til `main` med bestået CI.

## Ansvar og dataflow

Frontenden sender handlinger til `Yatzi.Api`, som bruger reglerne i `Yatzi.Core.Game` og returnerer spiltilstanden som JSON. Frontenden viser derefter svaret. Terningeværdier og antal brugte slag bestemmes af serveren.

```text
Frontend → HTTP /api/game → Yatzi.Api → Yatzi.Core.Game → JSON → frontend
```

Brug relative URL'er fra frontenden, da API'et og frontenden serveres fra samme adresse. Oplàens beskeder og den senere WebSocket-integration er beskrevet i [kommunikationsprotokollen](protocol.md).

## Endpoints og requests

Alle vellykkede kald returnerer **HTTP 200 OK**, `Content-Type: application/json` og hele den aktuelle spiltilstand i response-bodyen. JSON-felter bruger camelCase som i eksemplerne nedenfor.

| Endpoint | Request-body | Adfærd ved succes |
|---|---|---|
| `POST /api/game/new` | `{"name":"Andreas"}` med `Content-Type: application/json` | Opretter et nyt spil og erstatter et eventuelt eksisterende spil. |
| `GET /api/game` | Ingen | Henter spiltilstanden uden at ændre den. |
| `POST /api/game/roll` | Ingen | Kaster ikke-holdte terninger og bruger ét slag. |
| `POST /api/game/hold/{n}` | Ingen | Skifter terning nummer `n` mellem holdt og sluppet. |

`name` skal være tekst. Serveren fjerner indledende og afsluttende mellemrum; navnet skal derefter indeholde **1–20 tegn**. Et ugyldigt navn afvises, før et eksisterende spil erstattes.

Et kast er tilladt, når `rollsUsed` er 0, 1 eller 2. Et kast med alle fem terninger holdt bruger stadig ét slag. Hold/slip er kun tilladt efter første eller andet kast og ændrer hverken terningeværdier eller antal brugte slag.

### Terningenumre og indeks

API'et bruger terningenumre **1–5**, ligesom Oplàens `HOLD n`. Arrays i JSON, frontenden og `Game` bruger indeks **0–4**.

| Valg | API-kald | Indeks i `dice`, `held` og `Game` |
|---|---|---|
| Terning 1 | `POST /api/game/hold/1` | `0` |
| Terning 2 | `POST /api/game/hold/2` | `1` |
| Terning 5 | `POST /api/game/hold/5` | `4` |

Frederiks frontend skal derfor sende `index + 1` i URL'en. API-laget validerer nummeret og sender `n - 1` til `Game.ToggleHold(index)`.

Terningenummeret valideres før opslag af spillet. Et ugyldigt nummer giver derfor 400, også når intet spil er oprettet. Et gyldigt nummer uden et oprettet spil giver 404.

## Spiltilstand i response

Et nyt spil returnerer denne starttilstand:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0
}
```

| Felt | Type og betydning |
|---|---|
| `players` | Array med præcis én spiller i opgave #33. Spillerens `name` er det trimmede navn fra oprettelsen. |
| `currentPlayer` | Heltalsindeks i `players`; altid `0` i opgave #33. |
| `dice` | Præcis fem heltal i terningernes rækkefølge. `0` betyder endnu ikke kastet. Efter første kast er alle værdier fra 1 til 6. |
| `held` | Præcis fem booleske værdier i samme rækkefølge som `dice`. `true` betyder holdt. |
| `rollsUsed` | Heltal fra 0 til 3: antal brugte slag i den aktuelle tur. |

## Fejl

De beskrevne klientfejl returneres som `application/json` med ét felt, `message`, der indeholder en dansk forklaring:

```json
{
  "message": "Der er allerede brugt tre slag i denne tur."
}
```

| Situation | HTTP-status | `message` |
|---|---|---|
| `/new`: Ugyldig JSON, manglende body eller en body, der ikke er et JSON-objekt | **400 Bad Request** | Requesten skal indeholde et gyldigt JSON-objekt. |
| `/new`: `name` mangler, er `null` eller har en anden type end tekst | **400 Bad Request** | Navnet skal være tekst. |
| `/new`: Det trimmede navn er tomt eller længere end 20 tegn | **400 Bad Request** | Navnet skal indeholde mellem 1 og 20 tegn. |
| `/hold/{n}`: `n` er ikke et heltal fra 1 til 5, fx `0`, `6` eller `abc` | **400 Bad Request** | Terningens nummer skal være mellem 1 og 5. |
| GET, kast eller hold/slip før et spil er oprettet | **404 Not Found** | Der er ikke oprettet et spil. Start et nyt spil først. |
| Kast efter tredje slag | **409 Conflict** | Der er allerede brugt tre slag i denne tur. |
| Hold/slip før første eller efter tredje slag | **409 Conflict** | Hold/slip er kun tilladt efter første eller andet kast. |

400 beskriver et ugyldigt input, 404 et manglende spil og 409 en handling, der ikke er tilladt i den aktuelle spiltilstand.

**Afviste requests ændrer ikke spiltilstanden.** Det gælder også, hvis `/new` afvises, mens et spil allerede er i gang. Frontenden skal kontrollere HTTP-status og vise `message` ved fejl i stedet for at behandle svaret som en spiltilstand.

## Sammenhængende eksempel

Alle fem svar nedenfor har HTTP-status **200 OK** og `Content-Type: application/json`. Terningeværdierne illustrerer ét muligt forløb; nye kast giver tilfældige værdier.

### 1. Opret et nyt spil

`POST /api/game/new` med `Content-Type: application/json` og følgende request-body:

```json
{
  "name": "  Andreas  "
}
```

Response viser, at navnet er trimmet:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0
}
```

### 2. Hent tilstanden

`GET /api/game` uden request-body returnerer samme tilstand:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0
}
```

### 3. Kast første gang

`POST /api/game/roll` uden request-body giver eksempelvis:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [1, 4, 2, 6, 3],
  "held": [false, false, false, false, false],
  "rollsUsed": 1
}
```

### 4. Hold terning 2

`POST /api/game/hold/2` uden request-body ændrer kun `held[1]`:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [1, 4, 2, 6, 3],
  "held": [false, true, false, false, false],
  "rollsUsed": 1
}
```

### 5. Kast anden gang

`POST /api/game/roll` uden request-body kaster de fire ikke-holdte terninger:

```json
{
  "players": [{ "name": "Andreas" }],
  "currentPlayer": 0,
  "dice": [6, 4, 5, 1, 2],
  "held": [false, true, false, false, false],
  "rollsUsed": 2
}
```

Terning 2 beholder værdien **4**, og `rollsUsed` er nu **2**. Et GET-kald efter dette returnerer samme tilstand. Et nyt `POST /api/game/hold/2` slipper terningen, så den kastes igen ved næste kast.

## Spillets levetid og afgrænsning

- Serveren opbevarer ét fælles spil i hukommelsen. Alle browsere, der kalder denne server, arbejder på samme spil.
- Der findes intet spil ved serverstart. Først et gyldigt `POST /api/game/new` opretter tilstanden.
- Et nyt spil nulstiller terninger, hold-markeringer og slag og bruger det nye spillernavn.
- Ved genstart af serveren går spiltilstanden tabt; der bruges ingen database i opgave #33.
- Lobby, multiplayer, pointberegning, turskifte og Oplà-integration følger senere opgaver.

## Kontrolscenarier og tests

Eksemplet ovenfor og fejltabellen er dækket af [HTTP-testene](../backend/Yatzi.Tests/GameApiTests.cs). Testene kontrollerer også, at gentagne GET-kald og forskellige klienter bruger samme spil, at et nyt spil nulstiller en igangværende tur, og at afviste requests bevarer tilstanden. Alle terningenumre 1–5 samt samtidige kast og hold/slip er dækket. [Sessionstestene](../backend/Yatzi.Tests/GameSessionTests.cs) kontrollerer, at svarenes arrays ikke deler tilstand med spillet eller andre svar.

Kør hele testsuiten fra repositoryets rod med `dotnet test H3Yatzi.sln --configuration Release`.

Kontrakten er klar til deling med Frederik, men hans accept og den faktiske tilkobling er endnu ikke verificeret. Dokumentet alene afslutter ikke opgave #33.
