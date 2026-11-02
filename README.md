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

## GitHub Actions

| Workflow | Hvad den gør |
|---|---|
| **CI** | Bygger og tester backend (.NET 8) og kompilerer Arduino-sketchen ved hvert push og PR. |
| **Seed labels, milestones & issues** | Kør manuelt (Actions → Run workflow). Opretter labels, milestones og issues fra `.github/issues.json`. Kan køres igen uden dubletter. |
| **DoD progress** | Holder issuet **📋 DoD-status** opdateret, når issues åbnes og lukkes. |

## Arbejdsgang med issues

1. Vælg et issue og tildel det til dig selv (**Assignees**).
2. Lav en branch: `git checkout -b 12-ryst-registrering`
3. Commit og push, og åbn en Pull Request med `Closes #12` i beskrivelsen – så lukkes issuet automatisk ved merge.
4. CI skal være grøn, og en anden i gruppen reviewer, før der merges.
5. Brug skabelonen **🧪 Brugertest** til feedback fra testpersoner (DoD 9).

**Labels:** `dod`, `nth`, `spike`, `arduino`, `backend`, `frontend`, `communication`, `docs`, `test`, `user-test`
**Milestones:** *DoD – Mellemprojekt*, *Spikes – Det usikre*, *Nice to Have*
