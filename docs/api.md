# HTTP API-kontrakt til opgave #33 og #40

Denne kontrakt beskriver de seks HTTP-kald til oprettelse, kast, hold/slip og pointvalg. Opgave #40 udvider svarene fra #33 med spillerens pointtavle, mulige point og turstatus. Pointreglerne er beskrevet i [scoring.md](scoring.md).

**Læsestatus efter #44 trin 8, 10. november 2026:** Afsnittene nedenfor frem til [#44-kontrakten](#kommende-kontrakt-for-44) bevarer grundlaget før #44 og de historiske #33/#40-kontroller. På `a92c8a9` var kun første spiller aktiv, og første feltvalg låste turen. Frontendens request-tilpasning til #44 er implementeret i trin 7 og automatisk testet; rigtig browserkontrol er nu gennemført i trin 8.

**Aktuel lokal adfærd efter trin 8:** Roll, hold, score og strike kræver nu JSON med gameId, turnNumber og playerId i begge spilformer. Sessionen validerer kontekst og handling atomisk. Lobbyen køer STATE efter HTTP-kast/hold, STATE/WAIT ved næste tur og END ved afslutning; READY genskaber samme status. De 11 spilfelter fra trin 4 bevares. Afvisninger bruger det aftalte danske message-format og 400/404/409. POST /api/lobby/restart starter nu omkamp med samme deltagere og parringer efter afslutning, når alle er forbundne. Frontenden sender nu korrekt kontekst, fastholder joinedPlayerId, bruger serverens slutresultat og tilslutter omkampsknappen. De tidligere bodyløse browserkald er erstattet. Se [implementering og samlet lokal slutkontrol](game-lifecycle.md#slutkontrol-af-44-i-trin-8).

**Status ved slutkontrollen af #40:** Alle seks endpoints er implementeret, gennemgået og automatisk HTTP-testet. Release-buildet bestod med 0 fejl og 0 advarsler, og alle 612 tests bestod. Afleveringsstatus og CI dokumenteres i [PR #76](https://github.com/Bmantaz/H3-Yatzi/pull/76). Frontenden bruger fortsat mock-data; den grafiske pointtavle følger i #41. Næste tur og multiplayer følger i #44.

## Ansvar og dataflow

**Historisk udvidelse i #59 trin 5–6:** POST /api/lobby/start opretter 1–6 deltagere i samme GameSession med egne pointtavler og første deltager som aktiv. Spil-JSON bruger samme felter som nedenfor. HTTP-handlinger påvirker første deltager, og /api/game/new giver 409 efter lobbystart. Første spillers aktuelle Oplà kan også kaste/holde gennem servervalideret PlayerId; HTTP tilføjes ingen identitetsfelter eller automatiske WebSocket-udsendelser. Se [lobbykontrakten](lobby.md). Turskifte følger i #44. De historiske #40-resultater ovenfor bevares.

Frontenden sender handlinger til `Yatzi.Api`, som bruger reglerne i `Yatzi.Core.Game` og returnerer spiltilstanden som JSON. Frontenden viser derefter svaret. Terningeværdier og antal brugte slag bestemmes af serveren. Feltvalg går gennem `GameSession` til `Game → Scorecard → ScoreCalculator`. Sessionen bruger samme lås til turkontrol, registrering og kopiering af hele svaret. Arrays, spilleroplysninger og begge dictionaries er selvstændige kopier i hvert svar.

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
| `POST /api/game/score/{category}` | Ingen | Beregner og gemmer feltets point fra serverens aktuelle terninger og afslutter turen. |
| `POST /api/game/strike/{category}` | Ingen | Gemmer 0 i feltet og afslutter turen, også når feltet kunne give point. |

`name` skal være tekst. Serveren fjerner indledende og afsluttende mellemrum; navnet skal derefter indeholde **1–20 tegn**. Et ugyldigt navn afvises, før et eksisterende spil erstattes.

Et kast er tilladt i en åben tur, når `rollsUsed` er 0, 1 eller 2. Et kast med alle fem terninger holdt bruger stadig ét slag. Hold/slip er kun tilladt i en åben tur efter første eller andet kast og ændrer hverken terningeværdier eller antal brugte slag.

### Terningenumre og indeks

API'et bruger terningenumre **1–5**, ligesom Oplàens `HOLD n`. Arrays i JSON, frontenden og `Game` bruger indeks **0–4**.

| Valg | API-kald | Indeks i `dice`, `held` og `Game` |
|---|---|---|
| Terning 1 | `POST /api/game/hold/1` | `0` |
| Terning 2 | `POST /api/game/hold/2` | `1` |
| Terning 5 | `POST /api/game/hold/5` | `4` |

Frederiks frontend skal derfor sende `index + 1` i URL'en. API-laget validerer nummeret og sender `n - 1` til `Game.ToggleHold(index)`.

Terningenummeret valideres før opslag af spillet. Et ugyldigt nummer giver derfor 400, også når intet spil er oprettet. Et gyldigt nummer uden et oprettet spil giver 404.

### Felt-id'er og feltvalg

`category` skal matche præcis ét af disse id'er:

`ones`, `twos`, `threes`, `fours`, `fives`, `sixes`, `onePair`, `twoPairs`, `threeOfAKind`, `fourOfAKind`, `smallStraight`, `largeStraight`, `fullHouse`, `chance`, `yatzy`.

Der trimmes ikke, og store/små bogstaver er betydningsbærende. `OnePair`, enum-tal, kombinerede navne, `bonus` og `total` er ugyldige. Felt-id valideres før spilopslag, så ugyldigt id altid giver 400, også uden spil eller i en afsluttet tur.

Feltvalg kræver mindst ét kast og tillades efter første, andet eller tredje kast. Klienten sender kun handling og felt i URL'en; serveren bruger sine egne terninger. Et beregnet nul bruger feltet ligesom strygning. `null` betyder ledigt, mens alle gemte heltal, inklusive 0, betyder udfyldt og låst.

Efter et accepteret feltvalg er `turnCompleted=true`, og `possibleScores` er tomt. Terninger, hold, brugte slag og spillerindeks bevares. Nye kast, hold/slip og gyldige feltvalg afvises. Næste tur er endnu ikke tilgængelig; `/new` erstatter hele spillet og nulstiller pointene.

## Spiltilstand i response

Et nyt spil returnerer denne starttilstand:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    0,
    0,
    0,
    0,
    0
  ],
  "held": [
    false,
    false,
    false,
    false,
    false
  ],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false
}
```

| Felt | Type og betydning |
|---|---|
| `players` | Array med én spiller fra `/api/game/new` eller 1–6 i tilmeldingsrækkefølge fra lobbystart. `name` er det validerede navn, og hver `scorecard` er en selvstændig pointtavle. Spiller-id'er udstilles kun i lobby-JSON. |
| `currentPlayer` | Heltalsindeks i `players`; fortsat `0`, indtil #44 tilføjer turskifte. Terninger, hold, slag, mulige point og turstatus tilhører denne første deltager. |
| `dice` | Præcis fem heltal i terningernes rækkefølge. `0` betyder endnu ikke kastet. Efter første kast er alle værdier fra 1 til 6. |
| `held` | Præcis fem booleske værdier i samme rækkefølge som `dice`. `true` betyder holdt. |
| `rollsUsed` | Heltal fra 0 til 3: antal brugte slag i den aktuelle tur. |
| `players[i].scorecard.scores` | Alle 15 faste felt-id'er med `null` eller gemte heltalspoint. |
| `players[i].scorecard.upperSum` | Summen af gemte point i de seks øvre felter; ledige felter bidrager med 0. |
| `players[i].scorecard.bonus` | 50 ved øvre sum på mindst 63, ellers 0. Gælder også med andre øvre felter ledige. |
| `players[i].scorecard.total` | Alle gemte feltpoint plus bonus én gang. |
| `possibleScores` | Point for aktuelle ledige felter, inklusive 0. Tomt før første kast og efter turafslutning. |
| `turnCompleted` | `false` fra nyt spil, `true` efter ét accepteret feltvalg. |

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
| `/new`: Gyldigt input efter accepteret lobbystart | **409 Conflict** | Et lobbyspil er allerede startet og kan ikke nulstilles her. |
| `/hold/{n}`: `n` er ikke et heltal fra 1 til 5, fx `0`, `6` eller `abc` | **400 Bad Request** | Terningens nummer skal være mellem 1 og 5. |
| GET eller handling med gyldigt input før et spil er oprettet | **404 Not Found** | Der er ikke oprettet et spil. Start et nyt spil først. |
| Kast efter tredje slag i en åben tur | **409 Conflict** | Der er allerede brugt tre slag i denne tur. |
| Hold/slip før første eller efter tredje slag i en åben tur | **409 Conflict** | Hold/slip er kun tilladt efter første eller andet kast. |
| Feltvalg: ukendt id eller forkert stavning | **400 Bad Request** | Pointfeltet er ukendt. Brug et af de 15 felt-id'er. |
| Feltvalg før første kast | **409 Conflict** | Kast terningerne, før du vælger et pointfelt. |
| Feltet er allerede udfyldt | **409 Conflict** | Pointfeltet er allerede udfyldt. |
| Kast, hold/slip eller gyldigt feltvalg efter afsluttet tur | **409 Conflict** | Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur. |

I en afsluttet tur har turbeskeden forrang over øvrige spilregelafvisninger. Et gentaget feltvalg giver derfor turbeskeden. Validering af felt-id og terningenummer har fortsat forrang og giver 400 ved ugyldigt input.

400 beskriver et ugyldigt input, 404 et manglende spil og 409 en handling, der ikke er tilladt i den aktuelle spiltilstand.

**Afviste requests ændrer ikke spiltilstanden.** Det gælder også, hvis `/new` afvises, mens et spil allerede er i gang. Frontenden skal kontrollere HTTP-status og vise `message` ved fejl i stedet for at behandle svaret som en spiltilstand.

## Sammenhængende eksempel

Alle seks handlingssvar nedenfor har HTTP-status **200 OK** og `Content-Type: application/json`. Terningeværdierne illustrerer ét muligt forløb; nye kast giver tilfældige værdier.

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
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    0,
    0,
    0,
    0,
    0
  ],
  "held": [
    false,
    false,
    false,
    false,
    false
  ],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false
}
```

