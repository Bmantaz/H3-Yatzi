# Pointregler og API til opgave #40

Denne specifikation fastlægger regler, pointtavle, feltvalg og HTTP-kontrakten til [#40 – Pointberegning for alle 15 felter + bonus](https://github.com/Bmantaz/H3-Yatzi/issues/40). Den er fælles reference for backendens implementering og tests og for Frederiks pointtavle i #41.

**Status ved slutkontrollen:** Implementeringen fra step 1–7 er gennemgået. Alle seks spil-endpoints returnerer pointtavle, mulige point og turstatus efter kontrakten nedenfor. [ScoreCategoryIds](../backend/Yatzi.Api/ScoreCategoryIds.cs) oversætter de 15 præcise tekst-id'er; [GameSession](../backend/Yatzi.Api/GameSession.cs) udfører handling og kopiering under samme lås. Beregningsadfærd og turregler i [Game](../backend/Yatzi.Core/Game.cs), [Scorecard](../backend/Yatzi.Core/Scorecard.cs), [ScoreCategory](../backend/Yatzi.Core/ScoreCategory.cs) og [ScoreCalculator](../backend/Yatzi.Core/ScoreCalculator.cs) er bevaret. Slutkontrollen rettede forældede kommentarer om integrationen; der blev ikke fundet funktionelle fejl.

**Automatisk verificeret lokalt ved slutkontrollen:** Release-buildet bestod med 0 fejl og 0 advarsler. Alle 612 testcases bestod: de 531 cases fra step 1–6 og de 81 fra step 7. Sidstnævnte fordeler sig på 61 [HTTP-pointtests](../backend/Yatzi.Tests/GameScoringApiTests.cs), fire [sessionstests](../backend/Yatzi.Tests/GameSessionTests.cs), 12 [handler-tests](../backend/Yatzi.Tests/OplaMessageHandlerTests.cs) og fire [handler-integrationstests](../backend/Yatzi.Tests/OplaMessageHandlerIntegrationTests.cs). De dækker alle 15 id'er, feltvalg efter 1–3 kast, nulresultater, fejlprioritet, uændrede afvisninger, kopierede arrays og dictionaries, konkurrerende feltvalg, samtidige kast og handlerhandlinger samt nyt spil. De eksisterende kerneprøver for bonusgrænsen 62/63/64 og maksimum 374 er bevaret.

Session- og HTTP-integrationen er implementeret og automatisk testet uden fysisk hardware. [api.md](api.md) beskriver den aktuelle kontrakt, og eksemplerne nedenfor viser det implementerede format. Afleveringsstatus og CI dokumenteres i [PR #76](https://github.com/Bmantaz/H3-Yatzi/pull/76); afslutningskravene til #40 står nederst i denne specifikation. Frontendens pointtavle følger i #41, næste tur og multiplayer i #44 og den samlede fysiske forbindelse i #43.

## Grundlag og beslutninger

| Kilde | Betydning for specifikationen |
|---|---|
| [Opgave #40](https://github.com/Bmantaz/H3-Yatzi/issues/40) | Kræver alle 15 felter, bonus ved mindst 63, placering, strygning, låste felter og tests. |
| [Projektets GDD](GDD.md) og [papirtavlen i GDD-illustrationen](GDD%20picture.png) | Fastlægger spillets mål, op til tre slag, de 15 felter og pointtavlens maksimumværdier. |
| [Danspils danske Yatzy-regler](https://danspil.dk/wp-content/uploads/2018/01/14036_regler_LR.pdf), PDF-side 3–4 | Understøtter pointtabellen, bonus, brug af ét felt pr. tur og muligheden for at stoppe efter færre end tre slag. |
| [Algas danske Yatzy-regler](https://algaspel.se/wp-content/uploads/2023/10/sexspeliett.pdf), PDF-side 26–27 | Præciserer forskellige værdier i to par og hus, point fra de relevante terninger samt at chance kan vælges uanset kastet. |

**Vores fortolkning af udgiverreglerne:** Når et kast indeholder flere ens terninger end en kombination kræver, må de nødvendige terninger bruges som en delkombination. Et par kræver derfor mindst to ens, tre ens mindst tre og fire ens mindst fire. Udgiverreglernes formulering om kast, der indeholder kombinationen, omsættes her til præcise betingelser; reglerne opstiller ikke selv en særskilt tabel over alle delkombinationer.

**Aftalte projektvalg:** Strygning tillades også i et felt, der kunne give point. Et accepteret feltvalg afslutter og låser turen; næste tur tilføjes i #44. Felt-id'er, JSON-format, HTTP-fejl og samtidighed nedenfor er tekniske beslutninger for projektet.

Der indføres ingen ekstra bonus eller jokerregel ved gentagne Yatzy-kast. Et senere kast med fem ens kan bruges i andre ledige felter efter de samme beregningsregler.

## Alle 15 pointfelter

Felt-id'erne er faste engelske identifikatorer. De bruges med præcis denne stavning i JSON og feltvalgets URL. Bonus og summer er beregnede værdier og er ikke blandt de 15 valgbare felter.

| Felt | Felt-id | Betingelse og beregning | Maksimum |
|---|---|---|---:|
| 1'ere | `ones` | Antal 1'ere × 1. | 5 |
| 2'ere | `twos` | Antal 2'ere × 2. | 10 |
| 3'ere | `threes` | Antal 3'ere × 3. | 15 |
| 4'ere | `fours` | Antal 4'ere × 4. | 20 |
| 5'ere | `fives` | Antal 5'ere × 5. | 25 |
| 6'ere | `sixes` | Antal 6'ere × 6. | 30 |
| Ét par | `onePair` | Find den højeste værdi med mindst to forekomster. Point = 2 × værdien. | 12 |
| To par | `twoPairs` | To forskellige værdier skal hver forekomme mindst to gange. Point = 2 × hver værdi, lagt sammen. Kun fire terninger tæller. | 22 |
| Tre ens | `threeOfAKind` | En værdi skal forekomme mindst tre gange. Point = 3 × værdien. | 18 |
| Fire ens | `fourOfAKind` | En værdi skal forekomme mindst fire gange. Point = 4 × værdien. | 24 |
| Lille straight | `smallStraight` | Alle værdierne 1, 2, 3, 4 og 5, hver præcis én gang, giver 15. | 15 |
| Stor straight | `largeStraight` | Alle værdierne 2, 3, 4, 5 og 6, hver præcis én gang, giver 20. | 20 |
| Hus | `fullHouse` | Præcis tre af én værdi og to af en anden. Point = summen af de fem værdier. | 28 |
| Chance | `chance` | Summen af de fem værdier. Kan vælges, også når andre felter kunne bruges. | 30 |
| Yatzy | `yatzy` | Alle fem terninger har samme værdi. Giver altid 50. | 50 |

### Input og beregning

- Pointberegneren modtager præcis fem heltal i intervallet 1–6 og ét kendt felt. Manglende terninger, forkert antal, værdien 0, andre værdier uden for intervallet og ukendte felter er inputfejl.
- For et gyldigt kast giver en manglende kombination 0 point. Det er forskelligt fra ugyldigt input, som afvises før beregningen.
- Terningernes rækkefølge og hold-markeringer påvirker ikke pointene. Beregningen ændrer hverken terninger, rækkefølge, slag, hold-markeringer eller pointtavle og bruger ingen tilfældighedskilde.
- Spillets fem nuller før første kast betyder endnu ikke kastet. De sendes ikke til pointberegneren som et gyldigt kast.
- To par og hus kræver forskellige værdier: fire ens er ikke to par, og fem ens er ikke hus.

Eksempelvis giver `6,6,6,6,6` 12 i ét par, 18 i tre ens, 24 i fire ens, 30 i seksere eller chance og 50 i Yatzy. To par og hus giver 0.

## Pointtavle, bonus og summer

Hver spiller har sin egen pointtavle med alle 15 felt-id'er:

- `null` betyder, at feltet er ledigt.
- Et heltal fra 0 til feltets maksimum betyder, at feltet er udfyldt og låst.
- Både en placering til 0 point og en udtrykkelig strygning gemmes som `0`. Der gemmes ingen særskilt årsag til nullet.
- Et udfyldt felt kan ikke overskrives, stryges igen eller genberegnes efter senere kast. Læsning af mulige point udfylder ingen felter.

`upperSum` er summen af de gemte point i `ones` til og med `sixes`; ledige felter bidrager med 0. `bonus` er 50, når `upperSum >= 63`, ellers 0. Bonussen gælder straks, når grænsen nås, også hvis nogle øvre felter stadig er ledige.

`total` er `upperSum + bonus + summen af de ni nederste felter`. Hver værdi tælles én gang. Bonus beregnes fra pointtavlen og lægges ikke til igen ved gentagne læsninger eller feltvalg. Maksimum er 105 i den øvre sektion + 50 i bonus + 219 i den nedre sektion = **374 point**.

### Implementeret kernegrænseflade efter step 5

`Scorecard.Record(IReadOnlyList<int> dice, ScoreCategory category)` kontrollerer, at feltet er kendt og ledigt, kalder `ScoreCalculator.Calculate` og gemmer først efter vellykket beregning. `Scorecard.Strike(ScoreCategory category)` bruger samme feltkontrol og gemmer 0. Fejl ændrer ingen felter eller summer, og efter et afvist kast er feltet stadig ledigt. Tavlen modtager ingen færdig pointværdi fra kalderen.

`Scores` er en løbende `IReadOnlyDictionary<ScoreCategory, int?>` med en `ReadOnlyDictionary` omkring den private lagring. Visningen afspejler legitime registreringer og strygninger, mens skrivning, fjernelse og rydning gennem dictionary-grænseflader afvises. Den er ikke et selvstændigt øjebliksbillede; API-laget kopierer data til sine svar under sessionens lås. `UpperSum`, `Bonus` og `Total` har ingen setters eller særskilte akkumulerede værdier.

Tavlen kender ingen tur, spillerliste eller netværksforbindelse og har ingen egen lås. Flere forskellige felter kan derfor registreres direkte på samme tavle ved kernetest. `Game` ejer nu en privat tavle og kontrollerer kast og tur før skrivning; sessionens eksisterende lås beskytter feltvalg sammen med svarene fra step 7.

### Spillets kernegrænseflade efter step 6

| Medlem på `Game` | Implementeret betydning |
|---|---|
| `IReadOnlyDictionary<ScoreCategory, int?> Scores` | Viderestiller tavlens løbende, skrivebeskyttede visning af alle 15 felter. |
| `int UpperSum`, `int Bonus`, `int Total` | Viderestiller tavlens beregnede værdier uden særskilt lagring. |
| `bool TurnCompleted` | Starter som `false`; kun spillet kan sætte den til `true` efter vellykket registrering. |
| `IReadOnlyDictionary<ScoreCategory, int> PossibleScores` | Et nyt, skrivebeskyttet øjebliksbillede af pointene for ledige felter ved hver læsning. |
| `void Score(ScoreCategory category)` | Kalder tavlens `Record` med spillets egne `Dice` og afslutter turen efter vellykket registrering. |
| `void Strike(ScoreCategory category)` | Kalder tavlens `Strike` og afslutter turen efter vellykket registrering. |

Begge feltvalg validerer først enum-værdien, derefter åben tur og mindst ét kast. Et ukendt felt giver `ArgumentOutOfRangeException` med parameteren `category` og forklaringen `Pointfeltet er ukendt.`. Regelafvisninger giver `InvalidOperationException` med de aftalte danske beskeder i fejltabellen nedenfor. Pointtavlen kontrollerer fortsat, at feltet er ledigt. Turen låses først, når tavlens skrivning er lykkedes.

`Roll` kontrollerer afsluttet tur før slaggrænsen og tilfældighedskilden. `ToggleHold` bevarer indeksvalideringen først og kontrollerer derefter afsluttet tur før de eksisterende kastbetingelser. Et gentaget gyldigt feltvalg i en afsluttet tur giver derfor turbeskeden frem for beskeden om udfyldt felt. Der er ingen offentlig funktion til at åbne turen igen.

`PossibleScores` er tom før første kast og efter turafslutning. Ellers beregnes ledige felter gennem `ScoreCalculator.Calculate`, også når resultatet er 0. Læsning ændrer ingen spiltilstand og bruger ingen tilfældighed; senere kast ændrer ikke tidligere returnerede øjebliksbilleder. `Scores` følger derimod legitime feltvalg som en løbende visning. API-laget kopierer de relevante data til sine svar i step 7.

Dataflowet i kernen er `Game.Score → turkontrol → Scorecard.Record → ScoreCalculator.Calculate → gemte point → TurnCompleted=true`. Ved strygning gemmer tavlen 0 efter samme turkontrol. Spillet udstiller ikke sin skrivbare `Scorecard` og har ingen egen lås.

## Placering, strygning og turens afslutning

Placering og strygning kræver et oprettet spil, en ikke-afsluttet tur, mindst ét gennemført kast og et ledigt felt. Spilleren må vælge efter første, andet eller tredje kast og behøver ikke bruge alle tre slag. Alle fem aktuelle terninger indgår i vurderingen, uanset hvilke der er holdt.

**Placering:** Serveren beregner det valgte felts point ud fra sine aktuelle terninger og gemmer resultatet. Hvis kombinationen mangler, gemmes 0, og feltet er stadig brugt.

**Strygning:** Serveren gemmer 0 i det valgte felt, også hvis kastet kunne give point dér. Frontendens bekræftelse ved 0 point hører til #41.

Et accepteret feltvalg udfører samlet følgende:

1. Gemmer point i præcis ét felt og opdaterer de beregnede summer.
2. Sætter `turnCompleted` til `true`.
3. Bevarer `dice`, `held`, `rollsUsed` og `currentPlayer` som turens resultat.
4. Gør `possibleScores` tomt og afviser efterfølgende kast, hold/slip og feltvalg.

En afvist handling ændrer ingen tilstand. Et afvist feltvalg afslutter derfor heller ikke turen. Der oprettes ingen offentlig funktion til at åbne turen igen i #40.

**Midlertidig grænse til #44:** Efter det første accepterede feltvalg kan det nuværende spil læses, men næste tur er endnu ikke implementeret. Det gælder også med én spiller. Et nyt spil gennem det eksisterende `/api/game/new` erstatter fortsat hele spillet og nulstiller også pointtavle og `turnCompleted`; det bevarer ikke tidligere point som en ny tur ville gøre.

## Ansvar og dataflow

| Komponent | Ansvar |
|---|---|
| `Yatzi.Core` | Beregner point, ejer pointtavlen og håndhæver feltvalg og afsluttet tur uafhængigt af HTTP og hardware. |
| `GameSession` | Ejer det fælles spil i hukommelsen og beskytter læsning, handlinger og svar med den eksisterende lås. |
| HTTP-API | Validerer felt-id, kalder sessionen og omsætter resultatet til spiltilstand eller dansk JSON-fejl. |
| Frontend i #41 | Viser gemte og mulige point, modtager feltvalg med musen og bekræfter valg til 0 point. |
| `OplaMessageHandler` | Bruger fortsat samme session til `ROLL` og `HOLD`; kernens afsluttede tur afviser også disse handlinger. |

Dataflowet ved placering er `frontend → HTTP-feltvalg → GameSession → Yatzi.Core → kopieret spiltilstand → JSON → frontend`. Klienten vælger kun feltet og handlingen; den leverer ikke de point eller terninger, der skal gemmes. Ved strygning gemmer kernen altid 0.

Spiltilstanden forbliver i hukommelsen og går tabt ved servergenstart. Der tilføjes ingen database i #40.

## HTTP-kontrakt implementeret i step 7

**Denne udvidelse er implementeret og automatisk testet i API'et.** De eksisterende felter `players`, `currentPlayer`, `dice`, `held` og `rollsUsed` bevarer deres betydning. #40 bruger fortsat én spiller og `currentPlayer: 0`; multiplayer følger i #44.

### Spiltilstand

| Felt | Type og betydning |
|---|---|
| `players[i].scorecard.scores` | Objekt med alle 15 faste felt-id'er. Hver værdi er `null` eller de gemte heltalspoint. |
| `players[i].scorecard.upperSum` | Heltal med gemte point fra de seks øvre felter. |
| `players[i].scorecard.bonus` | Heltal, 0 eller 50, beregnet fra `upperSum`. |
| `players[i].scorecard.total` | Heltal med spillerens samlede gemte point inklusive bonus. |
| `possibleScores` | Objekt med kun den aktuelle spillers ledige felt-id'er og deres beregnede heltalspoint. 0 medtages, når en kombination mangler. |
| `turnCompleted` | Boolesk værdi. `false` fra nyt spil; `true` efter ét accepteret feltvalg. |

`possibleScores` er `{}` før første kast og efter afsluttet tur. Ellers beregnes det fra det aktuelle kast uden at ændre pointtavlen. Udfyldte felter udelades fra objektet. Alle pointtavlens 15 felter findes altid i `scores`, også når de er ledige. JSON-felters rækkefølge er ikke betydningsbærende.

### Endpoints

| Endpoint | Request-body | Resultat |
|---|---|---|
| `POST /api/game/score/{category}` | Ingen | Beregner og gemmer point fra serverens aktuelle kast i feltet. |
| `POST /api/game/strike/{category}` | Ingen | Gemmer 0 i feltet. |
| `GET /api/game` | Ingen | Returnerer spiltilstanden inklusive pointtavle, mulige point og afsluttet tur. |

`category` er ét af felt-id'erne i pointtabellen med præcis den angivne stavning. Eksempelvis er `onePair` gyldigt; `OnePair`, `bonus` og tal er ugyldige felt-id'er.

Et vellykket feltvalg giver **HTTP 200 OK**, `Content-Type: application/json` og hele spiltilstanden. De fire eksisterende spil-endpoints returnerer samme udvidede format ved succes. [API-kontrakten](api.md) og de tests, der kontrollerer det præcise JSON-format, er opdateret samlet i step 7.

### Fejl

Fejl returneres som `application/json` med det eksisterende `message`-felt. Felt-id valideres før spilopslag, svarende til terningenummeret i det eksisterende hold-endpoint. Ukendt felt giver derfor 400, også uden et oprettet spil. Et gyldigt felt uden spil giver 404; regelafvisninger i et oprettet spil giver 409.

| Situation | HTTP-status | Dansk `message` |
|---|---|---|
| Ukendt felt-id | 400 | Pointfeltet er ukendt. Brug et af de 15 felt-id'er. |
| Intet oprettet spil | 404 | Der er ikke oprettet et spil. Start et nyt spil først. |
| Feltvalg før første kast | 409 | Kast terningerne, før du vælger et pointfelt. |
| Feltet er allerede udfyldt | 409 | Pointfeltet er allerede udfyldt. |
| Kast, hold/slip eller feltvalg efter afsluttet tur | 409 | Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur. |

I en afsluttet tur bruges fejlen om afsluttet tur før andre spilregelafvisninger. Et gentaget feltvalg giver derfor denne fejl, også hvis det samme felt vælges igen. Alle afvisninger bevarer spiltilstanden. Eksisterende fejl ved slaggrænse og hold/slip gælder fortsat i en ikke-afsluttet tur.

```json
{
  "message": "Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur."
}
```

### Samtidighed og svar

Validering af spiltilstand, pointberegning, lagring, turafslutning og kopiering af svaret sker under sessionens eksisterende fælles lås. Den samme lås bruges af HTTP og Oplà-handleren.

To samtidige, gyldige feltvalg i samme tur må kun give ét accepteret valg, også når de vælger forskellige felter eller blander placering og strygning. Det andet afvises med 409. Rækkefølgen er ikke fastlagt. Ved samtidige kast og feltvalg skal hvert accepteret feltvalg bruge terningerne på sit eget handlingstidspunkt under låsen.

Svarene kopierer også pointtavle og mulige point. En senere handling eller ændring af et returneret C#-objekt må ikke ændre tidligere svar eller det interne spil. Læsning udfører ingen spilhandling.

## Sammenhængende kontrakteksempel

Alle vellykkede svar nedenfor viser det implementerede HTTP 200-format. Terningeværdierne illustrerer et bestemt muligt forløb; serveren bestemmer værdierne ved rigtige kast.

### 1. Nyt spil

`POST /api/game/new` med bodyen `{"name":"Andreas"}` returnerer:

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
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false
}
```

### 2. Kast og mulige point

Spilleren kaster, holder terning 1–3 og kaster igen. Efter de eksisterende kast- og hold-kald kan svaret eksempelvis være:

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
  "dice": [5, 5, 5, 2, 2],
  "held": [true, true, true, false, false],
  "rollsUsed": 2,
  "possibleScores": {
    "ones": 0,
    "twos": 4,
    "threes": 0,
    "fours": 0,
    "fives": 15,
    "sixes": 0,
    "onePair": 10,
    "twoPairs": 14,
    "threeOfAKind": 15,
    "fourOfAKind": 0,
    "smallStraight": 0,
    "largeStraight": 0,
    "fullHouse": 19,
    "chance": 19,
    "yatzy": 0
  },
  "turnCompleted": false
}
```

### 3. Placér i hus og afslut turen

`POST /api/game/score/fullHouse` uden request-body gemmer 19 og returnerer:

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
          "fullHouse": 19,
          "chance": null,
          "yatzy": null
        },
        "upperSum": 0,
        "bonus": 0,
        "total": 19
      }
    }
  ],
  "currentPlayer": 0,
  "dice": [5, 5, 5, 2, 2],
  "held": [true, true, true, false, false],
  "rollsUsed": 2,
  "possibleScores": {},
  "turnCompleted": true
}
```

Et GET-kald returnerer denne tilstand uændret. Et efterfølgende kast, hold/slip eller gyldigt feltvalg giver 409 med fejlen om afsluttet tur.

### Alternativ til placering: stryg et felt

Fra tilstanden i eksempel 2 kan spilleren i stedet sende `POST /api/game/strike/fives` uden request-body. Det gemmer 0 i `fives`, selv om feltet kunne give 15. Alle andre felter forbliver `null`; `upperSum`, `bonus` og `total` er 0, `possibleScores` er `{}`, og `turnCompleted` bliver `true`. Terninger, hold-markeringer og brugte slag bevares. Svaret indeholder hele spiltilstanden i samme format som eksempel 3.

Dette er en alternativ afslutning af turen. Kaldet kan ikke udføres efter eksempel 3, hvor turen allerede er afsluttet.

## Kontrolgrundlag til de efterfølgende steps

Scenarierne er kontrolgrundlag for hele #40. Efter step 7 er pointberegning, tavle og turregler dækket i kernen, mens sessionen, alle seks HTTP-endpoints, samtidige feltvalg, kopierede svar og handlerens samspil efter feltvalg også er automatisk testet. Testene bruger den rigtige session og HTTP-pipeline i hukommelsen. Ingen test åbner flere ture eller fylder tavlen ved at omgå turreglerne; bonusgrænser og fulde tavler kontrolleres direkte gennem Scorecard.

### Pointberegning og validering

| Situation | Forventet resultat |
|---|---|
| `1,1,2,3,6` i de seks øvre felter | `ones=2`, `twos=2`, `threes=3`, `fours=0`, `fives=0`, `sixes=6`. |
| `1,1,2,2,3` | Højeste par 4, to par 6; tre og fire ens 0. |
| `5,5,4,4,2` | Ét par 10, to par 18; tre ens, fire ens og hus 0. |
| `6,6,5,5,5` | Højeste par 12, to par 22, tre ens 15 og fire ens 0; det højeste par kan vælges fra en anden værdi end de tre ens. |
| `1,1,1,1,1` | Yatzy 50, chance 5 og hus 0. |
| `6,6,6,6,6` | Ét par 12, to par 0, tre ens 18, fire ens 24, hus 0, seksere og chance 30, Yatzy 50. |
| `5,5,5,2,2` | Ét par 10, to par 14, tre ens 15, hus og chance 19; fire ens og Yatzy 0. |
| `1,1,1,2,2` | Mindste hus 7 og chance 7; begge straights og Yatzy 0. |
| `6,6,6,5,5` | Største hus 28 og chance 28; begge straights og Yatzy 0. |
| `3,3,3,1,2` | Hus 0, fordi de to sidste terninger er forskellige; chance 12. |
| `4,4,4,4,3` | Ét par 8, tre ens 12, fire ens 16; to par og hus 0. |
| `5,1,4,2,3` | Lille straight 15; stor straight 0. |
| `6,2,5,3,4` | Stor straight 20; lille straight 0. |
| `1,2,3,4,4` | Begge straights 0; en duplikat erstatter ikke den manglende værdi. |
| `2,3,4,5,5` | Begge straights 0; fire sammenhængende værdier er ikke tilstrækkeligt. Chance 19. |
| `1,2,3,4,6` | Par, to par, tre ens, fire ens, hus, straights og Yatzy 0; chance 16. |
| `null`, fire eller seks terninger, en værdi 0, -1 eller 7 samt ukendt felt | Afvis som inputfejl; beregn ikke et almindeligt resultat på 0. |
| Samme gyldige værdier i forskellig rækkefølge eller med andre hold-markeringer | Samme point; input og øvrig spiltilstand bevares. |

Alle 15 felter dækkes efter step 4 med gyldige resultater og relevante nulresultater. Chance har ingen nulresultater med gyldigt input og giver altid 5–30 point. For hvert felt er maksimumværdien i pointtabellen også kontrolleret.

### Pointtavle, tur og API

| Situation | Forventet resultat |
|---|---|
| Ny pointtavle | Alle 15 felter `null`; alle summer 0. |
| Tavlens registrering til 0 eller strygning | Gem 0 og lås feltet. Både ny registrering og strygning i feltet afvises uden ændringer. |
| Afvist input til tavlens `Record` | Alle felter og summer bevares, og feltet kan stadig bruges til et gyldigt kast. |
| Løbende `Scores`-visning | Registreringer og strygninger bliver synlige; ændringer gennem dictionary-grænseflader afvises. |
| Øvre sum 62 / 63 / 64 | Bonus 0 / 50 / 50. Bonus beregnes kun fra de seks øvre felter. |
| Øvre sum 63 med andre øvre felter stadig ledige | Bonus 50 uden krav om at udfylde resten først. |
| Gentagen læsning eller et senere feltvalg efter opnået bonus | Bonus tælles én gang i totalen. |
| Maksimum i alle 15 felter | Øvre sum 105, bonus 50, nedre sum 219, total 374. |
| Alle 15 felter strøget | Alle felter er udfyldt med 0 og låst; øvre sum, bonus og total er 0. |
| To separate pointtavler | Ændringer på den ene påvirker ikke den anden. |
| Feltvalg efter første, andet eller tredje kast | Tilladt i et ledigt felt, hvis turen ikke er afsluttet. |
| Placering uden passende kombination | Gem 0, lås feltet og afslut turen. |
| Strygning af et felt, der kunne give point | Gem 0, lås feltet og afslut turen. |
| Feltvalg før første kast, genbrug af felt eller handling efter afsluttet tur | Afvis; alle point, terninger, hold-markeringer, slag og turstatus bevares. |
| Hent mulige point | Medtag også 0 for ledige felter, udelad udfyldte felter, og ændr ingen tilstand. |
| Kernens mulige point før første kast eller efter afsluttet tur | Tomt, skrivebeskyttet øjebliksbillede uden tilfældighedskald. |
| Tidligere læste mulige point efter nye kast eller feltvalg | Bevar de tidligere værdier; nye læsninger følger den aktuelle tilstand. |
| To samtidige feltvalg i samme tur | Præcis ét accepteres og ét felt udfyldes; det andet får 409. |
| Handlerens `ROLL` eller `HOLD n` efter feltvalg gennem HTTP | Kernens turregel afviser handlingen; handleren bruger sin eksisterende afvisningsadfærd uden `STATE`-svar. |
| Nyt spil efter feltvalg | Ny tom pointtavle, nul brugte slag, uslåede terninger, ingen hold og `turnCompleted=false`. |
| Tidligere returnerede svar og ændringer af deres objekter | Påvirker hverken interne data eller andre svar. |
| De eksisterende HTTP-endpoints og tests | Bevar eksisterende adfærd; opdatér forventet JSON-format bevidst sammen med kontrakten. |

Grænsetilfælde for pointtavlen og genbrug af felter kan testes direkte i kernen, også inden #44 gør flere ture mulige gennem API'et. Et fuldt spil med flere ture eller spillere er ikke et verificeret resultat af #40.

## Afgrænsning og aflevering

| Opgave | Ansvar |
|---|---|
| [#40](https://github.com/Bmantaz/H3-Yatzi/issues/40) | Pointberegning, pointtavle, bonus, placering, strygning, låste felter og tur, backendkontrakt og relevante tests. |
| [#41](https://github.com/Bmantaz/H3-Yatzi/issues/41) | Grafisk pointtavle, mulige point, feltvalg med musen og bekræftelse ved 0 point. |
| [#44](https://github.com/Bmantaz/H3-Yatzi/issues/44) | Næste tur og spiller, 1–6 spillere, spillets afslutning og vinder samt `STATE`, `WAIT` og `END` ved turskifte og afslutning. |
| [#59](https://github.com/Bmantaz/H3-Yatzi/issues/59), [#61](https://github.com/Bmantaz/H3-Yatzi/issues/61) og [#63](https://github.com/Bmantaz/H3-Yatzi/issues/63) | Lobby og parring, browser-bro samt `/ws/opla` og forbindelser. |
| [#43](https://github.com/Bmantaz/H3-Yatzi/issues/43) | Samlet fysisk integrationstest med Oplà, browser, backend og frontend. |

Den aftalte [Oplà-protokol](protocol.md) ændres ikke i step 1. Handlerens nuværende ansvar er beskrevet i [opla-handler.md](opla-handler.md).

**Step 1 er afsluttet**, når denne specifikation dækker alle felter, særtilfælde, bonus, feltvalg, turafslutning, kontrakteksempler, kontrolscenarier og opgavegrænser uden åbne beslutninger. Kontrol af dokumentet er ikke en funktionstest af den planlagte løsning.

**#40 er først afsluttet**, når de efterfølgende implementeringssteps er gennemført, de relevante tests består, ændringerne er merget til `main` gennem en PR med `Closes #40`, og CI er grøn. Specifikationen alene opfylder ikke disse afleveringskrav.
