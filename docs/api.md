# HTTP API-kontrakt til opgave #33 og #40

Denne kontrakt beskriver de seks HTTP-kald til oprettelse, kast, hold/slip og pointvalg. Opgave #40 udvider svarene fra #33 med spillerens pointtavle, mulige point og turstatus. Pointreglerne er beskrevet i [scoring.md](scoring.md).

**Status ved slutkontrollen af #40:** Alle seks endpoints er implementeret, gennemgået og automatisk HTTP-testet. Release-buildet bestod med 0 fejl og 0 advarsler, og alle 612 tests bestod. Aflevering og CI dokumenteres i PR'en til #40; merge til `main` udestår. Frontenden bruger fortsat mock-data; den grafiske pointtavle følger i #41. Næste tur og multiplayer følger i #44.

## Ansvar og dataflow

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
| `players` | Array med præcis én spiller. Spillerens `name` er det trimmede navn; `scorecard` indeholder pointtavlen. |
| `currentPlayer` | Heltalsindeks i `players`; altid `0` i #40. |
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
- Der findes intet spil ved serverstart. Først et gyldigt `POST /api/game/new` opretter tilstanden.
- Et nyt spil nulstiller terninger, hold-markeringer, slag, alle 15 pointfelter og turstatus og bruger det nye spillernavn.
- Ved genstart af serveren går spiltilstanden tabt; der bruges ingen database i #40.
- Lobby, multiplayer, næste tur, frontendens pointtavle og den samlede fysiske Oplà-integration følger deres særskilte opgaver.

## Kontrolscenarier og tests

Eksemplet ovenfor og fejltabellen er dækket af [HTTP-testene](../backend/Yatzi.Tests/GameApiTests.cs). Testene kontrollerer også, at gentagne GET-kald og forskellige klienter bruger samme spil, at et nyt spil nulstiller en igangværende tur, og at afviste requests bevarer tilstanden. Alle terningenumre 1–5 samt samtidige kast og hold/slip er dækket. [Point-API-testene](../backend/Yatzi.Tests/GameScoringApiTests.cs) kontrollerer alle 15 felt-id'er, placering og strygning efter 1–3 kast, nulresultater, fejlprioritet, låst tur og konkurrerende feltvalg. Handler-testene dækker også HTTP-feltvalg samtidig med `ROLL` og `HOLD`. [Sessionstestene](../backend/Yatzi.Tests/GameSessionTests.cs) kontrollerer, at svarenes arrays og dictionaries ikke deler tilstand med spillet eller andre svar, heller ikke efter feltvalg og nyt spil.

Kør hele testsuiten fra repositoryets rod med `dotnet test H3Yatzi.sln --configuration Release`.

Kontrakten er klar til deling med Frederik, men hans accept og den faktiske tilkobling er endnu ikke verificeret. Backendens automatiske tests omfatter ikke fysisk hardware eller et helt spil med flere ture. Slutkontrollen afleveres til review i en åben PR; #40 forbliver igangværende, indtil den senere merge og CI er verificeret.
