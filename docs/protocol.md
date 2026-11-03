# Kommunikationsprotokol – Oplà ⇄ server

> DoD 5. Aftalt i S1.1 af Bjarke, Andreas og Abdi. Ændringer i protokollen laves via PR og godkendes af alle tre.

## Overblik

- **Én Oplà pr. spiller, og hver spiller har sin egen PC.**
- **Én PC er vært** og kører serveren (`Yatzi.Api`). Alle spillere – også værten – åbner spillet i browseren (`http://<værtens-ip>:5000`) og joiner **lobbyen**.
- Man spiller **på skift** (klassisk Yatzy). Alle ser det samme spil på hver sin skærm. Kun Oplaen hos den spiller, der har tur, kan kaste og holde.
- **Det er ligegyldigt, om Oplaen er forbundet med USB eller WiFi.** Serveren får præcis de samme beskeder begge veje og kan ikke se forskel.

```
 Spiller A's PC                            Værtens PC
 ┌───────────────────────┐                 ┌──────────────────────────┐
 │ Browser (Web Serial)  │◄── WebSocket ──►│                          │
 │        ▲              │                 │  Server (Yatzi.Api)      │
 └────────┼──────────────┘                 │  lobby + spil + point    │
     USB  │                                │                          │
 ┌────────┴──┐                             │                          │
 │ Oplà A    │                             │                          │
 └───────────┘                             │                          │
 ┌───────────┐                             │                          │
 │ Oplà B    │◄────── WiFi, TCP 5050 ─────►│                          │
 └───────────┘                             └──────────────────────────┘
   (Spiller B's browser viser bare spillet)
```

## Forbindelser

Samme tekstlinjer på alle tre veje:

| Vej | Oplà-side | Server-side |
|---|---|---|
| **WiFi** | `WiFiClient` (WiFiNINA) forbinder til værtens IP, port **5050** | `TcpListener` på port 5050 |
| **USB** | `Serial`, 115200 baud | – |
| ↳ **browser-bro** | Spillerens browser læser USB med Web Serial og videresender linjerne uændret | WebSocket `/ws/opla` |
| ↳ **direkte i værten** | Oplaen sidder i USB på værtens egen PC | `SerialPort` (COM-port i `appsettings.json`) |

- **Format:** én besked pr. linje, UTF-8-tekst, afsluttet med `\n` (`\r\n` accepteres). Kommandoer med store bogstaver, felter adskilt af ét mellemrum.
- **Hvorfor tekstlinjer:** kan læses direkte i Serial Monitor eller med `telnet <værtens-ip> 5050`, så hver del kan testes alene ved at skrive beskederne i hånden.
- **Hvorfor TCP til WiFi:** samme linjer begge veje som USB, ingen broker (MQTT) og serveren kan selv sende til Oplaen (det kan HTTP ikke).
- **Hvorfor browser-bro til USB:** Oplaen sidder i spillerens egen PC, ikke i serveren. Browseren videresender, så spilleren ikke skal installere noget.

### Rækkefølge

USB først: fredagens integrationstest (S2.8) kører over USB – én Oplà direkte i værten og én via browser-broen. WiFi kommer i Sprint 3 (S3.9 og S3.10).

### Oplaen vælger selv (når WiFi er med)

1. Ved opstart: prøv WiFi og TCP til serveren (højst ca. 10 sek.).
2. Lykkes det, bruges **WiFi**. Ellers **USB**.
3. Mister den WiFi, skifter den til USB og prøver WiFi igen hvert 5. sek.
4. Den sender **kun på én vej ad gangen**, så serveren aldrig får det samme kast to gange.

### Opsætning

- **WiFi:** netværksnavn, kode og værtens IP skrives i `arduino/yatzi_controller/arduino_secrets.h`. Filen er **ikke** i git. Kopiér `arduino_secrets.example.h` og udfyld den.
- **Værtens PC:** Windows Firewall skal tillade indgående TCP 5050 (Oplà) og 5000 (browsere).
- **Netværk:** alle PC'er og Oplà'er skal være på samme netværk. Blokerer skolens net trafik mellem enheder, så brug et mobil-hotspot.
- **USB / Web Serial:** virker kun i **Chrome eller Edge** og kun på en "sikker" side. Siden fra værten (`http://<ip>:5000`) tæller ikke som sikker, så enten
  - kører serveren HTTPS, eller
  - hver spiller tilføjer værtens adresse i `chrome://flags/#unsafely-treat-insecure-origin-as-secure`.

  ⚠️ Afprøves i S1.10 (onsdag), før vi bygger videre på det. Virker det ikke, er reserveplanen et lille bro-program på spillerens PC.

## Parring: hvilken Oplà hører til hvilken spiller?

1. Oplaen forbinder og sender `READY`.
2. Serveren svarer `PAIR 4821` – en unik 4-cifret kode. Oplaen viser koden på den runde skærm.
3. I lobbyen skriver spilleren sit navn og koden. Serveren sender `JOINED Bjarke`, og Oplaen viser navnet.
   - **Over USB** kender browseren allerede koden (den videresender jo `PAIR`), så feltet udfyldes automatisk.
4. **Genforbindelse:** Oplaen husker sin kode og sender `READY 4821`. Serveren genkender den, og spilleren fortsætter uden at joine igen. Det gælder også, hvis Oplaen skifter fra WiFi til USB midt i spillet.

## Grundregler