### 2. Hent tilstanden

`GET /api/game` uden request-body returnerer samme tilstand:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    0,
    0,
    0,
    0,
    0
  ],
  "held": [
    false,
    false,
    false,
    false,
    false
  ],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false
}
```

### 3. Kast første gang

`POST /api/game/roll` uden request-body giver eksempelvis:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    1,
    4,
    2,
    6,
    3
  ],
  "held": [
    false,
    false,
    false,
    false,
    false
  ],
  "rollsUsed": 1,
  "possibleScores": {
    "ones": 1,
    "twos": 2,
    "threes": 3,
    "fours": 4,
    "fives": 0,
    "sixes": 6,
    "onePair": 0,
    "twoPairs": 0,
    "threeOfAKind": 0,
    "fourOfAKind": 0,
    "smallStraight": 0,
    "largeStraight": 0,
    "fullHouse": 0,
    "chance": 16,
    "yatzy": 0
  },
  "turnCompleted": false
}
```

### 4. Hold terning 2

`POST /api/game/hold/2` uden request-body ændrer kun `held[1]`:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    1,
    4,
    2,
    6,
    3
  ],
  "held": [
    false,
    true,
    false,
    false,
    false
  ],
  "rollsUsed": 1,
  "possibleScores": {
    "ones": 1,
    "twos": 2,
    "threes": 3,
    "fours": 4,
    "fives": 0,
    "sixes": 6,
    "onePair": 0,
    "twoPairs": 0,
    "threeOfAKind": 0,
    "fourOfAKind": 0,
    "smallStraight": 0,
    "largeStraight": 0,
    "fullHouse": 0,
    "chance": 16,
    "yatzy": 0
  },
  "turnCompleted": false
}
```

### 5. Kast anden gang

`POST /api/game/roll` uden request-body kaster de fire ikke-holdte terninger:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    6,
    4,
    5,
    1,
    2
  ],
  "held": [
    false,
    true,
    false,
    false,
    false
  ],
  "rollsUsed": 2,
  "possibleScores": {
    "ones": 1,
    "twos": 2,
    "threes": 0,
    "fours": 4,
    "fives": 5,
    "sixes": 6,
    "onePair": 0,
    "twoPairs": 0,
    "threeOfAKind": 0,
    "fourOfAKind": 0,
    "smallStraight": 0,
    "largeStraight": 0,
    "fullHouse": 0,
    "chance": 18,
    "yatzy": 0
  },
  "turnCompleted": false
}
```

