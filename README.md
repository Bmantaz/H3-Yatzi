# H3 Yatzi 🎲

[![CI](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml/badge.svg)](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml)

Mellemprojekt: et digitalt Yatzy-spil, hvor **Arduino Oplà** (MKR WiFi 1010) bruges som raflebæger – ryst for at kaste, touch 1–5 for at holde terninger. Hver spiller har sin egen Oplà og PC og joiner en fælles lobby; terninger og pointtavle vises i browseren.

- 📄 [Game Design Document](docs/GDD.md)
- ✅ [Definition of Done & Nice to Have](docs/DoD.md)
- 🔌 [Protokol Oplà ⇄ server](docs/protocol.md) – USB + WiFi, lobby og parring
- 🌐 [HTTP API-kontrakt](docs/api.md) – handlingskontekst, spiltilstand og danske fejl
- 🧮 [Pointregler og point-API](docs/scoring.md) – alle 15 felter, bonus, strygning og turafslutning
- 🎲 [Oplà-beskedhåndtering](docs/opla-handler.md) – ROLL/HOLD, STATE-svar, fælles spilsession og tests
- 🔗 [WebSocket til Oplà](docs/opla-websocket.md) – forbindelser, READY/adgang, beskedgrænser og handlerintegration i #63
- 🔑 [Lobby og parring](docs/lobby.md) – entydigt join, genforbindelse, start og omkamp
- 🔄 [Spillets livscyklus og slutkontrol af #44](docs/game-lifecycle.md#slutkontrol-af-44-i-trin-8) – krav/test-matrix, browserkontrol og faktiske resultater

## Struktur

```
arduino/yatzi_controller/   Sketch til Oplaen
backend/Yatzi.Core/         Spillogik (regler, point, ture)
backend/Yatzi.Api/          ASP.NET Core backend (API + serverer frontend)
backend/Yatzi.Tests/        xUnit-tests
frontend/                   HTML/CSS/JS
tests/frontend/             Node-tests af frontendens adfærd
docs/                       GDD, DoD, protokol
.github/                    Actions, issue-skabeloner, issue-seed-data
```

Backendens terningelogik, 15 pointfelter, bonus og livscyklus er implementeret. Hvert score/strike gemmer ét felt og åbner næste deltagers tur i tilmeldingsrækkefølge; enkeltspiller fortsætter sin egen næste tur. Når alle tavler er fulde, bevares sidste kast og spillerindeks, og serveren returnerer gameCompleted og alle winnerIndices fra total inklusive bonus. HTTP og Oplà deler GameSession-låsen, og alle svar er kopierede øjebliksbilleder. Se [API-kontrakten](docs/api.md), [pointreglerne](docs/scoring.md) og [livscyklussen](docs/game-lifecycle.md).

**Historik fra #40:** Ved slutkontrollen bestod 612 tests og Release-build med 0 fejl og 0 advarsler. Afleveringsstatus og CI for den opgave dokumenteres i [PR #76](https://github.com/Bmantaz/H3-Yatzi/pull/76).

`/ws/opla` har samme transportgrænser i alle miljøer. READY giver PAIR, join returnerer joinedPlayerId og sender JOINED, og genforbindelse med samme kode bevarer spiller-id og parring. Lobbyen koordinerer HTTP/Oplà, første start og omkamp med ordnede køer: STATE efter kast/hold, STATE/WAIT ved turskifte og END ved afslutning. Kun den aktive deltagers aktuelle Oplà kan handle. HTTP kræver gameId, turnNumber og eget playerId; enkeltspiller bruger null. Browserens reserve virker uden egen Oplà. Omkamp kræver afsluttet lobbyspil, tilmeldt initiativtager og alle deltagere forbundne. /api/game/new kan fortsat ikke overskrive et startet lobbyspil.

**Historik fra #63/#59:** Ved slutkontrollen af #63 bestod 0 buildfejl, 0 advarsler og **794 tests**. En separat lokal Production-server bestod dengang transportprøven med en rigtig WebSocket-klient. Afleveringsstatus, merge og CI for #63 dokumenteres i [PR #80](https://github.com/Bmantaz/H3-Yatzi/pull/80). Kontrollen af #59 trin 2 bestod med **814 tests**, trin 3 med **889**, trin 4 med **946** og trin 5 med **993**. Kontrollen af trin 6 bestod med **1.038 tests**. Trin 7 bestod med **1.048 tests** og en separat lokal Production-prøve med to spillere og genforbindelse. Slutreviewet i trin 8 rettede ugyldige Unicode-escapes i lobbyinput og består med **1.052 tests**, 0 buildfejl/0 advarsler og en genkørt Production-prøve. Leverancen findes i [PR #81](https://github.com/Bmantaz/H3-Yatzi/pull/81), som dokumenterer merge-status, verificerede commits og CI før/efter merge. Første grønne PR-CI på 4fe624c bestod backend og Arduino. Færdigkriteriet for #59 er merge og grøn CI på main. Disse tal beskriver de tidligere leverancer. De aktuelle lokale resultater for #44 står nedenfor; Frederiks brugeroplyste USB-kontrol gentages ikke, og fysisk integration i #43 venter.

**Lokal slutkontrol af #44, 10. november 2026:** Alle otte lokale trin er gennemført. Release-build består med 0 fejl/0 advarsler, 1.379 backendtests og 39 frontendtests består. En rigtig Chrome-browser er kontrolleret via den aftalte headless/CDP-reservevej med separat testprofil, to deltagere med ens navne, tilskuer, slutresultat, omkamp og 390 px visning. Separate Production-prøver med 1, 2 og 6 deltagere består to hele spil og to omkampe pr. prøve. Fællessketchen med withCase er kompileret uden upload; lokal noCase er bevaret. Se [resultater og krav/test-matrix](docs/game-lifecycle.md#slutkontrol-af-44-i-trin-8). Resultaterne beskriver kontrollen før integrationen af PR #86. Se [ny kontrol før PR efter integration af main](docs/game-lifecycle.md#kontrol-før-pr-efter-integration-af-main). #44 afleveres via PR; merge og grøn CI på main er særskilte færdigkrav. #43 venter.

## Kom i gang

**Krav:** [.NET 8 SDK](https://dotnet.microsoft.com/download). Frontendtests bruger Node 22 uden npm-installation. Arduino-kompilering bruger Arduino IDE 2 eller Arduino CLI med MKR WiFi 1010-boardmiljøet samt Arduino_MKRIoTCarrier og WiFiNINA.

```bash
dotnet run --project backend/Yatzi.Api   # åbn http://localhost:5000
dotnet test H3Yatzi.sln --configuration Release
node --check frontend/app.js
node --test tests/frontend/app.test.cjs
```

`/ws/opla` fungerer i alle miljøer. Almindelig HTTP uden WebSocket-opgradering giver 400; fuldt forbindelsesregister giver 503. READY → PAIR → HTTP-join → JOINED binder spilleren. Start kræver 1–6 forbundne deltagere; ROLL/HOLD accepteres kun fra den aktive spiller. Browserhandlinger kræver JSON-kontekst, og omkamp bruger POST /api/lobby/restart efter afslutning. Se [lobbykontrakten](docs/lobby.md) og [API-kontrakten](docs/api.md).

Arduino: åbn `arduino/yatzi_controller/yatzi_controller.ino`, vælg board *Arduino MKR WiFi 1010* og upload.

## Sprints og ansvar

| Sprint | Periode | Mål |
|---|---|---|
| **Sprint 1 – Fundament** | tir 3. – ons 4. nov | Hver del virker for sig. Protokollen aftales tirsdag formiddag. |
| **Sprint 2 – Integration** | tor 5. – fre 6. nov | Mindste spilbare version hele vejen igennem. Integrationstest fredag. |
| **Sprint 3 – Færdigt spil + brugertest** | man 9. – tir 10. nov | Helt spil, brugertest mandag, demo tirsdag. |

| Område | Ansvarlig |
|---|---|
| Frontend + lobby-skærm | Frederik (@Juuul999) |
| Backend: spillogik, beskedhåndtering, `/ws/opla`, lobby, parring | Andreas (@Zikka1987) |
| Arduino – ryst, skærm, samlet sketch (`shake.cpp`, `.ino`) + browser-bro (Web Serial) og tunnel | Bjarke (@Bmantaz) |
| Arduino – terning-input/hold, LED'er, WiFi (`dice_input.cpp`) | Abdi (@AssisDk) |

Se **📋 Sprint-board** (fastgjort issue) for hvem der laver hvad lige nu.

## GitHub Actions

| Workflow | Hvad den gør |
|---|---|
| **CI** | Bygger/tester backend (.NET 8), kontrollerer frontendens syntaks/Node-tests og kompilerer Arduino-sketchen ved de konfigurerede push-/PR-kørsler. Frontend-TAP og backend-TRX gemmes som artifacts. |
| **Sync sprint-plan** | Opretter sprint-milestones og nye sprint-opgaver ud fra `.github/issues.json`, og sætter labels, milestone og ansvarlige på eksisterende. Kører automatisk når filen ændres på `main`. ⚠️ Titel og tekst på **eksisterende** issues overskrives ikke (så jeres flueben bevares) – ret dem også direkte på GitHub. |
| **Sprint-board** | Holder issuet **📋 Sprint-board** opdateret: hvem laver hvad, status pr. opgave (⚪ ikke startet · 🟡 i gang · 🔴 forsinket · ✅ færdig), DoD-fremskridt og backlog. Kører ved ændringer og hver hverdag kl. 07:30. |
| **PR → issue-status** | En PR med `Closes #12` markerer #12 som 🟡 i gang. Ved merge lukkes issuet automatisk. |

## Arbejdsgang

1. Tag dit næste issue på sprint-boardet.
2. Lav en branch: `git checkout -b S1-3-ryst-detektion`
3. Commit og push, og åbn en Pull Request med `Closes #<nummer>` i beskrivelsen.
4. `main` er beskyttet: alt skal ind via en PR. CI skal være grøn før merge.
5. Brug skabelonen **🧪 Brugertest** til feedback fra testpersoner (DoD 9).
6. Kort stand-up hver morgen: hvad lavede jeg, hvad laver jeg nu, hvad blokerer mig?