1. **Serveren bestemmer.** Oplaen sender kun *hvad spilleren gjorde* (ryst, tryk). Serveren slår terningerne, tæller slag, holder styr på holdte terninger og hvis tur det er.
2. **Serveren sender altid status**, når noget ændrer sig for en Oplà. Oplaen sætter LED'er og skærm ud fra det – ikke ud fra sit eget gæt.
3. **Ugyldige beskeder ignoreres og logges**: `ROLL`/`HOLD` når det ikke er ens tur, `ROLL` efter 3. slag, `HOLD` før første slag, ukendt tekst. Oplaen får intet svar.
4. **Linjer der starter med `#` er debug** og ignoreres af modtageren (også af browser-broen). Oplaen skriver altid debug på USB (`Serial`), også når den spiller over WiFi, så Serial Monitor/Plotter virker stadig (fx accelerometer-data i S1.2).

## Oplà → server

| Besked | Betydning | Eksempel |
|--------|-----------|----------|
| `READY` | Oplaen er forbundet første gang og har ingen kode. | `READY` |
| `READY cccc` | Oplaen er genforbundet og har koden *cccc*. | `READY 4821` |
| `ROLL` | Spilleren har rystet = ét kast. Sendes præcis én gang pr. ryst (S1.3). | `ROLL` |
| `HOLD n` | Tryk på touch-knap *n* (1–5). Skifter terning *n* mellem holdt og ikke holdt. | `HOLD 3` |
| `# …` | Debug, ignoreres. | `# acc 0.02 -0.98 0.11` |

## Server → Oplà

| Besked | Betydning | Oplaen viser | Eksempel |
|--------|-----------|--------------|----------|
| `PAIR cccc` | Din kode. Ikke joinet endnu. | Koden på skærmen | `PAIR 4821` |
| `JOINED navn` | Parret med spilleren *navn* (resten af linjen). | Navnet | `JOINED Bjarke` |
| `WAIT navn` | Det er ikke din tur – *navn* har tur. LED'er slukket. | "Vent – Andreas" | `WAIT Andreas` |
| `STATE r hhhhh` | Det er **din tur**. *r* = brugte slag (0–3). *hhhhh* = terning 1–5, `1` = holdt. | "Slag r/3" + grønne LED'er | `STATE 2 10100` |
| `END` | Spillet er slut. | "Slut" | `END` |
| `# …` | Debug, ignoreres. | – | `# forbundet via TCP` |

## Eksempel: to spillere

Bjarke spiller over USB, Andreas over WiFi.

```
Oplà B (USB)   → READY
               ← PAIR 4821              skærm: "4821"
Oplà A (WiFi)  → READY
               ← PAIR 1377              skærm: "1377"
   … Bjarke joiner lobbyen (koden udfyldes automatisk via USB)
               ← JOINED Bjarke          (til Oplà B)
   … Andreas joiner med kode 1377
               ← JOINED Andreas         (til Oplà A)
   … værten trykker "Start spil"
Oplà B         ← STATE 0 00000          "Slag 0/3" – Bjarkes tur
Oplà A         ← WAIT Bjarke
Oplà A         → ROLL                   ignoreres – ikke Andreas' tur
Oplà B         → ROLL
               ← STATE 1 00000
Oplà B         → HOLD 2
               ← STATE 1 01000          grøn LED ved knap 2
   … Bjarke vælger felt på sin PC
Oplà B         ← WAIT Andreas
Oplà A         ← STATE 0 00000          "Slag 0/3" – Andreas' tur
   … osv. til alle felter er udfyldt
Oplà A og B    ← END
```

## Hvem bruger hvad

| Del | Ansvarlig | Opgave |
|-----|-----------|--------|
| **Oplà** | | |
| Sender `ROLL` | Bjarke | S1.3 (`shake.cpp`) |
| Sender `HOLD n` | Abdi | S1.4 (`dice_input.cpp`) |
| Samler sketchen, sender `READY`, læser server-beskeder | Bjarke | S2.2 (`yatzi_controller.ino`) |
| LED'er ud fra `STATE`/`WAIT` | Abdi | S2.3 (`showHeldDice`) |
| Skærm: kode, navn, "Vent – navn", "Slag x/3" | Bjarke | S2.4 |
| WiFi, `READY cccc` ved genforbindelse | Abdi | S3.9 |
| **Server** | | |
| Seriel-port i værtens PC + fælles beskedhåndtering | Andreas | S2.1 |
| Parring og lobby (`PAIR`, `JOINED`, lobby-API) | Andreas | S2.9 |
| Tur-styring (`STATE`, `WAIT`, `END`) | Andreas | S3.1 |
| TCP 5050 (WiFi) | Andreas | S3.10 |
| **Browser** | | |
| Web Serial – virker det? | Bjarke | S1.10 (spike) |
| Browser-bro + WebSocket `/ws/opla` | Bjarke | S2.11 |
| Lobby-skærm (navn + kode, start spil) | Frederik | S2.10 |

## Dataflow

```
Oplà ──WiFi TCP 5050──────────────────────┐
Oplà ──USB──► spillerens browser ──WS─────┤──► server (Yatzi.Api + Yatzi.Core)
                                          │        │
alle browsere ◄──HTTP /api + live-opdatering┘◄───────┘
```

_Live-opdatering (polling eller SignalR) besluttes i S2.7, og dataflowet opdateres i S3.7._