Terning 2 beholder værdien **4**, og `rollsUsed` er nu **2**. Et GET-kald efter dette returnerer samme tilstand. Et nyt `POST /api/game/hold/2` slipper terningen, så den kastes igen ved næste kast.

### 6. Placér point og afslut turen

`POST /api/game/score/chance` uden request-body beregner 18 fra kastet og returnerer:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null,
          "twos": null,
          "threes": null,
          "fours": null,
          "fives": null,
          "sixes": null,
          "onePair": null,
          "twoPairs": null,
          "threeOfAKind": null,
          "fourOfAKind": null,
          "smallStraight": null,
          "largeStraight": null,
          "fullHouse": null,
          "chance": 18,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 18
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [
    6,
    4,
    5,
    1,
    2
  ],
  "held": [
    false,
    true,
    false,
    false,
    false
  ],
  "rollsUsed": 2,
  "possibleScores": {},
  "turnCompleted": true
}
```

Gentagne GET-kald returnerer denne tilstand uden ændringer. Kast, hold/slip og gyldige feltvalg giver nu 409 med turbeskeden.

Som alternativ fra eksempel 5 kan `POST /api/game/strike/chance` gemme 0 i stedet for 18. Det giver `scorecard.total=0` og `turnCompleted=true`, bevarer terninger, hold og slag og gør `possibleScores` tomt. Dette er et alternativt feltvalg og kan ikke udføres efter eksempel 6.

## Samtidighed og svar

Alle indgange deler sessionens eksisterende lås. To samtidige feltvalg, også i forskellige felter eller med blandet placering og strygning, giver præcis ét accepteret valg og én 409-afvisning. Hvilken request der vinder, er ikke fastlagt. Et feltvalg samtidig med et kast bruger terningerne i sit eget returnerede øjebliksbillede.

HTTP og Oplà-handlerens `ROLL`/`HOLD` bruger samme lås. Efter feltvalg giver handleren én Warning og intet `STATE`-svar ved nye handlinger. `STATE r hhhhh` ændres ikke; `WAIT`/`END` følger i #44.

## Spillets levetid og afgrænsning

- Serveren opbevarer ét fælles spil i hukommelsen. Alle browsere, der kalder denne server, arbejder på samme spil.
- Der findes intet spil ved serverstart. `POST /api/game/new` opretter et selvstændigt spil; `POST /api/lobby/start` opretter lobbyens deltagere i samme session.
- Før lobbystart nulstiller et nyt selvstændigt spil terninger, hold, slag, pointfelter og turstatus. Lobbystart erstatter et eventuelt selvstændigt spil med et friskt lobbyspil. Efter lobbystart afvises `/api/game/new` med 409 og beskeden `Et lobbyspil er allerede startet og kan ikke nulstilles her.`; ugyldigt input giver fortsat 400 før denne kontrol.
- Ved genstart af serveren går spiltilstanden tabt; der bruges ingen database i #40.
- [Lobbyens READY/PAIR, GET, join/JOINED, genforbindelse og start](lobby.md) er implementeret i #59 trin 2–5. Egne pointtavler er oprettet, men alle handlinger vedrører fortsat første deltager. Oplà-spillerkontrol, næste tur, frontendens pointtavle og fysisk integration følger de resterende trin og særskilte opgaver.

## Kontrolscenarier og tests

Eksemplet ovenfor og fejltabellen er dækket af [HTTP-testene](../backend/Yatzi.Tests/GameApiTests.cs). Testene kontrollerer også, at gentagne GET-kald og forskellige klienter bruger samme spil, at et nyt spil nulstiller en igangværende tur, og at afviste requests bevarer tilstanden. Alle terningenumre 1–5 samt samtidige kast og hold/slip er dækket. [Point-API-testene](../backend/Yatzi.Tests/GameScoringApiTests.cs) kontrollerer alle 15 felt-id'er, placering og strygning efter 1–3 kast, nulresultater, fejlprioritet, låst tur og konkurrerende feltvalg. Handler-testene dækker også HTTP-feltvalg samtidig med `ROLL` og `HOLD`. [Sessionstestene](../backend/Yatzi.Tests/GameSessionTests.cs) kontrollerer, at svarenes arrays og dictionaries ikke deler tilstand med spillet eller andre svar, heller ikke efter feltvalg og nyt spil.

Kør hele testsuiten fra repositoryets rod med `dotnet test H3Yatzi.sln --configuration Release`.

Kontrakten er klar til deling med Frederik, men hans accept og den faktiske tilkobling er endnu ikke verificeret. Backendens automatiske tests omfatter ikke fysisk hardware eller et helt spil med flere ture. Afleveringsstatus og CI dokumenteres i [PR #76](https://github.com/Bmantaz/H3-Yatzi/pull/76).

## Kommende kontrakt for #44

**Aftalt i trin 1 – implementeret og lokalt slutkontrolleret gennem trin 8.** GitHub-aflevering med PR, merge og grøn CI følger senere. Spilsvar, HTTP-kontekst, rotation, slutstatus, vindere og controllerudsendelser nedenfor er nu implementeret og automatisk kontrolleret. `/new` bruger fortsat navn, og første lobbystart ingen body. Omkamp er implementeret med den aftalte gameId-/playerId-body, og frontenden bruger nu denne kontrakt. Eksemplerne er kontrakt-/acceptgrundlag med illustrative GUID'er, ikke optagede netværksforløb. Se [arbejdsplanen og kontrolresultaterne](game-lifecycle.md#slutkontrol-af-44-i-trin-8).

### Ruter og request-body

Alle JSON-requests bruger `Content-Type: application/json`. Rutenavne, terningenumre 1–5, de 15 præcise felt-id'er og pointregler bevares.

| Endpoint | Request-body efter trin 6 | Svar ved accept |
|---|---|---|
| `POST /api/game/new` | Uændret: `{"name":"Andreas"}`. | 200 med et nyt enkeltspillerspil og nyt gameId. Må fortsat erstatte enkeltspiller, men aldrig et startet lobbyspil. |
| `GET /api/game` | Ingen. | 200 med den aktuelle, kopierede spiltilstand; 404 uden spil. |
| `POST /api/game/roll` | Handlingskonteksten nedenfor. | 200 med samme tur efter kastet. |
| `POST /api/game/hold/{n}` | Handlingskonteksten nedenfor. | 200 med samme tur efter hold/slip. |
| `POST /api/game/score/{category}` | Handlingskonteksten nedenfor. | 200 efter registrering og automatisk turskifte eller spilafslutning. |
| `POST /api/game/strike/{category}` | Handlingskonteksten nedenfor. | 200 efter registrering af 0 og automatisk turskifte eller spilafslutning. |
| `POST /api/lobby/start` | Ingen. | 200 ved første start; kan ikke starte omkamp. |
| `POST /api/lobby/restart` | Afsluttet gameId og tilmeldt playerId; begge ikke-tomme GUID-strenge. | 200 med nyt spil-id og friske tavler hos de samme deltagere. Se [omkampskontrakten](lobby.md#omkamp). |

**Implementeret kompatibilitetsændring i trin 5:** Roll, hold, score og strike kræver JSON-body i begge spilformer. Der er ingen gammel enkeltspillervej uden body. Backendtests og frontend sender nu eksplicit kontekst. Den midlertidige inkompatibilitet frem til trin 6 er dermed fjernet. `/new` og første lobbystart bevarer deres requests.

Handlingskontekst for en lobbyspiller, eksempelvis til `POST /api/game/roll`:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 1,
  "playerId": "11111111-1111-4111-8111-111111111111"
}
```

