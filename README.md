# H3 Yatzi 🎲

[![CI](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml/badge.svg)](https://github.com/Bmantaz/H3-Yatzi/actions/workflows/ci.yml)

Mellemprojekt: et digitalt Yatzy-spil, hvor **Arduino Oplà** (MKR WiFi 1010) bruges som raflebæger – ryst for at kaste, touch 1–5 for at holde terninger. Terninger og pointtavle vises på PC'en.

- 📄 [Game Design Document](docs/GDD.md)
- ✅ [Definition of Done & Nice to Have](docs/DoD.md)
- 🔌 [Protokol Oplà ⇄ backend](docs/protocol.md) _(udfyldes af gruppen)_

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

Alle filer er skabeloner med `TODO`-kommentarer – selve spillet bygger I.

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
| Frontend | Frederik (@Juuul999) |
| Backend | Andreas (@Zikka1987) |
| Arduino – ryst (`shake.cpp`) | Bjarke (@Bmantaz) |
| Arduino – terning-input/hold (`dice_input.cpp`) | Abdi (@AssisDk) |

Se **📋 Sprint-board** (fastgjort issue) for hvem der laver hvad lige nu.

## GitHub Actions

| Workflow | Hvad den gør |
|---|---|
| **CI** | Bygger og tester backend (.NET 8) og kompilerer Arduino-sketchen ved hvert push og PR. |
| **Sync sprint-plan** | Opretter/opdaterer sprint-milestones, sprint-opgaver med ansvarlige og deadlines ud fra `.github/issues.json`. Kører automatisk når filen ændres på `main` – ret planen der. |
| **Sprint-board** | Holder issuet **📋 Sprint-board** opdateret: hvem laver hvad, status pr. opgave (⚪ ikke startet · 🟡 i gang · 🔴 forsinket · ✅ færdig), DoD-fremskridt og backlog. Kører ved ændringer og hver hverdag kl. 07:30. |
| **PR → issue-status** | En PR med `Closes #12` markerer #12 som 🟡 i gang. Ved merge lukkes issuet automatisk. |

## Arbejdsgang

1. Tag dit næste issue på sprint-boardet.
2. Lav en branch: `git checkout -b S1-3-ryst-detektion`
3. Commit og push, og åbn en Pull Request med `Closes #<nummer>` i beskrivelsen.
4. `main` er beskyttet: alt skal ind via en PR. CI skal være grøn før merge.
5. Brug skabelonen **🧪 Brugertest** til feedback fra testpersoner (DoD 9).
6. Kort stand-up hver morgen: hvad lavede jeg, hvad laver jeg nu, hvad blokerer mig?
