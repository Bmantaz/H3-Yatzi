# Kommunikationsprotokol – Oplà ⇄ backend

> DoD 5. Aftalt i S1.1 af Bjarke, Andreas og Abdi. Ændringer i protokollen laves via PR og godkendes af alle tre.

## Valg af protokol

Oplaen kan forbindes til backend på **to måder**, og de bruger **præcis de samme beskeder**:

| | USB-seriel | WiFi (TCP-socket) |
|---|---|---|
| Forbindelse | USB-kabel i PC'en | Oplà og PC på samme netværk |
| Indstillinger | 115200 baud, 8N1 | Backend lytter på **TCP-port 5050** |
| Oplà-side | `Serial` | `WiFiClient` (biblioteket WiFiNINA) |
| Backend-side | `System.IO.Ports.SerialPort` | `System.Net.Sockets.TcpListener` |
| Fordel | Simpelt, stabilt, virker altid | Ingen ledning – Oplaen kan sendes rundt ved bordet |

- **Format:** én besked pr. linje, ren ASCII-tekst, afsluttet med `\n` (backend accepterer også `\r\n`). Store bogstaver, felter adskilt af ét mellemrum.
- **Hvorfor tekstlinjer:** de kan læses direkte i Serial Monitor (USB) eller med `telnet <pc-ip> 5050` (WiFi), så vi kan teste Oplaen uden backend og backend uden Oplà ved at skrive beskederne i hånden.
- **Hvorfor TCP og ikke MQTT eller HTTP:** TCP sender de samme linjer som USB begge veje, så beskedkoden er fælles og kun forbindelsen er forskellig. MQTT kræver en broker, og med HTTP kan backend ikke selv sende `STATE` til Oplaen.
- **Hvorfor begge:** USB er vores sikre løsning til demoen. WiFi giver den rigtige raflebæger-oplevelse, men skolens netværk kan drille – så kan vi altid falde tilbage til kablet.

## Valg af forbindelse

**Oplaen** (ved opstart):

1. Prøv at forbinde til WiFi og derefter til backend på port 5050 (højst ca. 10 sek.).
2. Lykkes det: brug **WiFi**. Ellers: brug **USB**.
3. Mister den WiFi-forbindelsen, skifter den til USB og prøver WiFi igen hvert 5. sek.
4. Den sender **kun på én forbindelse ad gangen**, så backend aldrig får det samme kast to gange.
5. Efter hver (gen)forbindelse sender den `READY`.

**Backend** lytter på **både** seriel-porten og TCP-port 5050 hele tiden og sender `STATE` til alle forbundne. Seriel-porten (fx `COM3`) sættes i `appsettings.json`; er den tom, bruges kun WiFi.

**Opsætning af WiFi:**

- WiFi-navn, kode og PC'ens IP skrives i `arduino/yatzi_controller/arduino_secrets.h`. Filen er **ikke** i git (koden må ikke ligge offentligt). Kopiér `arduino_secrets.example.h` og udfyld den.
- Windows Firewall skal tillade indgående TCP på port 5050 for backend.
- Mange skolenetværk blokerer trafik mellem enheder. Virker det ikke, så brug et mobil-hotspot, som både PC og Oplà forbinder til – eller USB.

## Grundregler

1. **Backend bestemmer.** Oplaen sender kun *hvad spilleren gjorde* (ryst, tryk). Backend slår terningerne, tæller slag og holder styr på holdte terninger.
2. **Backend svarer altid med `STATE`**, når en gyldig besked har ændret noget. Oplaen sætter LED'er og skærm ud fra `STATE` – ikke ud fra sit eget gæt.
3. **Ugyldige beskeder ignoreres og logges** (fx `ROLL` efter 3. slag, `HOLD` før første slag, ukendt tekst). Oplaen får intet svar og skal ikke gøre noget.
4. **Linjer der starter med `#` er debug** og ignoreres af modtageren. Oplaen skriver altid debug på USB (`Serial`), også når den spiller over WiFi – så Serial Monitor/Plotter virker stadig (fx accelerometer-data i S1.2).

## Oplà → backend

| Besked | Betydning | Eksempel |
|--------|-----------|----------|
| `READY` | Oplaen er (gen)forbundet. Backend svarer med den aktuelle `STATE`. | `READY` |
| `ROLL` | Spilleren har rystet = ét kast. Sendes præcis én gang pr. ryst (S1.3). | `ROLL` |
| `HOLD n` | Tryk på touch-knap *n* (1–5). Skifter terning *n* mellem holdt og ikke holdt. | `HOLD 3` |
| `# …` | Debug, ignoreres af backend. | `# acc 0.02 -0.98 0.11` |

## Backend → Oplà

| Besked | Betydning | Eksempel |
|--------|-----------|----------|
| `STATE r hhhhh` | *r* = antal brugte slag i turen (0–3). *hhhhh* = fem tegn for terning 1–5: `1` = holdt, `0` = ikke holdt. | `STATE 2 10100` |
| `# …` | Debug, ignoreres af Oplaen. | `# forbundet via TCP` |

Ny tur (næste spiller) er bare `STATE 0 00000` – Oplaen slukker alle LED'er og viser "Slag 0/3".

## Eksempel på en tur

Ens for USB og WiFi:

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
| Forbindelse USB/WiFi, sender `READY`, læser `STATE` | Bjarke | S2.2 (`yatzi_controller.ino`) |
| LED'er ud fra `STATE` | Abdi | S2.3 (`showHeldDice`) |
| "Slag x/3" ud fra `STATE` | Bjarke | S2.4 |
| Lytter på seriel + TCP 5050, sender `STATE` | Andreas | S2.1 (seriel-/TCP-læser i backend) |

## Dataflow

```
                 USB-seriel ─┐
Oplà ── ROLL / HOLD n ──────►├──► backend (Yatzi.Api + Yatzi.Core) ──HTTP /api/game──► frontend
     ◄── STATE r hhhhh ──────┤
                 WiFi TCP ───┘
```

_Opdateres i S3.7, når vi ved om frontend bruger polling eller SignalR (S2.7)._
