# H3 Yatzi 🎲

[![CI](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml/badge.svg)](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml)

Mellemprojekt: et digitalt Yatzy-spil, hvor **Arduino Oplà** (MKR WiFi 1010) bruges som raflebæger – ryst for at kaste, touch 1–5 for at holde terninger. Hver spiller har sin egen Oplà og PC og joiner en fælles lobby; terninger og pointtavle vises i browseren.

- 📄 [Game Design Document](docs/GDD.md)
- ✅ [Definition of Done & Nice to Have](docs/DoD.md)
- 🔌 [Protokol Oplà ⇄ server](docs/protocol.md) – USB + WiFi, lobby og parring
- 🌐 [HTTP API-kontrakt](docs/api.md) – requests, spiltilstand og fejl for opgave #33

## Struktur

```
arduino/yatzi_controller/   Sketch til Oplaen
backend/Yatzi.Core/         Spillogik (regler, point, ture)
backend/Yatzi.Api/          ASP.NET Core backend (API + serverer frontend)
backend/Yatzi.Tests/        xUnit-tests
frontend/                   HTML/CSS/JS
docs/                       GDD, DoD, protokol
.github/                    Actions, issue-skabeloner, issue-seed-data
```

Backendens terningelogik og de fire spil-endpoints er implementeret og testet. Frontenden bruger fortsat mock-data. Lobby, pointberegning, turskifte og den samlede Oplà-integration følger i de næste sprintopgaver. Se [API-kontrakten](docs/api.md) for requests, spiltilstand og fejl.

## Kom i gang

**Krav:** [.NET 8 SDK](https://dotnet.microsoft.com/download) og Arduino IDE 2 med biblioteket `Arduino_MKRIoTCarrier`.

```bash
dotnet run --project backend/Yatzi.Api   # åbn http://localhost:5000
dotnet test                              # kør tests
```

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
| **CI** | Bygger og tester backend (.NET 8) og kompilerer Arduino-sketchen ved hvert push og PR. |
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