Alle tre felter er obligatoriske. gameId skal være tekst, der kan parses som en ikke-tom GUID. turnNumber skal være et positivt JSON-heltal inden for Int32. playerId skal være null eller tekst med en ikke-tom GUID. Ukendte JSON-felter ignoreres som i de eksisterende requests. I lobbyspil kræves en GUID; i enkeltspiller kræves null:

```json
{
  "gameId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "turnNumber": 1,
  "playerId": null
}
```

Konteksten skal komme fra den tilstand, handlingen vedrører. Browseren fastholder den ved et klik og ved eventuel bekræftelse af 0 point; den må ikke erstatte den med en nyere turs kontekst, før requesten sendes. playerId kommer fra join-svarets [joinedPlayerId](lobby.md#kommende-kontrakt-for-44), ikke fra navn eller et gæt om den aktive spiller.

Spil-id og turnummer sammenlignes med servertilstanden under samme lås som spillerkontrol, regelkontrol, spilændring og svarkopi. Feltvalg bruger serverens aktuelle terninger; klienten sender hverken terningeværdier eller point. Flere handlinger i samme tur bruger samme turnummer. Turnummeret er ikke et id for hvert kast, og gentagne roll/hold-requests inden for samme tur er ikke generelt idempotente.

### Udvidet spiltilstand

**Implementeret efter trin 4 og bevaret gennem trin 6:** De 11 svarfelter og reglerne i tabellen er aktive ved `/api/game/new`, GET, accepterede spilhandlinger, første lobbystart og omkamp.

Alle accepterede spilhandlinger samt lobbystart og omkamp returnerer det samme udvidede format. Hele svaret kopieres fra den accepterede tilstand, så senere handlinger ikke kan ændre det under serialisering eller afsendelse. Felter serialiseres fortsat som camelCase; feltrækkefølgen i JSON er ikke betydningsbærende.

| Felt | Betydning efter #44 |
|---|---|
| `players` | Samme format som hidtil, med 1–6 separate pointtavler i fast deltagerrækkefølge. Ingen nye id-felter her. |
| `currentPlayer` | Gyldigt indeks for den aktive spiller; efter afslutning sidste spillers indeks. |
| `dice`, `held`, `rollsUsed`, `possibleScores` | Tilhører den tur, currentPlayer viser. Frisk tur har fem 0'er, fem false, 0 slag og tomt possibleScores. |
| `turnCompleted` | False for en aktiv tur, også i svaret efter et normalt feltvalg, som allerede viser næste tur. True ved spilafslutning. |
| `gameId` | Nyt servergenereret, ikke-tomt GUID ved oprettelse, første lobbystart og omkamp. |
| `turnNumber` | Globalt turnummer fra 1; øges ved turskifte, også når én spiller fortsætter selv. Sidste feltvalg øger det ikke. |
| `gameCompleted` | True først, når alle spillernes 15 felter er udfyldt. |
| `winnerIndices` | Tomt under spillet; efter afslutning alle vindernes indeks i stigende deltagerrækkefølge. |

Et fuldt start-JSON efter lobbystart med Andreas og Frederik:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": null, "twos": null, "threes": null,
          "fours": null, "fives": null, "sixes": null,
          "onePair": null, "twoPairs": null, "threeOfAKind": null,
          "fourOfAKind": null, "smallStraight": null, "largeStraight": null,
          "fullHouse": null, "chance": null, "yatzy": null
        },
        "upperSum": 0, "bonus": 0, "total": 0
      }
    },
    {
      "name": "Frederik",
      "scorecard": {
        "scores": {
          "ones": null, "twos": null, "threes": null,
          "fours": null, "fives": null, "sixes": null,
          "onePair": null, "twoPairs": null, "threeOfAKind": null,
          "fourOfAKind": null, "smallStraight": null, "largeStraight": null,
          "fullHouse": null, "chance": null, "yatzy": null
        },
        "upperSum": 0, "bonus": 0, "total": 0
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false,
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 1,
  "gameCompleted": false,
  "winnerIndices": []
}
```

Efter et normalt feltvalg viser `players` både det netop gemte felt og de øvrige uændrede pointtavler. currentPlayer og turnNumber er allerede skiftet; terninger, hold og slag viser næste spillers friske tur. Se [acceptscenarier A–C](game-lifecycle.md#konkrete-acceptscenarier) for to spillere, rotation og enkeltspiller.

<a id="eksempel-paa-afsluttet-spil"></a>

### Eksempel på afsluttet spil

Forudsætningen er [scenarie D](game-lifecycle.md#d-sidste-feltvalg-og-uafgjort): Ved tur 30 mangler Frederik kun chance og har kastet fem 1'ere. `POST /api/game/score/chance` med gameId nedenfor, turnNumber 30 og playerId `22222222-2222-4222-8222-222222222222` giver HTTP 200 med følgende fulde svar:

```json
{
  "players": [
    {
      "name": "Andreas",
      "scorecard": {
        "scores": {
          "ones": 0, "twos": 0, "threes": 0,
          "fours": 0, "fives": 0, "sixes": 0,
          "onePair": 0, "twoPairs": 0, "threeOfAKind": 0,
          "fourOfAKind": 0, "smallStraight": 0, "largeStraight": 0,
          "fullHouse": 0, "chance": 8, "yatzy": 0
        },
        "upperSum": 0, "bonus": 0, "total": 8
      }
    },
    {
      "name": "Frederik",
      "scorecard": {
        "scores": {
          "ones": 0, "twos": 0, "threes": 0,
          "fours": 0, "fives": 0, "sixes": 0,
          "onePair": 0, "twoPairs": 0, "threeOfAKind": 0,
          "fourOfAKind": 0, "smallStraight": 0, "largeStraight": 0,
          "fullHouse": 0, "chance": 5, "yatzy": 0
        },
        "upperSum": 0, "bonus": 0, "total": 5
      }
    }
  ],
  "currentPlayer": 1,
  "dice": [1, 1, 1, 1, 1],
  "held": [false, false, false, false, false],
  "rollsUsed": 1,
  "possibleScores": {},
  "turnCompleted": true,
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 30,
  "gameCompleted": true,
  "winnerIndices": [0]
}
```

0 betyder udfyldt ligesom 5 eller 8. Det er totalen inklusive bonus, der afgør resultatet. Ved to Alex-spillere med hver 5 point er winnerIndices `[0,1]`; ved ét afsluttet enkeltspillerspil er det `[0]`. Frontenden bruger serverens gameCompleted og winnerIndices; den kan sortere en kopi af spillerne til ranglisten uden at ændre deres indeks i den oprindelige liste.

<a id="fejl-i-den-kommende-kontrakt"></a>
### Fejl efter trin 5

Alle afvisninger bevarer spil, point og tur. Fejl bruger `application/json` med dansk `message`; der returneres ikke en spiltilstand i fejlbodyen.

| Situation | Status | Aftalt message |
|---|---|---|
| Body mangler, er ugyldig JSON eller er ikke et objekt | 400 | Requesten skal indeholde et gyldigt JSON-objekt. |
| gameId mangler eller er ikke tekst med en gyldig, ikke-tom GUID | 400 | gameId skal være et gyldigt, ikke-tomt GUID. |
| turnNumber mangler eller er ikke et positivt Int32-heltal | 400 | turnNumber skal være et positivt heltal. |
| playerId mangler eller er hverken null eller tekst med en gyldig, ikke-tom GUID | 400 | playerId skal være null eller et gyldigt, ikke-tomt GUID. |
| playerId er null i lobbyspil | 400 | playerId skal være et gyldigt, ikke-tomt GUID i et lobbyspil. |
| Ugyldigt terningnummer eller felt-id | 400 | De eksisterende danske beskeder ovenfor bevares. |
| Gyldigt input, men intet spil er oprettet | 404 | Der er ikke oprettet et spil. Start et nyt spil først. |
| gameId matcher ikke det aktuelle spil | 409 | Spillet er ikke længere aktuelt. Hent spiltilstanden igen. |
| playerId er en GUID i enkeltspiller | 409 | Enkeltspillerhandlinger skal bruge playerId null. |
| playerId findes ikke blandt lobbyspillets deltagere | 409 | Spilleren deltager ikke i dette lobbyspil. |
| Spillet er afsluttet | 409 | Spillet er afsluttet. Start et nyt spil for at fortsætte. |
| turnNumber matcher ikke den aktuelle tur | 409 | Turen er ikke længere aktuel. Hent spiltilstanden igen. |
| Lobbyspilleren er ikke den aktive spiller | 409 | Det er ikke denne spillers tur. |
| Spilregel afviser kast, hold eller feltvalg | 409 | De eksisterende kerneregler og deres danske beskeder bevares, eksempelvis tre brugte slag eller et udfyldt felt. |

Fejlprioriteten fastlægges som: ruteinput → JSON/kontekstformat i rækkefølgen gameId, turnNumber, playerId → manglende spil → null-playerId i lobby → gameId-match → spillerrelation → spilafslutning → turnummer → aktiv spiller → kernens regler. Formatkontrol sker før opslag, mens de tilstandsafhængige kontroller og handlingen udføres samlet. GET uden spil kræver ingen kontekst og giver fortsat 404.

Omkamp bruger sin egen [fejltabel og fejlprioritet](lobby.md#fejl-ved-omkamp): playerId skal være en GUID, og turnNumber kræves ikke. HTTP-laget mapper LobbyRestartStatus til 200/404/409 efter formatkontrollen. Et afvist forsøg nulstiller hverken spil, lobby eller beskedkøer.

### Samtidighed, beskeder og frontend

Feltvalg og turskifte er én accepteret ændring. En konkurrerende request med gammel tur rammer ikke den nye spiller, heller ikke hvis denne allerede har kastet. Et nyt gameId afskærer gamle requests fra en tidligere omkamp. HTTP og Oplà bruger samme spilsession; lobbyen koordinerer afsendelsesrækkefølgen som beskrevet i [livscyklussen](game-lifecycle.md#komponentansvar-og-ordnede-beskeder).

Accepterede HTTP-kast og hold/slip sender samme STATE til den aktive Oplà som tilsvarende Oplà-input. Feltvalg sender ny STATE/WAIT eller END. Et sendesvigt ændrer ikke den accepterede spiltilstand eller et ellers aktivt requests 200-resultat; afsendelsesfejl og senere request-annullering medfører ingen automatisk genafspilning. READY læser den aktuelle status. Der tilføjes ikke gameId eller turnNumber til Oplàens tekstprotokol.

Backendens reservehandlinger kræver den aktive spillerrelation, men ikke en forbundet Oplà. De 11 svarfelter, kontekstkontrol og controllerbeskeder er testet efter trin 5. Frontenden sender nu konteksten fra handlingens snapshot og henter ny visning ved 409 uden automatisk at gentage handlingen med nyere kontekst. Feltknapper fastholder deres viste turs kontekst, også gennem 0-point-bekræftelsen, som sender strike. Spil-id og turnummer bruges også til at afvise gammel visning og stoppe gammel animation. De 39 Node-tests og alle 1.379 backendtests består. Rigtig Chrome med separat profil og den aftalte CDP-reservevej er nu kontrolleret med enkeltspiller, to ens navne, tilskuer, vindere, omkamp, forsinket polling og 390 px layout. Separate Production-prøver med rigtige HTTP-/WebSocket-klienter består for 1, 2 og 6 deltagere. De historiske #33/#40-testtal ovenfor dokumenterer ikke #44.
