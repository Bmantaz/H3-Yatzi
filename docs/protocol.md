# Kommunikationsprotokol – Oplà ⇄ server

> DoD 5. Aftalt i S1.1 af Bjarke, Andreas og Abdi. Ændringer i protokollen laves via PR og godkendes af alle tre.

## Overblik

- **Én Oplà og én PC pr. spiller.** Spillerne sidder **hver for sig** (forskellige hjem og netværk).
- **Én PC er vært** og kører serveren (`Yatzi.Api`). En **HTTPS-tunnel** gør den tilgængelig på internettet, fx `https://yatzi-xxx.ngrok-free.app`.
- Alle spillere – også værten – åbner tunnel-adressen i browseren og joiner **lobbyen**.
- Man spiller **på skift** (klassisk Yatzy). Kun Oplaen hos den spiller, der har tur, kan kaste og holde.
- **Det er ligegyldigt, om Oplaen er forbundet med USB eller WiFi.** Serveren har ét endpoint til Oplà'er – WebSocket `/ws/opla` – og får de samme beskeder begge veje.

```
 Spiller A (hjemme)                                     Værtens PC
 ┌─────────────────────────┐                          ┌───────────────────────┐
 │ Browser (Chrome/Edge)   │                          │                       │
 │  lobby + spil           │◄──── HTTPS ─────┐        │                       │
 │  browser-bro ───────────│◄──── WSS ───┐   │        │  Server (Yatzi.Api)   │
 │        ▲ Web Serial     │             │   │        │  lobby, spil, point   │
 └────────┼────────────────┘             ▼   ▼        │                       │
      USB │                         ┌──────────────┐  │  /ws/opla  (Oplà'er)  │
 ┌────────┴──┐                      │ HTTPS-tunnel │◄►│  /api      (browsere) │
 │ Oplà A    │                      │   (ngrok)    │  │                       │
 └───────────┘                      └──────────────┘  └───────────────────────┘
 Spiller B (hjemme)                      ▲   ▲
 ┌───────────┐                           │   │
 │ Oplà B    │──── WiFi, WSS ────────────┘   │
 └───────────┘                               │
 Browser B (lobby + spil) ──── HTTPS ────────┘
```

## Forbindelser

**Én model:** al Oplà-trafik går via WebSocket `/ws/opla` på serveren. Serveren læser aldrig selv en USB-port.

| Oplaen er forbundet med | Vej til serveren |
|---|---|
| **USB** (115200 baud) | Oplà → spillerens browser (Web Serial) → `wss://<tunnel>/ws/opla`. Browseren videresender linjerne uændret. |
| **WiFi** | Oplà → `wss://<tunnel>/ws/opla` direkte (`WiFiSSLClient` + `ArduinoHttpClient`). |

- **Format:** én besked pr. linje (pr. WebSocket-besked), UTF-8-tekst. Over USB afsluttes linjer med `\n` (`\r\n` accepteres). Kommandoer med store bogstaver, felter adskilt af ét mellemrum.
- **Hvorfor tekstlinjer:** kan læses i Serial Monitor og testes med fx `websocat wss://<tunnel>/ws/opla`, så hver del kan testes alene ved at skrive beskederne i hånden.
- **Hvorfor WebSocket:** virker gennem en HTTPS-tunnel og over internettet, går begge veje (serveren kan selv sende `STATE`), og browseren og Oplaen kan bruge samme endpoint.
- **Hvorfor tunnel:** værtens PC har en privat IP, som de andre ikke kan nå hjemmefra. Tunnelen giver en offentlig HTTPS-adresse uden at åbne routeren. HTTPS er samtidig et krav for Web Serial.
- **Hvorfor ikke MQTT/TCP:** ville kræve en offentlig broker eller åbne porte. WebSocket kører over den samme tunnel som resten af spillet.

### Rækkefølge

1. **S1.10 (onsdag):** spike – virker Web Serial + tunnel fra et andet netværk?
2. **Fredag (S2.8):** integrationstest hjemmefra, alle over **USB** via browser-broen.
3. **Mandag (S3.9, S3.10):** WiFi direkte fra Oplaen.

### Oplaen vælger selv (når WiFi er med)

1. Ved opstart: prøv WiFi og WebSocket til serveren (højst ca. 10 sek.).
2. Lykkes det, bruges **WiFi**. Ellers **USB** (browser-broen).
3. Mister den WiFi, skifter den til USB og prøver WiFi igen hvert 5. sek.
4. Den sender **kun på én vej ad gangen**, så serveren aldrig får det samme kast to gange.

### Opsætning

- **Værten:** kør serveren og `ngrok http --url=<fast-adresse> 5000`. Ngrok er gratis og giver én fast adresse pr. konto, så Oplà'erne ikke skal have ny adresse hver gang. (Detaljer skrives ind efter S1.10.)
- **Spillerne:** åbn tunnel-adressen i **Chrome eller Edge** (Web Serial virker ikke i Firefox/Safari). Klik "Forbind Oplà (USB)" og vælg porten.
- **WiFi:** netværksnavn, kode og tunnel-adressen skrives i `arduino/yatzi_controller/arduino_secrets.h`. Filen er **ikke** i git. Kopiér `arduino_secrets.example.h` og udfyld den.

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
| `# …` | Debug, ignoreres. | – | `# forbundet` |

## Eksempel: to spillere

Bjarke spiller over USB (browser-broen), Andreas over WiFi. De sidder hver for sig.

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
   … Bjarke vælger felt i browseren
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
| WiFi (WSS til `/ws/opla`), `READY cccc` ved genforbindelse | Abdi | S3.9 |
| **Server** | | |
| Beskedhåndtering: linje ind → svar ud | Andreas | S2.1 |
| Parring og lobby (`PAIR`, `JOINED`, lobby-API) | Andreas | S2.9 |
| Tur-styring (`STATE`, `WAIT`, `END`) | Andreas | S3.1 |
| WiFi-Oplà'er på `/ws/opla` via tunnel | Andreas | S3.10 |
| **Browser og tunnel** | | |
| Spike: Web Serial + tunnel fra andet netværk | Bjarke | S1.10 |
| Browser-bro + WebSocket `/ws/opla` | Bjarke | S2.11 |
| Lobby-skærm (navn + kode, start spil) | Frederik | S2.10 |

## Dataflow

```
Oplà ──WiFi──────────────────────────── WSS /ws/opla ──┐
Oplà ──USB──► spillerens browser (Web Serial) ── WSS ──┤──► tunnel ──► server (Yatzi.Api + Yatzi.Core)
                                                       │                    │
alle browsere ◄──── HTTPS /api + live-opdatering ──────┘◄───────────────────┘
```

_Live-opdatering (polling eller SignalR) besluttes i S2.7, og dataflowet opdateres i S3.7._
