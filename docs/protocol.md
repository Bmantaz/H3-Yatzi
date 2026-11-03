# Kommunikationsprotokol – Oplà ⇄ backend

> DoD 5. Aftalt i S1.1 af Bjarke, Andreas og Abdi. Ændringer i protokollen laves via PR og godkendes af alle tre.

## Valg af protokol

- **Transport:** USB-seriel, **115200 baud**, 8N1.
- **Format:** én besked pr. linje, ren ASCII-tekst, afsluttet med `\n` (backend accepterer også `\r\n`). Store bogstaver, felter adskilt af ét mellemrum.
- **Hvorfor:**
  - Oplaen sidder alligevel i PC'en med USB-kabel – ingen WiFi, broker eller netværksopsætning, der kan fejle til demoen.
  - Lav og stabil forsinkelse; vi sender kun få korte beskeder.
  - Tekstlinjer kan læses direkte i Serial Monitor, så vi kan teste Oplaen uden backend og backend uden Oplà (skriv beskederne i hånden).
  - Let at læse i C# med `System.IO.Ports.SerialPort.ReadLine()` og i Arduino med `Serial.readStringUntil('\n')`.

## Grundregler

1. **Backend bestemmer.** Oplaen sender kun *hvad spilleren gjorde* (ryst, tryk). Backend slår terningerne, tæller slag og holder styr på holdte terninger.
2. **Backend svarer altid med `STATE`**, når en gyldig besked har ændret noget. Oplaen sætter LED'er og skærm ud fra `STATE` – ikke ud fra sin egen gæt.
3. **Ugyldige beskeder ignoreres og logges** (fx `ROLL` efter 3. slag, `HOLD` før første slag, ukendt tekst). Oplaen får intet svar og skal ikke gøre noget.
4. **Linjer der starter med `#` er debug** og ignoreres af modtageren. Brug dem til fx accelerometer-data (S1.2), så log og protokol kan køre samtidig.

## Oplà → backend

| Besked | Betydning | Eksempel |
|--------|-----------|----------|
| `READY` | Oplaen er startet. Backend svarer med den aktuelle `STATE`. | `READY` |
| `ROLL` | Spilleren har rystet = ét kast. Sendes præcis én gang pr. ryst (S1.3). | `ROLL` |
| `HOLD n` | Tryk på touch-knap *n* (1–5). Skifter terning *n* mellem holdt og ikke holdt. | `HOLD 3` |
| `# …` | Debug, ignoreres af backend. | `# acc 0.02 -0.98 0.11` |

## Backend → Oplà

| Besked | Betydning | Eksempel |
|--------|-----------|----------|
| `STATE r hhhhh` | *r* = antal brugte slag i turen (0–3). *hhhhh* = fem tegn for terning 1–5: `1` = holdt, `0` = ikke holdt. | `STATE 2 10100` |
| `# …` | Debug, ignoreres af Oplaen. | `# port åbnet` |

Ny tur (næste spiller) er bare `STATE 0 00000` – Oplaen slukker alle LED'er og viser "Slag 0/3".

## Eksempel på en tur

```
Oplà → backend   READY
backend → Oplà   STATE 0 00000
Oplà → backend   ROLL              (ryst)
backend → Oplà   STATE 1 00000     skærm: "Slag 1/3"
Oplà → backend   HOLD 1            (tryk knap 1)
backend → Oplà   STATE 1 10000     grøn LED ved knap 1
Oplà → backend   HOLD 3
backend → Oplà   STATE 1 10100
Oplà → backend   ROLL
backend → Oplà   STATE 2 10100     skærm: "Slag 2/3"
Oplà → backend   ROLL
backend → Oplà   STATE 3 10100     skærm: "Slag 3/3"
Oplà → backend   ROLL              ignoreres – ingen slag tilbage
   … spilleren vælger felt på PC'en …
backend → Oplà   STATE 0 00000     ny tur
```

## Hvem bruger hvad

| Del | Ansvarlig | Opgave |
|-----|-----------|--------|
| Sender `ROLL` | Bjarke | S1.3 (`shake.cpp`) |
| Sender `HOLD n` | Abdi | S1.4 (`dice_input.cpp`) |
| Sender `READY`, læser `STATE` | Bjarke | S2.2 (`yatzi_controller.ino`) |
| LED'er ud fra `STATE` | Abdi | S2.3 (`showHeldDice`) |
| "Slag x/3" ud fra `STATE` | Bjarke | S2.4 |
| Læser `READY`/`ROLL`/`HOLD`, sender `STATE` | Andreas | S2.1 (seriel-læser i backend) |

## Dataflow

```
Oplà ──USB-seriel (ROLL / HOLD n)──▶ backend (Yatzi.Api + Yatzi.Core) ──HTTP /api/game──▶ frontend
Oplà ◀──USB-seriel (STATE r hhhhh)── backend
```

_Opdateres i S3.7, når vi ved om frontend bruger polling eller SignalR (S2.7)._
