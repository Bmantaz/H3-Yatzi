# WiFi-Oplà via tunnel – specifikation for #54

**Status 10. november 2026:** Step 1 er dokumenteret, step 2 er implementeret,
step 3 dækker gentagne forbindelsesskift i softwaretests, step 4 består lokalt
over TCP, og step 5 består over offentlig HTTPS/WSS gennem ngrok mod Production til
[#54 – Backend: WiFi-Oplà'er på /ws/opla via tunnel](https://github.com/Bmantaz/H3-Yatzi/issues/54).
[appsettings.json](../backend/Yatzi.Api/appsettings.json) indeholder nu en tom
`Tunnel:PublicUrl`, og opstarten validerer værdien og logger de konfigurerede
adresser. Step 2 består med 49 nye tests og tre isolerede procesprøver. Step 3
tilføjer seks beståede cases for gentagne overtagelser; den samlede
backendtest i step 3 bestod med 1.434 tests. Step 4 består med 55 kontrolpunkter
over rigtig HTTP/WebSocket og dokumenteret proces-/portoprydning. Step 5 består
med de samme otte forløb, 55 kontrolpunkter og normal TLS-certifikatvalidering.
#54 er fortsat åben; fysisk Oplà-/certifikattest og fællestest udestår.
Step 6's forberedelse består med prøvevært, hardwarevejledning, testprotokol
og Arduino-kompilering uden upload. Step 6 er fortsat åbent til fysisk test.
Step 7A's foreløbige review og friske kontroller består med 1.434 backendtests,
39 frontendtests og kompilering af fællessketchen. Produktionskoden er bevaret
i step 3–7A. Step 7's endelige godkendelse afventer step 6's fysiske evidens.

Efter brugerens beslutning i step 8 leveres softwaren til main før den planlagte
online fællestest med fysisk udstyr. Software-PR'en bruger `Refs #54`; #54
forbliver åben. Merge og grøn CI godkender softwareleverancen, mens step 6 og
step 7's endelige godkendelse fortsat kræver fysisk evidens.

Se [manuel opsætning uden lokale prøveværktøjer](#manuel-opsaetning) for at
starte backend, tunnel og frontend fra en almindelig checkout.

Målet er, at en Oplà over WiFi og en Oplà over USB-browserbroen bruger samme
offentlige endpoint, samme beskedhåndtering og samme spil. Den eksisterende
[kommunikationsprotokol](protocol.md), [WebSocket-kontrakt](opla-websocket.md) og
[lobbykontrakt](lobby.md) er grundlaget.

## Eksisterende grundlag og udestående arbejde

- #54's afhængigheder er [#27](https://github.com/Bmantaz/H3-Yatzi/issues/27),
  [#36](https://github.com/Bmantaz/H3-Yatzi/issues/36) og
  [#63](https://github.com/Bmantaz/H3-Yatzi/issues/63); de er afsluttet.
- [#53](https://github.com/Bmantaz/H3-Yatzi/issues/53) er lukket, og WiFi/USB-koden
  fra [PR #86](https://github.com/Bmantaz/H3-Yatzi/pull/86) ligger på main. PR'en
  dokumenterer fortsat ikke en gennemført fysisk WiFi-tur, USB-fallback eller
  fællestest med Andreas. De kontroller er derfor udestående.
- [#46](https://github.com/Bmantaz/H3-Yatzi/issues/46) afhænger af #43, #53 og #54.
  Den blokerer ikke starten på #54; længere stabilitetsprøver og rystefinjustering
  hører til #46.
- [#43](https://github.com/Bmantaz/H3-Yatzi/issues/43) har fortsat sine egne
  udestående krav til fysisk USB-integration med to spillere hjemmefra.

Genforbindelse og overtagelse findes allerede i backend. Ved undersøgelsen før
step 1 bestod 81 fokuserede backendtests. Det er et tidligere softwarekontrolresultat,
ikke dokumentation for en fysisk Oplà, en offentlig WSS-tunnel eller fællestesten.
I step 1 blev backendtests ikke genkørt, fordi leverancen kun var dokumentation.
Resultaterne for step 2–7A står i kontrolafsnittene nedenfor. Trin 7A er et
foreløbigt softwarereview; den fysiske del af trin 6 er fortsat udestående.

## Konfigurationskontrakt til step 2

### Nøgle og lokale kilder

`Tunnel:PublicUrl` indeholder tunnelens offentlige HTTPS-baseadresse. Standarden
i `appsettings.json` er en tom streng. Dette JSON-udsnit viser den tilføjede del;
de eksisterende indstillinger for logging og lokal lytteadresse er bevaret:

```json
{
  "Tunnel": {
    "PublicUrl": ""
  }
}
```

Værten kan angive sin faktiske adresse i
`backend/Yatzi.Api/appsettings.Development.json`, som allerede er Git-ignoreret.
Filen indlæses i Development-miljøet. Følgende er kun et eksempel med et
eksempelværtsnavn, der ikke er en aktiv tunnel:

```json
{
  "Tunnel": {
    "PublicUrl": "https://yatzi.example.invalid"
  }
}
```

Miljøvariablen `Tunnel__PublicUrl` kan bruges i både Development og Production:

```powershell
$env:Tunnel__PublicUrl = 'https://yatzi.example.invalid'
```

Programmet bruger allerede `WebApplication.CreateBuilder(args)`. Blandt de
beskrevne kilder har miljøvariablen forrang over miljøets appsettings-fil, som har
forrang over `appsettings.json`. Standardprioriteten bevares; der indføres ingen
særskilt konfigurationsindlæsning. Dobbelte underscores repræsenterer niveauerne
i nøglen. Se [Microsofts dokumentation om konfiguration i ASP.NET Core 8](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#default-app-configuration-sources).

Den faktiske adresse fra step 5 er dokumenteret i tunnelprøven nedenfor; den er
ikke hardkodet i backend eller prøveklient. Ngrok-authtoken, WiFi-adgangskode og
andre hemmeligheder må ikke stå i repositoryet eller loggen.
Arduinoens faktiske oplysninger forbliver i den Git-ignorerede
`arduino_secrets.h`; backendindstillingen ændrer ikke automatisk Arduinoens host.

### Validering og opstartsadfærd

Værdien behandles én gang som del af opstarten. Indledende og afsluttende
whitespace fjernes før validering.

| Tilfælde | Kontrakt til implementeringen |
|---|---|
| Nøglen mangler, er tom eller kun indeholder whitespace | Serveren starter til lokal brug. En Information-log oplyser, at ingen offentlig tunneladresse er konfigureret. |
| Udfyldt, gyldig adresse | Accepter en absolut HTTPS-adresse med værtsnavn og eventuelt gyldig port. Kun tom sti eller rodstien `/` accepteres. Loginoplysninger, query og fragment afvises. |
| Udfyldt, ugyldig adresse | Stop opstarten før serveren accepterer requests. Fejlen angiver `Tunnel:PublicUrl` og det forventede format, uden at udskrive inputværdien. |

Originalformen kontrolleres før URI-normalisering: Backslashes, interne
whitespace-/kontroltegn og ikke-rodstier som `/spil/..` afvises. Dermed kan
normalisering ikke få en forbudt sti til at ligne rodstien. En eksplicit port
skal ligge fra 1 til 65535.

En gyldig adresse normaliseres til `https://<vært>[:port]` uden afsluttende `/`.
Standardporten 443 udelades; en anden gyldig port bevares. WSS-adressen afledes
fra samme vært og port med skemaet `wss` og den faste sti `/ws/opla`.

Opstartsloggen bruger struktureret Information-logning med felterne
`TunnelPublicUrl` og `OplaWebSocketUrl`. Den forklarer, at adresserne er
konfigurerede offentlige indgange. Ugyldig konfiguration giver en fast dansk
`InvalidOperationException` uden rå input eller en indre parserundtagelse.

| Eksempel på konfigurationsværdi | Forventet og automatisk kontrolleret resultat i step 2 |
|---|---|
| `https://yatzi.example.invalid` | HTTPS-baseadresse `https://yatzi.example.invalid`; Oplà-adresse `wss://yatzi.example.invalid/ws/opla`. |
| `  https://yatzi.example.invalid/  ` | Samme normaliserede adresser som ovenfor. |
| `https://yatzi.example.invalid:443/` | Standardporten udelades i begge loggede adresser. |
| `https://yatzi.example.invalid:8443/` | Port 8443 bevares i begge adresser. |
| Relativ adresse, HTTP, WSS, ikke-rodsti, loginoplysninger, query, fragment eller ugyldig port | Opstarten stoppes med den beskrevne konfigurationsfejl. |

### Offentlig adresse og lokal lytteadresse

`Tunnel:PublicUrl` er en offentlig adresse til logning og opsætning. Backendens
lokale lytteadresse styres særskilt; den eksisterende standard er
`http://localhost:5000`. Ngrok skal videresende til den adresse, backend faktisk
lytter på. Den offentlige adresse skal ikke bruges som lokal lytteadresse.

Indstillingen starter ikke ngrok, opretter ikke en tunnel og foretager ikke
netværkskald for at kontrollere adressen. En korrekt konfiguration og en korrekt
opstartslog dokumenterer derfor ikke, at tunnelen er tilgængelig. Det verificeres
med en rigtig klient i step 5.

<a id="manuel-opsaetning"></a>

## Manuel opsætning uden lokale prøveværktøjer

Denne vejledning bruger kun den delte kode og standardværktøjer. Kommandoerne
er til operatørens egen senere kørsel; step 7A har ikke startet en ny offentlig
tunnel eller udført hardwareupload. Forudsætninger er .NET 8-kompatibelt SDK,
ngrok på PATH og en konto tilknyttet lokalt. Brug
[ngroks officielle Windows-vejledning](https://ngrok.com/download/windows)
til installation og kontotilknytning. Tokenet bliver i ngroks normale
konfiguration uden for projektet; det må ikke kopieres til chat, kode eller logs.

Brug to PowerShell-terminaler fra repositoryets rod, Mellemprojekt/H3-Yatzi.
Nedenfor vælges lokal port 5000 og ngroks standard-managementadresse 127.0.0.1:4040.
Hvis en af portene er optaget, eller en anden ngrok allerede kører, stoppes
opsætningen. Stop kun processer, som du selv har startet til denne prøve.

### 1. Byg og start en tom lokal Production-API

I terminal A:

```powershell
dotnet restore H3Yatzi.sln
dotnet build H3Yatzi.sln --no-restore --configuration Release
$apiDll = (Resolve-Path 'backend/Yatzi.Api/bin/Release/net8.0/Yatzi.Api.dll').Path
$apiContentRoot = (Resolve-Path 'backend/Yatzi.Api').Path
dotnet $apiDll --environment Production --contentRoot $apiContentRoot --urls http://127.0.0.1:5000 --Tunnel:PublicUrl=
```

Fortsæt kun efter vellykket restore/build. Afvent Now listening on den valgte
loopback-adresse og Hosting environment: Production. Tom Tunnel:PublicUrl
giver Information-loggen om lokal brug. CLI-argumentet tilsidesætter en
eventuel miljøvariabel uden at ændre den. Production bruger ikke
appsettings.Development.json. Den normale content root er nødvendig, fordi
Program finder frontend via ../../frontend.

### 2. Start din egen tunnel mod præcis den lokale adresse

I terminal B:

```powershell
ngrok version
ngrok config check
ngrok http http://127.0.0.1:5000
```

Fortsæt kun efter gyldig konfiguration og et aktivt HTTPS-endpoint. Ngrok bruger
kontoens tildelte adresse; der vælges ikke et opfundet domæne. Bevar terminal B
og ngrok kørende i resten af opsætningen.

### 3. Find adressen og genstart kun den tomme API

Ingen spillere må have tilmeldt sig endnu. Stop API'en med Ctrl+C i terminal A;
lad ngrok i terminal B fortsætte. I samme terminal A hentes den faktiske adresse
fra [ngroks lokale GET /api/endpoints](https://ngrok.com/docs/gateway/agent/api#list-endpoints):

```powershell
$endpoints = (Invoke-RestMethod 'http://127.0.0.1:4040/api/endpoints').endpoints
$httpsEndpoints = @($endpoints | Where-Object { ([uri]$_.url).Scheme -eq 'https' })
if ($httpsEndpoints.Count -ne 1) { throw 'Forventede ét HTTPS-endpoint fra egen ngrok.' }
$upstream = $httpsEndpoints[0].upstream.url
if ([string]::IsNullOrWhiteSpace($upstream) -or $upstream.TrimEnd('/') -ne 'http://127.0.0.1:5000') {
    throw 'Ngrok peger ikke på den valgte lokale API-adresse.'
}
$tunnelPublicUrl = $httpsEndpoints[0].url.TrimEnd('/')
$tunnelPublicUrl
dotnet $apiDll --environment Production --contentRoot $apiContentRoot --urls http://127.0.0.1:5000 "--Tunnel:PublicUrl=$tunnelPublicUrl"
```

Variablerne apiDll og apiContentRoot fra første trin findes stadig i samme
terminal efter Ctrl+C. Opstartsloggen skal nu vise den normaliserede offentlige
HTTPS-baseadresse som TunnelPublicUrl og wss://samme-vært/ws/opla som
OplaWebSocketUrl. Ugyldig adresse stopper opstarten; loggen beviser ikke i sig
selv, at tunnelen virker. API'en og ngrok skal blive kørende gennem hele spillet.
Denne tomme genstart er en opsætningshandling før join, aldrig en
genforbindelsestest med et igangværende spil.

### 4. Kontrollér frontend/API og forbind controlleren

Åbn den fundne HTTPS-baseadresse i Chrome eller Edge. Håndtér en eventuel
ngrok-browseradvarsel med Visit Site. Projektets lobby skal vises, og style.css,
bridge.js og app.js skal indlæses. Åbn også /api/health på samme offentlige
adresse: forvent JSON med status ok. HTML/login/advarsel tæller ikke som API.

| Transport | Operatørens opsætning og forventede tegn |
|---|---|
| USB | Lad controllerens test-WiFi være utilgængeligt. Luk Serial Monitor, klik Forbind Oplà (USB), og vælg dens COM-port. Broen bruger 115200 baud og WSS på samme vært. En firecifret kode kommer fra PAIR og udfylder kodefeltet; join med navn og start spillet. |
| WiFi | Kopiér arduino_secrets.example.h til den ignorerede arduino_secrets.h ved siden af sketchen. Udfyld SSID/password lokalt, SECRET_HOST med tunnelens værtsnavn uden skema/sti og SECRET_PORT 443. Klargør NINA-certifikatet, og upload derefter den rigtige controller før spilstart. Afvent WiFi og koden på Oplàen; indtast koden i browseren og join. |

Hardwaredelen kræver MKR WiFi 1010-boardmiljøet (arduino:samd:mkrwifi1010) og
bibliotekerne Arduino_MKRIoTCarrier, WiFiNINA og ArduinoHttpClient. NINA-version
og faktisk case/board skal identificeres på udstyret. Brug Arduino
IDE 2's Tools → Upload Root Certificates for den aktuelle HTTPS-adresse og
afvent Certificates uploaded; se [Arduinos officielle certifikatvejledning](https://support.arduino.cc/hc/en-us/articles/360016119219-Upload-SSL-root-certificates).
Eventuelle diagnose-/certifikatværktøjer kan overskrive sketchen, så controlleren
uploades bagefter. Der bruges normal certifikatvalidering, og ingen upload
udføres midt i et spil. Strømforsyning uden USB-data og dette hardwareforløb er
fortsat ikke fysisk verificeret i #54.

Et vellykket WSS-håndtryk med READY/PAIR og korrekte spilstatusser er nødvendigt;
HTTP-health alene er utilstrækkeligt. Over USB kan browserens netværksværktøjer
vise /ws/opla og protokolsvar. Over WiFi bruges controllerens transportindikator,
kode og observerede spilstatus sammen med HTTP/lobby som fysisk evidens.
Browseren viser terningeværdierne; Oplàens skærm og LED'er viser tur, slag og hold.

### 5. Kontrollér skift og afslut din egen prøve

To deltagere skal først have hver sin kode, stabilt spiller-id og gemte point.
Under aktiv tur gemmes hele /api/game og /api/lobby før skiftet, fx efter to slag
med terning 2 holdt. Bevar USB-kabel, strøm og browserbro, læg controlleren stille,
tænd testnetværket og afvent WiFi samt korrekt STATE. Gem samme HTTP-snapshots
igen; spil, id'er, navne, rækkefølge og connected skal være uændrede. Sluk kun
controllerens testnetværk, afvent USB og gentag sammenligningen. Den anden
deltager og værtens internet skal være uafhængige af dette testnetværk.

Gentag begge retninger som ventende spiller: forvent WAIT og uændret spil,
også efter ventendes ryst/touch. Lad turen vende tilbage og kontrollér, at ét
gyldigt ryst giver præcis ét kast med begge spilleres tidligere point bevaret.
Nye WiFi-forsøg kræver otte sekunders ro siden seneste afsendelse; afvent faktisk
transport/status med højst 60 sekunder pr. skift. Timeout giver ikke godkendelse.
Ingen spilhandlinger udføres under sammenligningen. Sammenlign JSON-objekter
semantisk, ikke rå tekst med afhængighed af feltrækkefølge.

Ren WiFi testes særskilt med egnet strømforsyning og helt uden USB-data: join,
tre slag, hold/slip, afvist fjerde kast og positive chancepoint skal observeres.
Fællestesten med Abdi skal bruge to fysiske controllere og dokumentere deltagere,
tidspunkt, opsætning, forventet/faktisk resultat og evidens for hver fysisk case.
Hvis strøm flyttes med reset, bruges et nyt spil; det tæller ikke som bevaret
tilstand ved transportskift. Samme backend og kode skal bestå under skiftene.

Efter prøven lukkes egne browserbro-/controllerforbindelser, og egne API- og
ngrok-terminaler stoppes med Ctrl+C. Kontotilknytning og installation bevares.
Vær opmærksom på, at backendgenstart mister spil/parringer i hukommelsen.

### Fejlfinding ved opsætning

| Observation | Konkret kontrol |
|---|---|
| Lokal JSON-health fejler | Kontrollér den faktiske lokale lytteadresse og Production-startloggen før tunnelen undersøges. |
| API virker, men rodstien giver 404 | Kontrollér content root: den skal være backend/Yatzi.Api, så ../../frontend findes. |
| Offentlig adresse giver HTML/login eller forkert indhold | Kontrollér HTTPS-endpoint, upstream og om backend fortsat lytter. En advarselsside må ikke registreres som JSON-health eller fungerende WSS. |
| WSS eller NINA fejler | Kontrollér /ws/opla, port 443, faktisk SECRET_HOST, NINA-version og uploadet root-certifikat. Et bestået .NET-WSS-forløb er ikke et bevis for NINA-certifikater. |
| USB-porten kan ikke åbnes | Luk Serial Monitor og andre ejere af den pågældende COM-port; forbind via brugerens klik i Chrome/Edge. |
| Koden eller spillet ændres efter reset/genstart | Kontrollér, om controller eller backend mistede hukommelsen. Start en ny prøve; registrér ikke dette som et rent transportskift. |

### Automatiske softwaretests fra samme checkout

Frontendkontrollerne kræver Node 22. Kør fra repositoryets rod:

```powershell
dotnet test H3Yatzi.sln --configuration Release
node --check frontend/app.js
node --check frontend/bridge.js
node --test --test-reporter=tap tests/frontend/app.test.cjs
```

Disse kontroller bruger hverken den offentlige tunnel eller fysisk hardware.
De faktiske, daterede kontrolresultater og deres begrænsninger står nedenfor.

## Komponentansvar og dataflow

```text
USB:  Oplà -> Web Serial-browserbro -> WSS-tunnel -> /ws/opla
WiFi: Oplà ------------------------> WSS-tunnel -> /ws/opla
                                                     |
                                  transport- og adgangskontrol
                                                     |
                                     LobbySession og register
                                                     |
                                      OplaMessageHandler
                                                     |
                                       GameSession -> Yatzi.Core
                                                     |
                              svar via den aktuelle forbindelse

Browserens HTTP-API bruger samme lobby og GameSession til join, feltvalg og visning.
```

| Komponent | Ansvar i den eksisterende løsning |
|---|---|
| [Program](../backend/Yatzi.Api/Program.cs) | Starter backend, registrerer fælles singleton-tjenester og WebSocket-middleware samt HTTP-/WebSocket-endpoints. |
| [TunnelStartup](../backend/Yatzi.Api/TunnelStartup.cs) | Læser den færdige konfiguration efter Build, validerer offentlig adresse og logger HTTPS-/WSS-adresser før serverstart. Ændrer ingen lytteadresse og foretager ingen netværkskald. |
| [Arduinoens link](../arduino/yatzi_controller/link.cpp) og [controller](../arduino/yatzi_controller/yatzi_controller.ino) | Vælger WiFi eller USB, sender input og READY med bevaret kode, modtager status og opdaterer hardwarefeedback. |
| [Browserbro](../frontend/bridge.js) | Videresender USB-protokollinjer mellem Web Serial og `/ws/opla`. |
| [OplaWebSocketEndpoints](../backend/Yatzi.Api/OplaWebSocketEndpoints.cs) | Ejer transportens levetid og validering, registrerer READY og fører godkendte input videre til lobbyens koordinering. |
| [LobbySession](../backend/Yatzi.Api/LobbySession.cs) og [OplaConnectionRegistry](../backend/Yatzi.Api/OplaConnectionRegistry.cs) | Ejer henholdsvis koder/spillere/beskedrækkefølge og forbindelser/adgang. Flytter bindingen ved genforbindelse og tilbagekalder gammel adgang. |
| [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) | Fortolker `ROLL`/`HOLD` gennem den fælles spilsession. |
| [GameSession](../backend/Yatzi.Api/GameSession.cs) og [Yatzi.Core](../backend/Yatzi.Core/README.md) | Ejer serverens spiltilstand i hukommelsen og reglerne for kast, hold, point og ture. HTTP og Oplà deler sessionen. |

Transportgrænserne bevares: 12 forbindelser/reservationer, højst 256 payload-bytes,
64 Unicode-kodepunkter og 20 komplette tekstbeskeder pr. rullende sekund pr.
forbindelse. Der tilføjes ingen ny route, HTTP-kontrakt eller parallel spillogik.

### READY og skift under et igangværende spil

1. Første forbindelse sender `READY`, modtager `PAIR cccc` og tilmeldes gennem
   lobbyens HTTP-join. Serveren sender `JOINED navn`.
2. Ved et transportskift sender den nye forbindelse `READY cccc` som sin første
   READY-besked. En forbindelse, der allerede har en kodepost, beholder sin egen
   tilknytning; den kan ikke skifte spiller ved senere at sende en anden kode.
3. Lobbyen genfinder den tilmeldte spiller. Registeret flytter spillerbindingen
   og tilbagekalder den gamle adgang atomisk. Den gamle socket lukkes gennem
   den eksisterende lukkevej.
4. Den nye forbindelse modtager `PAIR`, `JOINED` og, efter spillestart, aktuel
   `STATE`, `WAIT` eller `END`. Genforbindelsen opretter ikke en ny spiller eller
   nulstiller spillet. Sen oprydning af gammel forbindelse må ikke fjerne den nye.

Kontrollen sammenligner spiller-id, `gameId`, tur, brugte slag, terninger,
hold-markeringer og point før/efter overtagelsen. En accepteret handling må ikke
genudføres ved transportfejl. Testklienten sender ikke nye spilhandlinger under
selve sammenligningen.

Forudsætningen er samme kørende backend og en bevaret kode i Oplàens hukommelse.
Spillet og parringerne ligger i serverens hukommelse; servergenstart er ikke
genforbindelse til et bevaret spil. En ubrugt kode fra en lukket forbindelse kan
bortfalde. Fysisk transportskift afprøves med bevaret strøm, så et strømtab på
Oplàen ikke bliver forvekslet med et rent forbindelsesskift.

## Krav/test-matrix for #54

Matricen skelner mellem konfigurationen i step 2, softwaremodellerne i step 3,
rigtig lokal TCP i step 4 og offentlig HTTPS/WSS i step 5. Fysisk integration
udestår i step 6. Software- og tunnelresultaterne afslutter ikke hele #54.

| Krav fra #54 | Eksisterende grundlag og nødvendig leverance | Kontrol og forventet resultat | Trin |
|---|---|---|---|
| Klient uden browser på `/ws/opla` gennem tunnel | Består lokalt i step 4 og over offentlig HTTPS/WSS i step 5 med rigtig ClientWebSocket/Kestrel. | Håndtryk, READY/PAIR, HTTP-join/JOINED, start, ROLL/HOLD og genforbindelse består. Step 5 har 55 kontrolpunkter, normal TLS-validering og ingen cookies, Origin-header eller ngrok-skip-browser-warning-header. Protokol og HTTP-snapshots stemmer. | 4–5 |
| Samme endpoint og beskedhåndtering som USB | `/ws/opla`, lobby, handler og GameSession er fælles. Gennemgået og softwarekontrolleret i step 3. | 271 eksisterende tests bestod før udvidelsen. Seks nye cases følger HTTP-join/start/score og WebSocket-READY/input gennem samme app-pipeline og bevarer hele spiltilstanden. Ingen produktionsændringer eller dobbelt spillogik. | 3 |
| Tunneladresse i konfiguration og opstartslog | Implementeret i step 2; faktiske offentlige logfelter kontrolleret i step 5; genkontrolleret i step 7A. | 49 konfigurationscases og de seks transportskiftcases består i step 7A's samlede kørsel med 1.434 backendtests. De tidligere tre procesprøver dækker lokal start og konfigurationskilder. Tom værdi tillader lokal brug; gyldig adresse giver logfelter; ugyldig værdi stopper opstart uden rå input. Step 5 kontrollerer de faktiske HTTPS/WSS-felter mod ngroks endpoint/upstream. | 2, 5, 7A |
| Skift mellem USB og WiFi under spil med `READY cccc` | Softwaremodeller består i step 3; overtagelse og normal genforbindelse består lokalt i step 4 og over WSS i step 5. Fysisk skift udestår. | Samme kode og spilleridentitet genbruges. Step 3 dækker STATE/WAIT/END; step 4–5 dækker kast/hold, afvist gammel ROLL, bevarede snapshots, point og tur tilbage til genforbundet spiller. | 3–6 |
| Test sammen med Abdi (#53) | WiFi-koden er merget. Step 6's prøvevært, vejledning og seks tomme testcases er forberedt; fysisk fællestest udestår. | En fysisk Oplà spiller en hel tur over WiFi uden USB-dataforbindelse. Browser, skærm og LED'er følger serveren. USB virker, og transportskift bevarer spillet. Dato, deltagere, opsætning og faktisk resultat noteres. Softwareforberedelsen kan ikke godkende denne række. | 6 |

Eksisterende tests, som er gennemgået i step 3, er
[LobbyReconnectSessionTests](../backend/Yatzi.Tests/LobbyReconnectSessionTests.cs),
[OplaConnectionTakeoverTests](../backend/Yatzi.Tests/OplaConnectionTakeoverTests.cs),
[LobbyStartSessionTests](../backend/Yatzi.Tests/LobbyStartSessionTests.cs),
[LobbyGameInputTests](../backend/Yatzi.Tests/LobbyGameInputTests.cs) og
[LobbyBackendIntegrationTests](../backend/Yatzi.Tests/LobbyBackendIntegrationTests.cs).
De dækker blandt andet spilleridentitet, gammel oprydning, samtidige overtagelser
og genoprettelse af STATE/WAIT efter turskifte. Step 3 supplerer med det samlede
gentagne forløb og fuld snapshot-sammenligning som beskrevet nedenfor.

### Grænser mellem kontrollerne

- **Automatiske tests:** Verificerer konfiguration, app-pipeline og
  genforbindelsesadfærd med kontrollerede forbindelser. De beviser ikke fysisk
  USB/WiFi, NINA-certifikater eller offentlig tunneladgang.
- **Rigtig lokal klient:** Verificerer Production-serverens WebSocket over TCP
  uden browser. Lokal `ws` er endnu ikke en offentlig `wss`-prøve.
- **Rigtig tunnelprøve:** Verificerer håndtryk, protokol og spilhandlinger gennem
  den faktiske WSS-adresse. Et HTTP-healthcheck er ikke tilstrækkeligt.
- **Fysisk fællestest:** Verificerer Oplà, certifikat, WiFi, browserbro, tunnel og
  backend sammen. Udføres sammen med Abdi med faktiske resultater. WiFi-udfald
  og skift tilbage til USB kræver en tilgængelig USB-browserbro.

## Lokal kontrol af step 2

Følgende er gennemført på `S3-10-wifi-opla-tunnel` uden commit eller push:

- Restore og Release-build består med **0 fejl og 0 advarsler**.
- **49 nye tests** i
  [TunnelStartupTests](../backend/Yatzi.Tests/TunnelStartupTests.cs) og
  [TunnelStartupIntegrationTests](../backend/Yatzi.Tests/TunnelStartupIntegrationTests.cs)
  består, ingen fejlede eller oversprungne. De kontrollerer tom/manglende værdi,
  normalisering, standard-/alternativ port, ugyldigt format, skjulte ikke-rodstier,
  fejl uden rå input og app-opstart i Development og Production. Integrationstests
  overskriver konfiguration pr. vært uden at ændre globale miljøvariabler.
- Hele backendtestsuiten består med **1.428 tests**, ingen fejlede eller
  oversprungne. TRX-resultaterne er kontrolleret i
  `TestResults/issue54-step2-focused.trx` og `TestResults/issue54-step2-results.trx`.
- Tre separate processer med den byggede Release-API og dynamiske loopback-porte
  består: Production uden offentlig adresse returnerer HTTP-health `ok`;
  Development med en adresse i en midlertidig appsettings-fil logger i stedet
  miljøvariablens normaliserede HTTPS-/WSS-adresser og returnerer health `ok`;
  ugyldig udfyldt adresse afslutter processen med fejl før serverstart, uden
  testmarkøren i output.
- Prøvernes konfiguration er isoleret fra brugerens filer og miljø. Alle tre
  processer er stoppet, og de midlertidige konfigurationsmapper er fjernet.
  Prøveværktøj, logs og `startup-results.json` ligger lokalt under
  `TestResults/issue54-step2/` og indgår ikke i Git-leverancen.

Disse resultater verificerer opstartsadfærd og lokal HTTP-health. Der er ikke
startet ngrok, oprettet en offentlig WSS-forbindelse eller udført en fysisk
Oplà-/WiFi-/USB-test. Disse resultater bevares som afslutningen af step 2.

## Lokal kontrol af step 3

Step 3 tilføjer
[LobbyTransportSwitchApiTests](../backend/Yatzi.Tests/LobbyTransportSwitchApiTests.cs)
som en del af den eksisterende `LobbyApiTests`-fixture. Testene modellerer
USB → WiFi → USB med tre forskellige WebSocket-forbindelser gennem appens
testserver i hukommelsen. Serveren modtager ingen transportmarkør; alle
forbindelser bruger samme `/ws/opla`, lobby, beskedhandler og GameSession.

Hvert scenarie køres i Development og Production, altså seks cases:

| Testmetode | Kontrolleret forløb |
|---|---|
| `TransportSwitch_ActivePlayerKeepsStateAndCanRollOnceAfterTwoTakeovers` | A har positive chancepoint gemt gennem HTTP-score. B har to slag og terning 2 holdt. B overtages to gange med samme kode og får PAIR/JOINED/STATE. Et nyt kast efter andet skift øger slagtælleren fra to til tre og bevarer den holdte terning. |
| `TransportSwitch_WaitingPlayerKeepsIdentityAndCannotRollOrHoldAfterTwoTakeovers` | I samme ikke-tomme spil overtages A to gange, mens B har tur. A får PAIR/JOINED/WAIT B. ROLL og HOLD afvises uden spilændring; efterfølgende READY giver fortsat WAIT uden et uventet STATE-svar foran. |
| `TransportSwitch_CompletedGameKeepsResultAndCannotReopenAfterTwoTakeovers` | Begge tavler udfyldes gennem gyldige HTTP-kast og scorevalg, og hvert WebSocket-statussvar læses. B overtages to gange med PAIR/JOINED/END. ROLL og HOLD afvises på slutstatus; et efterfølgende READY giver stadig END. |

Ved hver overtagelse sammenlignes hele HTTP-spiløjebliksbilledet med
`GameStateAssertions.Equal`, både før og efter gammel oprydning: gameId,
turnNumber, aktiv spiller, terninger, slag, hold, mulige point, alle tavler og
summer, tur-/slutstatus og vinderindeks. Lobbyens spiller-id'er, navne,
rækkefølge og forbindelsesstatus sammenlignes særskilt. Den nye binding skal
have samme spiller-id og en ny ConnectionId.

Den nye forbindelse modtager sine tre svar, mens gammel lukning stadig venter.
Først derefter afslutter testklienten det gamle lukkehåndtryk og afventer
`Completion`. Den gamle binding er straks tilbagekaldt; gentaget fjernelse af
den gamle ConnectionId må ikke fjerne den nye. Efter begge skift kan den nye
forbindelse fortsætte, og ved afslutning af testen er registeret tomt. Der bruges
ingen vilkårlige ventepauser; protokolsvar, afvisningslogs og Completion bruges
som behandlingsmarkører under fixturets eksisterende deadline.

Den eksisterende dækning genbruges til de øvrige egenskaber:

| Egenskab | Eksisterende testdækning |
|---|---|
| Tilbagekaldt adgang og input under konkurrerende overtagelse | `LobbyGameInputTests.ConcurrentTakeoverAndInput_OnlyCurrentBindingCanContinue` og `RecheckRejectsWrongIdentityRemovedAndUnboundConnections`. |
| En allerede accepteret handling genudføres ikke ved sen sendefejl | `LobbyGameInputTests.TakeoverDuringOldSend_PreservesActionAndDoesNotWaitOrCloseNewOwner`. |
| Sen gammel oprydning eller READY bevarer ny binding | `LobbyReconnectSessionTests.LateOldCleanupAndReady_DoNotRemoveNewBindingOrIssueAnotherCode`. |
| Samtidige overtagelser og begrænset gammel lukning | `LobbyReconnectSessionTests.ConcurrentTakeovers_LeaveOnlyLastAcceptedConnectionBound` og `OldCloseTimeout_AbortsAfterFiveSecondsWithoutAffectingNewBinding`. |
| Transportgrænser og kapacitet består ved genforbindelse | `LobbyApiTests.LobbyTransportViolation_PreservesGameAndAllowsSamePlayerToReconnect`, `FullLobbyRegistry_RejectsExtraConnectionThenAllowsTakeoverWithoutLosingBinding` og registerets `OplaConnectionTakeoverTests`. |

**Faktisk verifikation:** De 271 relevante eksisterende tests bestod før nye
cases blev tilføjet. Release-build består med **0 fejl og 0 advarsler**; de
**seks nye cases** består, og hele backendtestsuiten består med **1.434 tests**,
ingen fejlede eller oversprungne. Resultaterne er kontrolleret i
`TestResults/issue54-step3-baseline.trx`, `TestResults/issue54-step3-focused.trx`
og `TestResults/issue54-step3-results.trx`.

Der var ikke behov for produktionsrettelser. Resultaterne er softwaremodeller
for transportskift, ikke fysisk USB-/WiFi-verifikation. De særskilte TCP- og
WSS-prøver står nedenfor; fysisk fællestest i step 6 udestår.

## Rigtig lokal TCP-prøve i step 4

Step 4 bruger et selvstændigt .NET 8-konsolprojekt uden pakker, projektreferencer
eller WebApplicationFactory. Python-runneren starter den byggede Release-API
som en skjult Kestrel-proces med Production-miljø, midlertidig konfiguration og
dynamisk loopback-port. Serverloggen bekræfter Production, og HTTP-health skal
være `ok`, før klienten starter. `Tunnel:PublicUrl` tvinges til tom i child-processen;
brugerens filer, miljøvariabler og lokale lytteadresse ændres ikke.

Klienten bruger HttpClient og ClientWebSocket uden cookies, proxy eller
standardlegitimationsoplysninger og opretter ingen Origin-header. Server og
klient kommunikerer over faktisk lokal TCP, ikke testserveren i hukommelsen.
Begge Release-builds bestod med **0 fejl og 0 advarsler**.

**Bestået kørsel:** .NET-runtime **8.0.19**, Production på
`http://127.0.0.1:61054` (prøveserveren er nu stoppet), **55 kontrolpunkter**,
**103 protokol-/HTTP-hændelser** og **19 HTTP-snapshots**. De otte forløb består:

| Forløb | Faktisk kontrolleret resultat |
|---|---|
| Parring og join | A og B fik forskellige firecifrede koder, entydige joinedPlayerId og korrekte JOINED-svar. Lobbyen startede tom og bevarede tilmeldingsrækkefølgen. |
| Spillestart | Samme gameId startede med tur 1, STATE 0 til A og WAIT A til B. |
| Ventende input | B's ROLL/HOLD ændrede intet. READY-markøren gav PAIR/JOINED/WAIT uden et ekstra STATE foran. |
| Kast og hold | A kastede to gange med terning 2 holdt; STATE og HTTP-snapshot viste to slag og bevaret værdi for den holdte terning. |
| Overtagelse af åben forbindelse | A2 genbrugte A1's kode og fik PAIR/JOINED/STATE 2 01000. Hele spil- og lobbyøjebliksbilledet var uændret. En gammel ROLL kunne faktisk sendes fra A1 og blev afvist i serverloggen, fordi den gamle binding var tilbagekaldt. A1 modtog normal Close. Efter lukkehåndtryk og READY på A2 var spil og identitet stadig uændret. |
| Fortsat spil og point | A2 ændrede hold til 01100 og gemte positive chancepoint med gameId/turnNumber/playerId. Samme spil skiftede til B med STATE 0 og WAIT B til A2. |
| Normal genforbindelse | A2 lukkede normalt og blev vist frakoblet i lobbyen uden spilændring. A3 genbrugte samme kode og spiller-id, modtog WAIT B og bevarede hele spillet og pointene uden nyt join. |
| Turen vendte tilbage | B kastede og gemte positive chancepoint. A3 fik næste tur og kastede én gang. GameId, begge spiller-id'er og de gemte point var bevaret på tur 3. |

Komplette spiløjebliksbilleder sammenlignes med JsonNode.DeepEquals ved
overtagelse og afbrydelse/genforbindelse. Lobbyidentiteter, navne, rækkefølge og
forbindelsesstatus sammenlignes særskilt. Transkriptet registrerer præcist,
hvilke beskeder og HTTP-svar klienten faktisk modtog.

Alle fire sockets sluttede som Closed og blev disponeret. Server- og
klientprocessen er afsluttet, den midlertidige konfigurationsmappe er fjernet,
og OS' lytteroversigt viste ingen lytter på prøveporten. Runneren har en samlet
40-sekundersfrist samt separat oprydning; fejl og timeouts kan ikke tælle som PASS.

Prøvekode og artefakter ligger lokalt i den Git-ignorerede mappe
`TestResults/issue54-step4/`. `latest-run.json` peger på den samlede seneste
kontrol; den beståede kørsel findes i `run-cknjfv9n/` med `result.json`,
`client-results.json`, `client-protocol.jsonl`, `http-snapshots.json`,
`server.log` og `client-output.log`.

To indledende kørsler bestod klientforløbet, men blev korrekt markeret FAIL,
fordi en TCP-forbindelsesprøve ikke entydigt kunne bekræfte frigivet port på
Windows. Oprydningskontrollen blev ændret til den direkte OS-lytteroversigt;
hele prøven bestod derefter. De to fejlkørsler er bevaret i deres egne mapper.
Der var ikke behov for produktionsrettelser. Den samlede automatiske testsuite
fra step 3 er bevaret som tidligere resultat og er ikke genkørt i dette trin.

Denne prøve dokumenterer lokal ws over TCP. Den tester ikke offentlig WSS,
TLS-certifikater, Web Serial-browserbro, fysisk WiFi/USB eller Arduino-hardware.
Step 5 udvider denne prøve med offentlig HTTPS/WSS som beskrevet nedenfor.

## Offentlig HTTPS-/WSS-prøve i step 5

Den nuværende udviklings-PC er prøvevært; gruppens eksisterende vært er ikke
ændret. Ngrok er installeret med den officielle Windows-kommando
`winget install ngrok -s msstore`. Den faktiske CLI-version er
**3.39.11-msix-stable**. Brugeren har selv tilknyttet kontoen i sin lokale
terminal. Authtoken bliver i ngroks normale konfiguration uden for projektet;
prøveværktøjet læser ikke filindholdet og gemmer ingen token- eller miljødumps.
Se [ngroks officielle Windows-vejledning](https://ngrok.com/download/windows).

Det selvstændige .NET 8-projekt og Python-runneren er kopieret fra step 4 til
den Git-ignorerede mappe `TestResults/issue54-step5/`. Step 4's prøvefiler og
tidligere resultater er bevaret. Begge nye Release-builds består med **0 fejl
og 0 advarsler**; der er ingen nye pakker eller backend-projektreferencer.

Runneren stopper ved en eksisterende ngrok-proces eller en optaget port 4040.
Den starter sin egen skjulte Production-API med tom tunnelindstilling og
dynamisk loopback-port. Egen ngrok videresender til netop den lokale adresse.
HTTPS-adressen og upstream hentes fra ngroks lokale
[`GET /api/endpoints`](https://ngrok.com/docs/gateway/agent/api#list-endpoints),
og upstream skal matche prøveinstansen. Før nogen spillere oprettes, stoppes
den tomme bootstrap-API, og en ny prøve-API starter på samme port med den
fundne `Tunnel:PublicUrl`. Production, lokal JSON-health, tom lobby og de to
strukturerede offentlige logfelter kontrolleres før klientstart.

**Bestået kørsel 10. november 2026:** `run-3b_1rrvr`, .NET-runtime **8.0.19**,
.NET SDK **9.0.304**, Python **3.13.1** og ngrok-versionen ovenfor.
De faktisk fundne adresser var:

```text
TunnelPublicUrl:  https://seminar-implement-peddling.ngrok-free.dev
OplaWebSocketUrl: wss://seminar-implement-peddling.ngrok-free.dev/ws/opla
Upstream:        http://127.0.0.1:63477
```

Adresserne er historik fra prøven; tunnelen og prøve-API'en er nu stoppet.
Kontoens tildelte adresse blev fundet under kørslen og er ikke hardkodet i
prøveværktøjet. Alle klientens HTTP- og WebSocket-kald gik gennem den offentlige
adresse: HttpClient/ClientWebSocket → HTTPS/WSS hos ngrok → lokal Kestrel →
samme `/ws/opla`, lobby, handler og GameSession som USB-browserbroen.
TLS termineres hos ngrok; upstream på samme PC er lokal HTTP/ws.

Klienten bruger systemets normale TLS-certifikatvalidering uden bypass,
cookies, proxy, standardlegitimationsoplysninger eller oprettet Origin-header.
Basiskørslen har ingen `ngrok-skip-browser-warning`-header. HTTP-svar skal være
`application/json`, health skal have `status: ok`, og et rigtigt WSS-håndtryk
med korrekte protokolsvar er påkrævet. HTML-advarsler, login og redirects
tæller ikke som succes. Ngrok beskriver programmatisk API-trafik som upåvirket
af browseradvarslen i sin [officielle beskrivelse](https://ngrok.com/docs/pricing-limits/free-plan-limits#removing-the-interstitial-page).

**Alle otte forløb fra step 4 består over HTTPS/WSS:**

| Forløb | Faktisk kontrolleret resultat |
|---|---|
| Parring og join | A og B fik forskellige koder og spiller-id'er. Lobbyens navne, id'er og rækkefølge stemte. |
| Spillestart | Samme spil startede med STATE 0 til A og WAIT A til B på tur 1. |
| Ventende input | B's ROLL/HOLD gav ingen spilændring eller ekstra STATE før READY-markørens PAIR/JOINED/WAIT. |
| Kast og hold | To kast og hold af terning 2 stemte mellem STATE og HTTP; den holdte værdi var bevaret. |
| Åben overtagelse | A2 genbrugte koden; hele spillet og lobbyidentiteterne var uændrede. Gammel ROLL kunne faktisk sendes, men blev afvist i serverloggen med READY/parring mangler. A1 modtog normal Close. READY på A2 og nye snapshots viste fortsat uændret spil og bevaret ny binding. |
| Fortsat spil og point | A2 ændrede hold til 01100 og gemte positive chancepoint med aktuel gameId, turnNumber og joinedPlayerId. Turen skiftede korrekt til B. |
| Normal afbrydelse/genforbindelse | A blev først vist frakoblet, mens hele spillet bestod. A3 genskabte samme spiller med kode og WAIT B, uden nyt join. |
| Turen tilbage | B gemte positive chancepoint. A3 fik turen og kastede én gang; samme gameId, begge id'er og begge spilleres gemte point bestod på tur 3. |

I alt **55 kontrolpunkter**, **103 protokol-/HTTP-hændelser** og **19 snapshots**.
Komplette spiløjebliksbilleder sammenlignes semantisk med JsonNode.DeepEquals;
lobbyidentiteter og forbindelsesstatus kontrolleres særskilt. Gammel ROLL er
registreret som `sent` og gammel lukning som `normalClose` fra klienten.
Serveren loggede også `ConnectionClosedPrematurely` for den tilbagekaldte
forbindelse gennem tunnelen; ny binding og spillet bestod efter oprydningen.
Der bruges ingen timeout som bevis for afvist input eller vellykket lukning.

Netværksprøven tog **6,414 sekunder**, heraf **4,695 sekunder** i klienten.
Runnerens grænser er 120 sekunder samlet, højst 30 til readiness og højst 90
til klienten, med separat oprydning på højst 20 sekunder. Builds og lokal
kontotilknytning ligger før netværksfristens start. Alle fire klientsockets
sluttede som Closed og blev disponeret. Bootstrap-API, endelig API, ngrok og
klient blev stoppet. Den midlertidige konfiguration er fjernet, og OS-tabellen
viser ingen lyttere på **63477** eller **4040**. Installationen og brugerens
ngrok-kontotilknytning er bevaret til senere arbejde.

Den beståede mappe indeholder `result.json`, `client-results.json`,
`client-protocol.jsonl`, `http-snapshots.json`, `bootstrap-server.log`,
`server.log`, `ngrok-sanitized.jsonl`, `client-output.log` og de to buildlogs.
`TestResults/issue54-step5/latest-run.json` peger på resultatet. Ngrok-diagnostik
bevarer kun faste hændelsesnavne, niveau og eventuelle offentlige fejlkoder;
rå fejl-/konfigurationsfelter gemmes ikke. Forberedelseskørslen `run-g4ejx7z0`
er bevaret særskilt som PREPARED og tæller ikke som en WSS-prøve.

Slutkontrollen består med streng UTF-8, 23 lokale dokumentationslinks, to
gyldige JSON-eksempler og git diff --check, inklusive den nye specifikation.
Alle 109 tidligere trackede/ikke-ignorerede projektfiler ud over specifikationen
er byte-for-byte bevaret. AGENTS.md, skoleplan, arbejdslog, trin 4-prøvefiler og
den personlige noCase-ændring er også bevaret. En syntetisk hemmelighedsmarkør
fjernes korrekt fra ngrok-diagnostik, og en kort egen konfliktlytter på 4040
afvises uden overtagelse. Egen konfliktlytter er lukket og porten fri igen.
Kontrollerne er gemt i `TestResults/issue54-step5/verification.json`.

Ingen produktionsrettelse var nødvendig. Hele backendtestsuiten er ikke
genkørt i step 5; de 1.434 tests fra step 3 er et tidligere resultat.
Prøven kørte fra samme PC gennem ngroks offentlige tjeneste. Et fysisk andet
netværk, Arduino/NINA-certifikater, browserbro og fysisk WiFi/USB er **ikke
afprøvet**. Step 6 med Abdi er næste trin. #54, #43 og #46 afsluttes ikke,
og der er ingen commit, push eller PR.

## Step 6 – forberedelse færdig, fysisk test afventer

Efter brugerens valg er kun forberedelsen udført. Der er et kontrollerbart
hotspot/router til rådighed, men strømforsyning uden USB-data er uafklaret,
og fysisk udstyr/deltagelse med Abdi afventer. Ingen hardware-, firmware- eller
certifikatupload er udført. Der er ikke oprettet en faktisk arduino_secrets.h;
NINA-firmwareversion og fysisk funktion er ikke verificeret.

De lokale værktøjer ligger under den Git-ignorerede mappe
`TestResults/issue54-step6/`. `HARDWARE-GUIDE.md` beskriver udstyr, lokal
konfiguration, Arduinos certifikatværktøj, uploadrækkefølge og den fysiske prøve.
`physical-test-protocol.json` indeholder seks cases med status IKKE_AFPRØVET og
tomme felter til deltagere, tidspunkt, opsætning, faktisk resultat og evidens.
En kopi placeres i hver ny kørselsmappe. Ingen softwarekontrol udfylder fysiske
resultater automatisk.

### Værktøj og faktisk værtskontrol

`run_host.py` genbruger trin 5's uændrede proces-, endpoint- og
sanitiseringshjælpere. Standardtilstanden er `--verify-host`; `--serve` er til
senere manuel drift. Den separate .NET 8-klient har ingen pakker eller
backend-referencer. Alle HTTP-/WebSocket-kald bruger den faktiske offentlige
HTTPS/WSS-adresse med systemets TLS-validering uden cookies, proxy,
standardlegitimationsoplysninger, oprettet Origin-header eller ngrok-skip-header.

API'en bruger nu backendprojektets normale content root, fordi Program finder
frontenden gennem ../../frontend. Trin 5's midlertidige konfigurationsmappe var
egnet til API-prøven, men serverede ikke browserfrontenden. Prøveindstillinger
gives som child-procesargumenter i Production; ingen brugerfiler, globale
miljøvariabler, produktionskode eller API-kontrakter ændres.

Den seneste automatiske værtskontrol, **run-821k_pba**, består med **16
klientkontroller**. Offentlig JSON-health, den rigtige HTML-frontend og alle
tre CSS-/JavaScript-filer virker; alle fire filers SHA-256 matcher denne branch.
WSS-håndtryk → READY → firecifret PAIR virker; socketen afslutter normalt og
disponeres uden HTTP-join. Lobbyen er tom og uændret efter softwareparringen.

Faktisk adresse var `https://seminar-implement-peddling.ngrok-free.dev`, upstream
`http://127.0.0.1:65427`. Production og begge offentlige opstartslogfelter var
korrekte. Readiness tog **1,338 sekunder**, samlet softwareverifikation **2,365
sekunder**, og oprydning **0,182 sekunder**. De automatiske grænser er 30/90/120
sekunder for readiness/klient/samlet kontrol og separat 20 til oprydning.
Alle egne processer er stoppet, og port **4040** og **65427** har ingen lyttere.
Ngrok-installation og kontotilknytning bevares. Begge Release-builds består med
**0 fejl og 0 advarsler**; .NET-runtime er 8.0.19, SDK 9.0.304, Python 3.13.1
og ngrok 3.39.11-msix-stable.

### Manuel drift og snapshots er softwarekontrolleret

`--serve` bevarer samme API/tunnel indtil stop eller Ctrl+C. `snapshot NAVN`
gemmer hele spillet og lobbyen via den offentlige adresse; game læses igen efter
lobbyen for at opdage ændringer under læsningen. Før start gemmes den faktiske
404-JSON uden at oprette et spil. `compare FOER EFTER` sammenligner vært, spil
og lobby semantisk med JsonNode.DeepEquals, inklusive alle pointtavler,
spiller-id'er, navne, rækkefølge og connected. Tidspunkt og artefaktnavn indgår
ikke. Forskelle giver DIFFERENT/fejlkode og bevares i diagnostikken.

Manuel drift er kontrolleret med software i **run-jorcpm2c**: tomt snapshot,
oprettelse af et separat enkeltspillerspil som prøvegrundlag, to komplette
snapshots, MATCH og stop består. **Ni kontrolpunkter** i
`controls-m0ozfejv/result.json` bekræfter også, at ændret hold eller lobbyidentitet
i kopier af JSON opdages som DIFFERENT med fejlkode. Ingen fysisk controller
eller browserhandling indgik. Drift slutter med SESSION_STOPPED, ikke en fysisk
godkendelse. Egen API, ngrok, klient og bootstrap blev stoppet; porte frigivet.

En kontrolleret KeyboardInterrupt i hovedtråden efter READY består i
**run-7lf6rfeg**, med dokumenteret oprydning og stopReason ctrl-c. Dette er en
softwarekontrol af afbrydelsesstien, ikke en fysisk tastatur-/hardwaretest.
Første interrupt-kontrol fik straks EOF fra testmiljøets stdin, hvorefter et
forsinket interrupt ramte portkontrollen under oprydning. Den afbrudte kørsel
**run-4nc3fdrl** er bevaret som FAIL med efterfølgende kontrol af frigivne porte.
Kontrolharnesset holder nu stdin åbent; runneren beskytter desuden sin afgrænsede
oprydning mod ekstra SIGINT. Kun prøveværktøjerne blev ændret.

`latest-verification.json`, `latest-session.json` og hver kørsels `result.json`
skelner mellem automatisk værtskontrol og manuel driftsafslutning. Software- og
fysisk status er særskilte; fysisk status er fortsat not-tested. Logs, transkript,
hashes, snapshots, sammenligninger, buildlogs og sanitiseret ngrok-diagnostik
er bevaret. En afsluttet session står ikke fortsat som READY i session.json.

### Arduino-kompilering og fysisk opfølgning

Den aktuelle personlige noCase-sketch er kompileret uden hemmelighedsfil og
uden upload til `arduino:samd:mkrwifi1010`: **106.640 bytes flash (40 %)** og
**7.988 bytes globale variable (24 %)**. Arduino IDE 2.3.10, CLI 1.5.1,
arduino:samd 1.8.14, Arduino_MKRIoTCarrier 2.1.0, WiFiNINA 2.1.1 og
ArduinoHttpClient 0.6.2 er registreret i `toolchain.json`; ingen opgraderinger.

Den senere fysiske protokol dækker USB, en WiFi-tur uden USB-data, to fysiske
controllere med Abdi, USB → WiFi → USB under aktiv og ventende tur samt fortsat
spil med bevarede point. USB-kabel og strøm beholdes under skiftetesten.
Kun den valgte controllers testnetværk afbrydes; anden deltager og værtens
internet skal være uafhængige. Afvent faktisk transport/status, med otte
sekunders ro før nye WiFi-forsøg og højst 60 sekunder pr. skift. Ingen
spilhandlinger udføres under sammenligningen, og timeout giver ikke godkendelse.

Alle 109 tidligere projektfiler ud over specifikationen og 4.122 tidligere
prøve-/buildartefakter ud over den tilsigtet opdaterede interne plan er
byte-for-byte bevaret. Skolemateriale, arbejdslog og noCase er uændret.
Streng UTF-8, 23 lokale specifikationslinks, to JSON-eksempler, værktøjets JSON,
Git-diff og whitespace består i `TestResults/issue54-step6/verification.json`.
Hele backendtestsuiten er ikke
genkørt i step 6; de 1.434 tests fra step 3 var tidligere verifikation.
Den friske kørsel i step 7A står nedenfor. **Forberedelse færdig, fysisk test
afventer**. Step 7's endelige godkendelse afventer step 6's fysiske
kontrolpunkter; brugeren har særskilt bestilt det foreløbige softwarereview i 7A.
Ingen commit, push eller PR; #54, #43 og #46 forbliver åbne.

## Step 7A – foreløbigt review og friske softwarekontroller

Den 10. november 2026 er den delte leverance gennemgået, inklusive nye filer:
tunnelindstillingen, opstartskaldet, den interne hjælpeklasse, tre testfiler og
denne specifikation. Intet er staged. Personlig noCase, hemmeligheder, lokal
konfiguration, prøveværktøjer og TestResults indgår ikke i den foreslåede delte
ændring. Eksisterende referencedokumenter og skolemateriale bevares.

Reviewet fandt ingen fejl, der krævede ændringer i produktionskode eller tests.
Originalform og Uri-validering, logniveau/strukturerede felter, opstartsplacering,
fælles endpoint/session, identiteter og forsinket gammel oprydning er gennemgået.
De seks transportcases sammenligner hele GameStateResponse gennem den fælles
assertionshjælper og kontrollerer lobbyidentiteter separat. Afvisningslogs,
protokolsvar og Completion bruges til synkronisering under fixturets deadline.

To dokumentationspunkter er håndteret: en ny udvikler havde ikke en selvstændig
opstartsrækkefølge uden de ignorerede prøveværktøjer, og den nye status skulle
skelne mellem 7A og endelig fysisk godkendelse. Den manuelle vejledning ovenfor
indeholder nu bygge-/opstartssekvens, normal content root, faktisk endpoint og
upstream, tom genstart før join, hardwareforudsætninger og relevante kontroller.
De fysiske rækker er fortsat åbne. Den lokale reviewrapport registrerer fil/linje,
konsekvens og håndtering; disse punkter er ikke produktionsfejl.

**Friske resultater i run-oh66m6ap:**

| Kontrol | Faktisk resultat i step 7A |
|---|---|
| Restore og Release-build | Består med 0 fejl og 0 advarsler; SDK 9.0.304 og net8.0-target. |
| Hele backendtestsuiten | 1.434 udførte og beståede tests; ingen fejl eller oversprungne. Alle 49 konfigurationscases og seks transportcases er særskilt kontrolleret i TRX, inklusive aktiv/ventende/afsluttet i Development og Production. |
| Frontend | Syntaks for app.js og bridge.js består. Node 22.19.0 kører 39 tests, alle består; ingen fejlede, annullerede, oversprungne eller todo. TAP og procesfejlkode er kontrolleret. |
| Fællessketch | Isoleret kopi med withCase fra HEAD og ingen faktisk secrets-fil kompilerer til arduino:samd:mkrwifi1010: 106.640 bytes flash og 7.988 bytes globale variable. Original personlig noCase er bevaret. Ingen upload. |

Arduino IDE 2.3.10, CLI 1.5.1, arduino:samd 1.8.14, Carrier 2.1.0,
WiFiNINA 2.1.1 og ArduinoHttpClient 0.6.2 er genbrugt uden opgraderinger.
Første kopikontrol stoppede, fordi den sammenlignede Windows' CRLF med Gits LF
som forskellige kildefiler. Prøvehjælperen accepterer nu kun denne forskel og
bygger kopien fra HEAD-bytes. Det indledende FAIL og den efterfølgende beståede
kompilering er bevaret særskilt. Ingen Arduino-kildefil blev ændret.

SHA-256 for alle 109 tidligere projektfiler ud over specifikationen matcher
grundlaget fra step 4–6. Derfor bevares TCP/WSS-resultaterne som dateret evidens:
lokal TCP i run-cknjfv9n, offentlig WSS i run-3b_1rrvr og værtskontrol i
run-821k_pba. Alle er fra 10. november 2026; ingen ny offentlig tunnel er startet
i step 7A, og der er ingen transport-/opstartsrettelse, som kræver en ny prøve.
Dette er en kontrol af uændret grundlag, ikke en ny hardwareverifikation.

Nye logs, TRX, TAP, Arduino-resultat, reviewrapport og kontrolopsummering ligger
under den Git-ignorerede `TestResults/issue54-step7/run-oh66m6ap/`.
Slutkontrollen består med streng UTF-8, 23 lokale fillinks, ét internt anker,
to JSON-eksempler og syntaksparser for alle fem PowerShell-blokke uden udførelse.
Git-diff og fuldfil-whitespacekontrol består med korrekt håndtering af CRLF/LF.
Alle 109 tidligere projektfiler ud over specifikationen og 4.545 tidligere
artefakter ud over den tilsigtet opdaterede interne plan er byte-for-byte bevaret.
Skoleplan, arbejdslog, begge Arduino-referencefiler og noCase-hash er uændrede.
`summary.json` indeholder filmanifest, reviewpunkter, faktiske tællere og status.
**Step 7A: foreløbigt softwarereview gennemført. Step 6: fysisk test afventer.
Step 7: endelig godkendelse afventer fysisk evidens.** Efter fysisk test
gennemgås resultater og eventuelle rettelser; berørte kontroller gentages,
før step 7 afsluttes. I step 7A blev der ikke udført commit, push eller PR.
Den daværende rækkefølge med fysisk godkendelse før softwaremerge er ændret
ved brugerens efterfølgende beslutning i step 8 nedenfor.

## Step 8 – softwareaflevering før fysisk fællestest

Den aftalte rækkefølge fra 10. november 2026 er software-PR, grøn CI, merge,
ny CI og lokal efterkontrol på main, efterfulgt af gruppens online fællestest
med de fysiske Oplà'er. De syv delte filer omfatter tunnelkonfiguration og
opstartslog, de 49 konfigurationscases, seks transportskiftcases og denne
specifikation. Den personlige `noCase()`-ændring, lokale indstillinger,
hemmeligheder og prøveartefakter følger ikke med.

Software-PR'en henviser med `Refs #54` og lukker ikke issuet. Testgrundlaget er
step 7A's softwarekontroller samt de daterede TCP-/WSS-resultater ovenfor;
ændret kode eller integration kræver nye berørte kontroller. PR-CI kontrolleres
for det seneste PR-hoved, og særskilt push-CI kontrolleres på merge-committen.
Faktiske SHA'er, PR-/CI-links, TRX/TAP, logs og lokal efterkontrol registreres
under den ignorerede `TestResults/issue54-step8/` og i PR'ens afleveringsstatus.

Fysisk USB, WiFi uden USB-data, aktivt og ventende transportskift samt
fællestesten med Abdi er fortsat ikke afprøvet. Kør den senere fysiske protokol
fra step 6 mod den leverede main-version og registrér den anvendte commit,
deltagere, tidspunkt, forventet/faktisk resultat og evidens. Herefter gennemgås
resultaterne i den endelige step 7. En særskilt dokumentations-/eventuel
rettelses-PR med `Closes #54` afslutter først opgaven, når alle krav er opfyldt.
#43 og #46 beholder deres egne udestående krav.

## Færdigkriterier og samlet aflevering

Step 1 er færdigt, når specifikationen er sammenholdt med alle fem opgavepunkter
i #54 og den eksisterende kode, konfigurationskontrakten er entydig, og UTF-8,
lokale links, JSON-eksempler og whitespace er kontrolleret. Den interne plan
opdateres med step 1 afsluttet og step 2 som næste trin.

Step 2 er færdigt, når den aftalte konfiguration og opstartslog er implementeret,
de målrettede tests, hele backendtestsuiten og de isolerede procesprøver består,
og dokumentation og diff er kontrolleret. Den interne plan opdateres herefter
med step 2 afsluttet og step 3 som næste trin. Step 1's færdigkriterier ovenfor
bevares som historik.

Step 3 er færdigt, når de gentagne overtagelser består i alle tre spiltilstande
og begge miljøer, de eksisterende regressionstests består, og krav/test-matricen
viser koblingen til de konkrete tests. Den interne plan opdateres med step 3
afsluttet og step 4 som næste trin. Step 1 og 2's kriterier bevares som historik.

Step 4 er færdigt, når Production-miljø, rigtig TCP-klient, alle otte forløb,
snapshotsammenligninger og oprydning består med gemt diagnostik, og dokumentation
og diff er kontrolleret. Den interne plan opdateres med step 4 afsluttet og
step 5 som næste trin. De tidligere færdigkriterier bevares som historik.

Step 5 er færdigt, når den rigtige offentlige HTTPS-/WSS-prøve, alle otte
klientforløb, normal TLS-validering, faktiske opstartslogfelter og oprydning
består med gemt diagnostik. Specifikation og intern plan opdateres med faktiske
resultater, step 5 afsluttet og step 6 som næste trin. De tidligere kriterier
bevares som historik.

Step 6 er først færdigt, når USB, ren WiFi uden USB-data, begge transportretninger
under aktiv/ventende tur, fortsat spil og fællestesten med Abdi er fysisk
dokumenteret. Prøveværten, softwarekontroller og kompilering erstatter ikke
disse observationer. Et andet fysisk netværk registreres kun som afprøvet, hvis
det faktisk blev brugt. Herefter kan den endelige kontrol i step 7 gennemføres.

Step 8's softwareleverance er færdig, når softwaren er merget gennem en PR med
`Refs #54`, CI på main og lokal efterkontrol består, og den personlige sketch
og tidligere filer er bevaret. Fysisk test og endelig godkendelse står åbne.

#54 forbliver åben efter softwareleverancen. Den samlede aflevering kræver
resultater for alle rækker i matricen, dokumenteret fysisk fællestest og en
afsluttende PR med `Closes #54`. Projektets proces kræver grøn CI; efter merge
kontrolleres også CI på den præcise merge-commit. #43 og #46 beholder deres
selvstændige krav.
