# WebSocket til Oplà i opgave #63

Denne specifikation fastlægger ansvar, forbindelsestilstande og integrationsgrænser for [#63 – WebSocket /ws/opla](https://github.com/Bmantaz/H3-Yatzi/issues/63). Den beskriver transporten, beskedkontrollen, READY/adgangen, handlerintegrationen og slutkontrollen fra de seks interne steps på branchen `S2-12-websocket-opla`.

**Status efter lokal slutkontrol i step 6:** `/ws/opla` fungerer i alle miljøer med eget forbindelsesregister, beskedgrænser, streng READY-genkendelse, serverstyret adgang og den eksisterende `OplaMessageHandler`. En serverparret forbindelse kan udføre ROLL/HOLD gennem den fælles `GameSession` og modtage STATE på samme forbindelse. Standardbehandleren for READY udsteder ingen kode, binder ingen spiller og sender intet svar. Rigtig parring følger i #59; indtil da bruger de automatiske tests testværtens kontrollerede serverbinding. Ekkoet er fjernet. Grænserne er 12 samtidige eller reserverede forbindelser, 256 payload-bytes, 64 Unicode-kodepunkter og 20 tekstbeskeder i et rullende sekund pr. forbindelse. Almindelig HTTP uden opgradering giver 400 i alle miljøer; fuldt register giver 503. PR og CI dokumenterer afleveringen til review; #63 er først afsluttet efter merge og grøn CI på main.

Den eksisterende [kommunikationsprotokol](protocol.md) og dens beskeder er bevaret. Hvor protokollen angiver omtrentlige ressourcegrænser, fastlægger dette dokument de konkrete værdier til #63.

## Implementeret transport og handlerintegration i step 2–6

| Komponent | Implementeret ansvar |
|---|---|
| [OplaWebSocketEndpoints](../backend/Yatzi.Api/OplaWebSocketEndpoints.cs) | Registrerer den præcise route, kontrollerer opgradering/kapacitet, samler tekstfragmenter og validerer beskeder ens i alle miljøer. Behandler READY og adgang før ét handlerkald; sender hver svarlinje særskilt til afsenderen. Ejer lukning og disposal; requesten afventer hele forbindelsens levetid. |
| [OplaConnectionRegistry](../backend/Yatzi.Api/OplaConnectionRegistry.cs) | Singleton med atomisk reservation af de 12 pladser, servergenererede Guid-identiteter, sessionopslag, READY-registrering, spillerbinding/adgangsopslag og afsendelse til en bestemt modtager. Reservationer tæller med, før accept er afsluttet. |
| [OplaConnection](../backend/Yatzi.Api/OplaConnection.cs) | Én session pr. accepteret socket. Tilstand og spiller-id beskyttes samlet. Afsendelser på samme socket serialiseres med SemaphoreSlim. Lukning tilbagekalder straks adgang og har en samlet frist på fem sekunder, inklusive ventetid på afsendelseslåsen. |
| [OplaMessageRateLimiter](../backend/Yatzi.Api/OplaMessageRateLimiter.cs) | Én tæller pr. forbindelses modtagelsesløkke. Gemmer højst 20 monotone tidsstempler og afviser besked 21 inden for et rullende sekund. Kender hverken WebSocket, parring eller spil. |
| [IOplaReadyHandler og PendingOplaReadyHandler](../backend/Yatzi.Api/IOplaReadyHandler.cs) | Serverintern grænse til #59 og statsløs standard uden parring eller svar. Modtager forbindelses-id, eventuel kode og annullering. |
| [OplaConnectionState](../backend/Yatzi.Api/OplaConnectionState.cs) | Tilstandene `Connected`, `Ready`, `Paired` og `Closed`. Closed gælder fra lukningens start, før netværksarbejdet er afsluttet. |

[Program.cs](../backend/Yatzi.Api/Program.cs) registrerer registeret, systemets `TimeProvider` og `IOplaReadyHandler` med `PendingOplaReadyHandler` som statsløs singleton. #59 erstatter denne implementering med lobby/parring. Endpointet modtager den allerede registrerede singleton `OplaMessageHandler`, som deler `GameSession` med HTTP-API'et. `UseWebSockets()` kaldes ét centralt sted for både `/ws/opla` og `/ws/spike`; spikens øvrige adfærd er bevaret. `TimeProvider` lader testene styre ratens tidsstempler og udløse lukkegrænsen uden faste ventepauser. Ratetælleren oprettes direkte i modtagelsesløkken og registreres ikke som singleton.

Der returneres ikke ekko. Gyldigt READY registreres og videresendes til READY-behandleren uden Warning. Øvrige enkeltlinjer kræver en servervalideret spillerbinding. Uden binding gives én transport-Warning om manglende READY/parring og intet svar. Med binding leveres linjen uændret til handleren, som accepterer ROLL/HOLD eller afviser med sin egen Warning. Transporten gentager ikke denne afvisningslog. Mellemrum alene er ikke tom tekst og når derfor adgangskontrollen og eventuelt handleren.

I alle miljøer afvises almindelig HTTP uden WebSocket-opgradering med 400, og et 13. forbindelsesforsøg får 503 uden opgradering. Binære beskeder, ugyldig UTF-8 og payload over 256 bytes medfører lukning. Bufferen gemmer højst 256 payload-bytes; en særskilt prøvebyte opdager overskridelsen, også inden sidste fragment. Fragmenteret UTF-8 afkodes først, når hele beskeden er modtaget.

Oprydningen håndterer normal lukning, netværksfejl, request-annullering og serverstop. Nye afsendelser afvises, når lukning starter. Derefter lukkes eller afbrydes socketen, den disposes, og reservationen frigives. Sessionens `Completion` afsluttes først efter oprydningen. Registerets lås holdes ikke under netværksarbejde, og transporten benytter ikke `GameSession`-låsen. Et stille netværksudfald kan først ryddes op, når den underliggende transport registrerer det; der er ikke tilføjet en heartbeat-protokol.

### Beskedkontrol fra step 3

Bytegrænsen og afvisning af binære beskeder håndhæves under modtagelsen. Når sidste tekstfragment er modtaget, udføres kontrollerne i denne rækkefølge:

1. **Rate:** Den komplette tekstbesked tæller én gang før afkodning eller indholdskontrol, også ved tom tekst, debug, READY eller ugyldigt input. Fragmenter og udgående svar tæller ikke særskilt. Besked 21 i vinduet lukker med `PolicyViolation` (1008) uden protokolsvar.
2. **UTF-8:** Hele beskeden afkodes strengt. Ugyldig kodning lukker med `InvalidPayloadData` (1007).
3. **Kodepunkter:** `EnumerateRunes()` tæller højst 64 Unicode-kodepunkter, inklusive mellemrum og debugpræfiks. Kombinerende tegn tæller selvstændigt. Overskridelse lukker med `MessageTooBig` (1009), ligesom overskridelse af bytegrænsen. 64 firebyte-emojis fylder 256 bytes og består længdekontrollerne, men skal stadig bestå de følgende kontroller; 65 ASCII-tegn afvises.
4. **Enkeltlinje:** Tom tekst eller ethvert CR/LF ignoreres med én Warning og intet svar. Forbindelsen kan fortsætte med næste besked. Teksten trimmes, splittes eller normaliseres ikke.
5. **Debug:** En linje med `#` som første tegn ignoreres uden svar eller Warning, når de foregående kontroller er bestået. `# debug\nROLL` afvises derfor som linjefejl.
6. **READY, adgang og handler:** Gyldigt READY registreres og leveres til READY-behandleren. Øvrig tekst kræver en spillerbinding og går derefter uændret til `OplaMessageHandler.HandleLine`. Hver returneret linje sendes særskilt til samme forbindelse; en tom svarliste giver ingen afsendelse.

Ratevinduet bruger `TimeProvider.GetTimestamp()` og `GetElapsedTime()`, ikke vægurets klokkesekunder. En post med alder præcis ét sekund er udløbet; yngre poster bliver i køen. Hver forbindelse har sin egen kø, som kun bruges af dens modtagelsesløkke. Genforbindelse starter med en ny tæller.

Transportafvisninger logges én gang på Warning-niveau med `ConnectionId`, årsag og relevant længde eller antal. Rå tekst og parringskoder logges ikke. Den første fejlede kontrol afgør afvisningen; eksempelvis får en ugyldig UTF-8-besked på plads 21 kun rateafvisningen. Lukning bruger fortsat den fælles oprydning og femsekundersgrænsen fra step 2.

### READY og serverbinding fra step 4

Kun præcis `READY` eller `READY cccc` med fire ASCII-cifre genkendes. `READY 0012` leverer koden `0012` som tekst; `READY 0000` er også syntaktisk gyldig. Koden genkendes eller valideres først af #59. Ugyldige varianter giver ingen tilstandsændring eller READY-kald. Klientens `PAIR` og `JOINED` kan heller ikke oprette en binding.

Endpointet kalder `TryMarkReady` før `IOplaReadyHandler.HandleReadyAsync(ConnectionId, code, cancellationToken)` og afventer behandleren uden register- eller tilstandslås. Alle gyldige gentagelser leveres, også efter parring, fordi Arduinoen gentager READY under opstart og genforbindelse. Gentagelser opretter ingen ekstra session og fjerner ikke en eksisterende binding. Kaldet bruger requestens og serverstoppets kombinerede annullering; fejl bruger endpointets eksisterende oprydning, uden baggrundsopgave eller genkørsel.

`TryBindPlayer` kræver en accepteret, stadig åben session efter READY og et ikke-tomt `Guid PlayerId`. Gentaget binding til samme spiller lykkes uden ændring; en anden spiller afvises. `TryGetPlayerId` læser tilstand og identitet samlet og returnerer kun adgang for en registreret, parret forbindelse. Lukkende forbindelser og reservationer giver `false` og `Guid.Empty`. #59 ejer spiller-id'ets oprindelse og de logiske regler for koder og spillerrelationer; transporten opfinder ingen spiller eller kode.

Lukning markerer straks `Closed` og tømmer bindingen, inden netværkslukningen afventes. `Remove` tilbagekalder også adgang på gamle objektreferencer uden selv at lukke eller dispose socketen. Tilstand og identitet beskyttes af én kort tilstandslås. Registeroperationer finder sessionen under registerlåsen og udfører kun synkrone tilstandsoperationer der; ingen netværksoperation udføres under disse låse. Oprydning bruger kun det konkrete `ConnectionId`, så en nyere forbindelses binding bevares.

### Handler og svar fra step 5

Endpointet kontrollerer annullering og spillerbinding uden et `await` før det synkrone handlerkald. Adgangstjekket er accepttidspunktet: en allerede lukket forbindelse afvises, mens en godkendt handling kan færdiggøres, selv om forbindelsen lukker bagefter. Spilreglerne gælder stadig i handleren og kernen.

Handleren kaldes præcis én gang pr. accepteret komplet linje. Den eksisterende signatur `IReadOnlyList<string> HandleLine(string? line)` og STATE-formatet er bevaret. `PlayerId` er adgangsbetingelsen i transporten; handleren modtager ikke spiller-id og arbejder fortsat på serverens fælles enkeltspillerspil. To parrede testforbindelser har ikke hvert sit spil og dokumenterer ikke multiplayer eller turkontrol i #44.

Handleren afslutter spilhandlingen og kopierer tilstanden under `GameSession`-låsen, før netværksafsendelse afventes. Svar sendes i rækkefølge som individuelle tekstbeskeder gennem `OplaConnection.SendAsync`. Et ventende svar beholder handlingens eget STATE, selv om HTTP eller en anden forbindelse ændrer spillet. Der foretages intet ekstra spilopslag eller nogen broadcast.

Hvis afsendelsen returnerer `false`, forlades modtagelsesløkken. Sendesvigt og annullering bruger den eksisterende fejlgrænse og oprydning. En udført handling rulles ikke tilbage, gemmes ikke til genafspilning og gentages ikke; dens svar flyttes heller ikke til en ny forbindelse. Ingen register-, tilstands- eller spilsessionslås holdes under netværksafsendelsen.

### Kontrol og lokal afprøvning af step 2–6

De nye [endpoint-tests](../backend/Yatzi.Tests/OplaWebSocketEndpointTests.cs) bruger den rigtige app-pipeline og WebSocket-testklienten i hukommelsen. [Levetidstestene](../backend/Yatzi.Tests/OplaWebSocketLifetimeTests.cs) fremkalder acceptfejl, transportfejl, annullering og lukke-timeout. [Sessionstestene](../backend/Yatzi.Tests/OplaConnectionTests.cs) kontrollerer afsendelsesrækkefølge og lukning, og [registertestene](../backend/Yatzi.Tests/OplaConnectionRegistryTests.cs) kontrollerer reservationer og modtagere. Testene bruger [kontrollerede transportobjekter](../backend/Yatzi.Tests/OplaTransportTestDoubles.cs), ikke fysisk hardware.

Step 3 udvider endpoint- og levetidstestene med kodepunkter, fragmentering, rate, afvisninger, logging og fortsættelse efter linjefejl. [Ratetællerens tests](../backend/Yatzi.Tests/OplaMessageRateLimiterTests.cs) dækker 20/21, præcis ét sekund, delvist udløb, skift af klokkesekund og uafhængighed af væguret. Testene styrer tiden og afventer operationerne uden faste ventepauser.

Step 4 erstatter ekkobaseret synkronisering med READY- og logsignaler gennem channels samt eksplicit afsluttet lukning. Testudgaven af READY-behandleren kan binde en spiller serverinternt; den findes kun i testprojektet og tilgås ikke gennem en klientkommando, testrute eller produktionsindstilling. [Standardbehandlerens tests](../backend/Yatzi.Tests/PendingOplaReadyHandlerTests.cs) kontrollerer, at den ikke giver adgang eller svar og respekterer annullering. Endpoint-, register- og levetidstestene dækker strengt READY-format, gentagelser, konkurrerende bindinger, tilbagekaldt adgang under lukning, sen binding, genforbindelse og fejl/annullering under READY-behandling.

[Spiltransportens integrationstests](../backend/Yatzi.Tests/OplaWebSocketGameIntegrationTests.cs) tilføjer 32 testcases fra step 5 med den rigtige handler, servicecontainer og HTTP-API. De dækker ROLL/HOLD og STATE, bevarede terninger, HTTP-samspil og nyt spil, handlerafvisninger uden dobbeltlogning, præcise modtagere, fragmentering, samtidige kast og transportfejl. Kontrollerede sockets viser, at en ventende afsendelse ikke spærrer HTTP eller andre forbindelser, og at fejl, `false` og annullering ikke gentager et accepteret kast. Tests synkroniserer på operationer, READY og logs uden faste ventepauser.

Step 6 fjerner den midlertidige Development-begrænsning og endpointets miljøafhængighed. Miljøtestene dækker Development, Production, Staging og Test: HTTP uden opgradering giver 400, WebSocket accepteres, og standardbehandleren giver ingen kode eller adgang. En særskilt Production-test med kontrolleret serverparring verificerer rigtig ROLL/STATE og fælles HTTP-spiltilstand. Ingen testbinding kan aktiveres gennem klienten eller konfigurationen.

**Verificeret lokalt efter step 2:** Release-build bestod med 0 fejl og 0 advarsler. Hele testsuiten bestod med **669 tests**, heraf de 612 eksisterende og 57 nye; ingen blev sprunget over. Diff-/whitespacekontrol og dokumentets 17 lokale links bestod. En separat lokal Kestrel-server blev desuden startet med `--environment Development`: en rigtig WebSocket returnerede `READY` uændret og lukkede normalt. Med `--environment Production` gav `/ws/opla` HTTP 503. Testserverne blev stoppet efter kontrollen. Dette er lokale resultater; ny GitHub-CI og fysisk hardware er ikke afprøvet i step 2.

**Verificeret lokalt efter step 3:** Release-build bestod med 0 fejl og 0 advarsler. Hele testsuiten bestod med **720 tests**; ingen fejlede eller blev sprunget over. Det omfatter de eksisterende regressionstests samt de nye grænse-, format- og ratetests. Diffkontrollen, scope-/UTF-8-/whitespacekontrollen af alle 13 ændrede eller nye filer og dokumentets 19 lokale links bestod. Kontrollen bruger testserveren i hukommelsen og kontrollerede transportobjekter; fysisk Oplà-integration og ny GitHub-CI er fortsat ikke verificeret.

**Verificeret lokalt efter step 4:** Release-build bestod med 0 fejl og 0 advarsler. Hele testsuiten bestod med **759 tests**, ingen fejlede eller blev sprunget over. Diffkontrollen, scope-/UTF-8-/whitespacekontrollen af alle 16 ændrede eller nye filer og dokumentets 22 lokale links bestod. Testene bruger den rigtige app-pipeline i hukommelsen og kontrollerede transportobjekter. Fysisk Oplà-integration og ny GitHub-CI er fortsat ikke verificeret. Resultaterne fra step 2–3 ovenfor er historiske; deres ekko er fjernet i step 4.

**Verificeret lokalt efter step 5:** Release-build bestod med 0 fejl og 0 advarsler. Hele testsuiten bestod med **791 tests**, heraf de 759 fra step 4 og 32 nye integrationstestcases. Ingen fejlede eller blev sprunget over. Diffkontrol samt scope-/UTF-8-/whitespacekontrol af alle 21 ændrede eller nye filer bestod. Alle 46 lokale links i WebSocket-specifikationen (23), handlerdokumentationen (13) og README (10) bestod. Kontrollen er automatisk i hukommelsen uden åbne netværksporte eller fysisk hardware. Ny GitHub-CI og fysisk Oplà-integration er fortsat ikke verificeret.

**Lokal slutkontrol i step 6:** Restore og Release-build bestod med 0 fejl og 0 advarsler. Hele testsuiten bestod med **794 tests**, ingen fejlede eller blev sprunget over. Samlet gennemgang, diff-/scope-/UTF-8-/whitespacekontrol af alle 21 ændrede eller nye filer og dokumentationens 46 lokale links bestod. En separat Kestrel-server i Production blev afprøvet med en rigtig WebSocket-klient på localhost: HTTP uden opgradering gav 400, WebSocket blev accepteret, READY/ROLL/HOLD gav ingen svar eller spilændring uden parring, og 65 tegn lukkede med 1009. Normal lukning og genforbindelse bestod; testserveren blev stoppet, og porten var frigivet. STATE-samspillet er verificeret automatisk med kontrolleret serverparring; ingen fysisk Oplà-test er gennemført. Tidligere steps' miljøafvisninger ovenfor er historiske. CI-resultater og afleveringsstatus dokumenteres i den samlede PR for #63.

Kør fra repositoryets rod:

```powershell
dotnet build H3Yatzi.sln --configuration Release
dotnet test H3Yatzi.sln --configuration Release --no-build
dotnet run --project backend/Yatzi.Api
```

Til en manuel transportprøve åbnes `http://localhost:5000`, og følgende køres i browserens udviklerkonsol:

```javascript
const oplaSocket = new WebSocket(`${location.origin.replace(/^http/, "ws")}/ws/opla`);
oplaSocket.onmessage = event => console.log("Svar:", event.data);
oplaSocket.onclose = event => console.log("Lukket:", event.code);
oplaSocket.onopen = () => oplaSocket.send("READY 0012");
// Efter forbindelsen er åbnet: oplaSocket.close();
// Alternativ længdeprøve: oplaSocket.send("x".repeat(65)); // lukker med 1009
```

Forvent med standardbehandleren en åben forbindelse uden svar på READY: intet ekko, ingen parringskode og intet STATE. `ROLL` eller `HOLD 1` giver heller intet svar eller spilændring uden parring; serveren logger den manglende adgang. STATE-integrationen afprøves derfor gennem den kontrollerede serverparring i de automatiske tests, indtil #59 er klar. I en ny forbindelse kan 21 hurtige READY-beskeder afprøve lukning med 1008. Bruges USB-browser-broen, er venten på rigtig parring fortsat forventet. Alle miljøer har samme adfærd. De manuelle trin erstatter ikke automatiske tests eller fysisk Oplà-integration.

Ekkoet og Development-begrænsningen er fjernet. Der er ingen konfigurationskontakt til ekko eller til at omgå parring.

## Ansvar og eksisterende udgangspunkt

| Komponent eller opgave | Ansvar |
|---|---|
| [#63 – WebSocket](https://github.com/Bmantaz/H3-Yatzi/issues/63) | Åbner og lukker forbindelser, registrerer en session pr. forbindelse, samler og validerer beskeder, håndhæver grænser og sender svar til den rigtige forbindelse. |
| [#59 – Lobby og parring](https://github.com/Bmantaz/H3-Yatzi/issues/59) | Tildeler og genkender parringskoder, knytter Oplà til spiller og danner `PAIR cccc` og `JOINED navn`. Ejer også lobbyens HTTP-endpoints. |
| [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) | Behandler accepterede `ROLL`- og `HOLD n`-linjer gennem den eksisterende `GameSession` og returnerer `STATE`. Format, fejl og logging er beskrevet i [handlerdokumentationen](opla-handler.md). |
| [GameSession](../backend/Yatzi.Api/GameSession.cs) | Ejer den fælles spiltilstand og beskytter spilhandlinger og kopieringen af svar under samme lås. |
| [Yatzi.Core.Game](../backend/Yatzi.Core/Game.cs) | Håndhæver reglerne for kast, hold/slip, pointvalg og afslutning af den aktuelle tur. |
| [#44 – Multiplayer](https://github.com/Bmantaz/H3-Yatzi/issues/44) | Bestemmer aktiv spiller, kontrollerer hvis tur det er, skifter tur, finder vinder og styrer `STATE`, `WAIT` og `END` ved turskifte og afslutning. |

En **forbindelsessession** beskriver én åben WebSocket-forbindelse. Den erstatter ikke `GameSession` og opretter ikke et selvstændigt spil pr. Oplà. HTTP-API'et og Oplà-handleren skal fortsat bruge samme spilsession, jf. [API-kontrakten](api.md).

Før step 2 registrerede [Program.cs](../backend/Yatzi.Api/Program.cs) alene spillets session og handler og havde kun WebSocket-spiken. Det eksisterende [Spike.cs](../backend/Yatzi.Api/Spike.cs) tilbyder fortsat `/ws/spike`, hvor `READY` giver den faste testkode `PAIR 1234`. Det er ikke rigtig parring eller en forbindelse til spillogikken.

## Dataflow og komponenternes samarbejde

Diagrammets handler- og STATE-forløb er implementeret i step 5. Med standardbehandleren stopper spilkommandoer fortsat ved adgangskontrollen, indtil #59 binder en spiller. Testværten kan kontrollere resten af forløbet med serverintern parring.

```text
Oplà → USB/Web Serial → browser-bro → WSS /ws/opla
                                      ↓
                            session for forbindelsen
                                      ↓
                      beskedformat og ressourcegrænser
                         ├─ # debug → ignoreres
                         ├─ READY [kode] → grænsen til #59
                         └─ øvrig linje → adgangskontrol
                                           ↓
                                OplaMessageHandler
                                           ↓
                             GameSession → Yatzi.Core.Game
                                           ↓
                                kopieret spiltilstand
                                           ↓
                         STATE → samme WebSocket → Oplà
```

Browser-broen transporterer tekstlinjer. Serveren læser ikke en USB-port. Direkte Wi-Fi kan senere bruge samme endpoint og samme beskeder uden at ændre grænserne nedenfor.

Transportlaget registrerer forbindelsesstatus og kontrollerer adgang før et handlerkald. Handleren bestemmer spilhandlingen og danner svaret. Hverken en transportfejl, et `READY` eller en genforbindelse opretter eller nulstiller et spil.

Parring er en nødvendig adgangsbetingelse, men afgør ikke, hvis tur det er. Spillerens identitet skal følge forbindelsen, så #44 senere kan afvise en anden spillers handlinger før udførelse. #63 indfører ikke turskifte eller multiplayer i den nuværende kerne.

## Forbindelsestilstande og adgang

Hver forbindelse får et servergenereret `ConnectionId`, som ikke er parringskoden. Sessionen indeholder en læsbar `OplaConnectionState` og en eventuel servervalideret spilleridentitet som ikke-tomt `Guid PlayerId`. Spilleridentiteten er hverken visningsnavn, parringskode eller position i spillerlisten. De konkrete kode- og spillermodeller hører til #59 og #44.

| Tilstand | Hvordan opstår den? | Adgang til spilkommandoer |
|---|---|---|
| **Ny forbindelse** | WebSocket-forbindelsen er accepteret og registreret. | Ingen. |
| **READY modtaget** | Serveren har modtaget præcis `READY` eller `READY cccc`, hvor `cccc` er fire ASCII-cifre. | Ingen; en medsendt kode er endnu ikke valideret. |
| **Parret** | Serverens parringslogik har knyttet en spiller til en åben forbindelse, der har sendt READY. | Linjer kan gå videre til handleren; spilregler og den senere turkontrol gælder stadig. |
| **Lukket** | Normal lukning, afbrudt forbindelse, serverstop eller lukning efter en overskredet grænse. | Ingen; sessionen fjernes fra registeret. |

Det normale forløb er `Ny forbindelse → READY modtaget → Parret → Lukket`. Forbindelsen kan også lukkes fra de to første tilstande. En lukket session genbruges ikke.

- Kun serverens parringslogik kan markere en forbindelse som parret. `PAIR`, `JOINED`, et spillernavn eller en kode sendt af klienten kan ikke gøre det.
- Et gyldigt READY registreres og leveres til integrationspunktet for #59. Et gentaget gyldigt READY på samme forbindelse opretter ikke en ekstra session og giver ikke ekstra rettigheder. Det fjerner heller ikke en eksisterende valideret binding; eventuelle ændringer af bindingen tilhører #59.
- Et ugyldigt READY-format ændrer ikke sessionen. Eksempler er `ready`, `READY `, `READY 123`, `READY abcd` og ekstra felter.
- Før READY og parring afvises linjer, der ellers kunne nå spilhandleren: intet handlerkald, ingen spilændring og intet svar. Afvisningen logges én gang.
- Debuglinjer med `#` først ignoreres efter kontrollen af beskedformat og ressourcegrænser og kræver ikke parring.

### Genforbindelse

En genforbindelse får et nyt `ConnectionId` og starter som **Ny forbindelse**. `READY 4821` er en anmodning om, at #59 genkender koden; teksten er ikke i sig selv dokumentation for parring. Kun #59 kan genetablere spillerbindingen på den nye forbindelse.

Transportregisteret fjerner den gamle forbindelse og dens adgang. #59 ejer den logiske kode/spillerrelation, der senere skal kunne overleve et kort forbindelsesudfald. En sen oprydning af den gamle forbindelse må ikke fjerne en binding på en nyere forbindelse. Der flyttes eller genafspilles ingen tidligere spilkommandoer.

## Serverinterne integrationsgrænser

Transportens serverinterne operationer er implementeret i step 4. De opretter ingen nye HTTP-ruter eller klientkommandoer. Den rigtige lobbyimplementering af READY følger i #59:

| Operation | Input og ansvar | Forventet resultat |
|---|---|---|
| **Registrér READY: `TryMarkReady(Guid)`** | Endpointet leverer `ConnectionId` efter syntakskontrollen. | Connected bliver Ready. Ready/Paired bevares; manglende, reserveret eller lukket session giver false. |
| **Modtag READY: `IOplaReadyHandler.HandleReadyAsync(Guid, string?, CancellationToken)`** | #63 leverer `ConnectionId`, eventuel firecifret kode og annullering efter registrering af READY. Null betyder ingen kode; foranstillede nuller bevares. | Standarden gør intet. #59 kan senere genkende eller tildele kode og sende det relevante svar. READY alene giver ingen binding. |
| **Knyt spiller: `TryBindPlayer(Guid, Guid)`** | #59 leverer `ConnectionId` og servervalideret `PlayerId`. | Åben session efter READY kræves. Tomt id eller en anden eksisterende binding afvises. Gentaget identisk binding lykkes. |
| **Læs adgang: `TryGetPlayerId(Guid, out Guid)`** | Transporten leverer `ConnectionId`. | Kun en registreret, åben, parret forbindelse giver true og spiller-id; ellers false og Guid.Empty. |
| **Send: `SendAsync(Guid, string, CancellationToken)`** | #59 eller #44 leverer `ConnectionId`, én svarlinje uden linjeskift og annullering. | Sendes gennem sessionens fælles afsendelsesvej til præcis modtageren. Manglende eller lukket session giver false. |
| **Fjern: `Remove(Guid)`** | Endpointet frigiver pladsen efter acceptfejl eller afsluttet oprydning. | Bindingen tilbagekaldes også på gamle referencer; pladsen frigives præcis én gang. Metoden udfører intet netværksarbejde. |

Binding, adgangskontrol og oprydning skal bruge en sammenhængende, trådsikker sessionstilstand. To samtidige tilslutninger må ikke få samme `ConnectionId`, og parallelle forsøg må ikke overskride forbindelsesgrænsen.

Alle afsendelser til samme WebSocket, også fremtidige beskeder fra lobbyen og turskifte, går gennem én fælles afsendelsesvej, der udfører afsendelserne efter hinanden. En langsom eller afbrudt modtager må ikke holde `GameSession`-låsen eller blokere andre forbindelsers netværksarbejde.

### Tilkobling til den eksisterende handler

Denne tilkobling er implementeret i step 5. Efter formatkontrol og adgangskontrol leveres én komplet linje til `OplaMessageHandler.HandleLine(string? line)`. Der oprettes ikke en ny `GameSession` eller en separat handler med et andet spil pr. forbindelse.

- Linjen trimmes ikke, og store/små bogstaver ændres ikke. `ROLL ` bliver derfor ikke til et gyldigt `ROLL`.
- `READY` behandles ved grænsen til #59 og sendes ikke til spilhandleren. Debug ignoreres som beskrevet ovenfor.
- Øvrige linjer fra en parret forbindelse sendes til handleren. Den genkender `ROLL` og `HOLD 1`–`HOLD 5` og afviser ukendte kommandoer efter sin eksisterende kontrakt.
- Hver returneret svarlinje sendes som en separat WebSocket-tekstbesked til den samme forbindelse. En tom svarliste giver ingen afsendelse eller ekstra spilopslag.
- Handlerens synkrone spilhandling og kopieringen af tilstanden afsluttes, før transporten afventer netværksafsendelse. Et sendesvigt ruller ikke et allerede accepteret kast tilbage, og transporten gentager ikke kommandoen.

## Beskedformat og ressourcegrænser

Én WebSocket-besked indeholder én protokollinje kodet som gyldig UTF-8. USB's afsluttende `\n` eller `\r\n` fjernes af browser-broen før afsendelse over WebSocket; de accepteres ikke inde i WebSocket-teksten. Ingen kommandoer splittes eller normaliseres på serveren.

En WebSocket-besked kan være fordelt på flere fragmenter. Fragmenterne samles med begrænset hukommelse, inden linjen behandles. Et UTF-8-tegn kan være delt mellem fragmenter; afkodning må derfor ikke behandle hvert fragment som en selvstændig tekst. En delvis besked må aldrig kalde handleren.

| Kontrol | Præcis grænse og adfærd |
|---|---|
| Tegn pr. besked | Højst **64 Unicode-kodepunkter**, inklusive mellemrum og debugpræfiks. Tegn er ikke antal UTF-8-bytes. En besked på 65 tegn lukker forbindelsen uden behandling. |
| Modtagelsesbuffer | Højst **256 payload-bytes** pr. samlet besked, fordi 64 gyldige Unicode-kodepunkter højst fylder 256 UTF-8-bytes. Overskridelse opdages under modtagelsen, også inden sidste fragment, og lukker forbindelsen. Tegngrænsen kontrolleres også efter afkodning. |
| Beskedrate | Højst **20 beskeder i et rullende sekund pr. forbindelse**. Ved tidspunkt `t` tælles modtagne beskeder med alder under ét sekund; en besked præcis ét sekund gammel er udløbet. Besked 21 inden for vinduet lukker forbindelsen uden behandling. Tiden måles monotont. |
| Hvad tælles i raten? | Hver fuldført indgående tekstbesked tæller én gang ved modtagelsen, før kontrol af indhold eller parring. Det omfatter tom tekst, ugyldige kommandoer, debug og READY. Fragmenter tælles ikke som særskilte beskeder. |
| Samtidige forbindelser | Højst **12** åbne eller reserverede Oplà-forbindelser. Plads reserveres atomisk før accept. En ekstra tilslutning afvises med HTTP 503 uden WebSocket-opgradering; en mislykket accept frigiver reservationen. |
| HTTP uden WebSocket-opgradering | Afvises med HTTP 400 og opretter ingen forbindelsessession. |
| Binær besked eller ugyldig UTF-8 | Forbindelsen lukkes uden handlerkald eller spilændring. |
| Tom tekst eller CR/LF i en ellers gyldig tekstbesked | Beskeden ignoreres og logges én gang uden spilændring. Det gælder også flere kommandoer samlet i én tekstbesked. |

Grænserne gælder også før READY og parring. En browser, der filtrerer støj, erstatter ikke serverens kontroller. Udgående linjer sendes uden afsluttende linjeskift; browser-broen tilføjer linjeskift ved skrivning til USB.

## Afvisninger, logging og oprydning

Formatfejl, ukendte kommandoer og afvisninger på grund af manglende READY/parring giver ingen spilændring og intet protokolsvar. Overskredet længde eller rate lukker forbindelsen; grænsen på 12 forbindelser håndhæves inden accept.

Transportlaget logger egne afvisninger på Warning-niveau med `ConnectionId`, årsag og relevant længde eller antal. Ukendt rå tekst, parringskoder og hele payloads skrives ikke i afvisningsloggen. Handlerens eksisterende afvisninger logges fortsat af handleren og gentages ikke af transportlaget. Gyldige READY-beskeder og almindelig debug giver ingen Warning; debug tæller stadig med i ressourcegrænserne.

Normal lukning, kabeludfald via browser-broen, netværksfejl og serverens annullering skal alle ende i samme oprydning. Sessionen fjernes, videre adgang afvises, og pladsen frigives. Lukning og netværksafsendelse udføres uden for den fælles spilsessions lås. En lukkeoperation må ikke vente ubegrænset på modparten; senest efter fem sekunder afbrydes forbindelsen, og oprydningen afsluttes.

## Adfærd indtil lobbyen er implementeret

#63 kan implementeres og automatisk testes før #59. Standardforløbet i alle miljøer er:

1. Oplà-forbindelsen accepteres og får sin egen session.
2. Et gyldigt READY registreres. Der tildeles ingen rigtig kode og sendes intet `PAIR` eller `JOINED`, så længe #59 ikke er tilkoblet.
3. Spilkommandoer forbliver afvist, fordi forbindelsen ikke er parret.
4. Gentaget READY giver ikke adgang eller ekstra sessioner.

Kontrolleret testparring passerer adgangskontrollen og kan udføre handlinger gennem den eksisterende handler. Et spil skal være oprettet, og kernens regler gælder stadig. Produktionsserverens standardbehandler parrer ingen forbindelse, før #59 er tilkoblet.

Automatiske tests kan bruge testværtens servicecontainer til kontrolleret serverintern parring og en testimplementering af READY-behandlingen. Det kræver hverken fysisk hardware eller en offentlig testkommando. Produktionsklienten får ikke adgang til denne testopsætning. Den firecifrede kode er parring og indfører ikke et login- eller JWT-system.

Den tidlige ekko-udgave fra step 2–3 er fjernet. Den kaldte ikke spilhandleren og markerede ingen forbindelse som parret. Den eksisterende `/ws/spike` og dens testkode er fortsat en separat spike og dokumenterer ikke rigtig parring.

[PR #79](https://github.com/Bmantaz/H3-Yatzi/pull/79) behandler browserens støjfiltrering og Arduinoens feedback ved forbindelsestab. Dens ændringer kan indarbejdes uafhængigt af denne backendkontrakt. Fysisk samspil og to spillere gennem tunnelen hører fortsat til [#43 – Integrationstest](https://github.com/Bmantaz/H3-Yatzi/issues/43).

## Kontrolscenarier til step 2–6

Følgende er acceptscenarier for hele #63. Transport, tegn-/rategrænser, linjeformat, READY, serverbinding, adgangskontrol, handler-/STATE-integration og miljøadfærd har automatiske tests og lokal slutkontrol som beskrevet øverst. Rigtig lobbyparring i #59 og fysisk integration er stadig udestående. Afleveringen af #63 kræver desuden merge og grøn CI på main; tabellen dokumenterer ikke disse udeståender:

| Scenarie | Forventet resultat |
|---|---|
| `ROLL` eller `HOLD 1` før READY | Ingen handlerkald, svar eller spilændring; én Warning pr. afvist besked. |
| Gyldigt READY efterfulgt af `ROLL`, uden parring | Sessionen er READY, men kastet afvises uden ændring. READY giver ikke Warning. |
| `READY 4821`, klientens `PAIR 4821` eller `JOINED Andreas` | Klienten får ikke selv adgang. En kode i READY skal først genkendes af #59. |
| Ugyldigt READY eller gentaget gyldigt READY | Ugyldigt format ændrer ingen tilstand. Gentagelser giver ikke ekstra sessioner eller rettigheder. |
| Kontrolleret serverparring før READY eller efter lukning | Bindingen afvises uden ændring af andre sessioner. |
| Kontrolleret serverparring efter READY og derefter kast/hold | Den eksisterende handler ændrer det fælles spil og sender kun STATE til afsenderen. |
| Ukendt kommando eller regelafvist kast fra en parret forbindelse | Intet svar og uændret spil; handlerens Warning logges kun én gang. |
| Lukket forbindelse og ny forbindelse med samme READY-kode | Den nye session starter uden adgang; den gamle session kan ikke længere bruges. |
| Sen oprydning af gammel forbindelse efter genforbindelse | Den nye forbindelses session og eventuelle senere binding bevares. |
| 64 tegn og 65 tegn | 64 består længdekontrollen; 65 lukker uden behandling. Indhold og adgang kontrolleres stadig ved 64 tegn. |
| Flerbyte-tegn og et UTF-8-tegn delt mellem fragmenter | Korrekt samlet afkodning og tegntælling; byte- og tegngrænser kontrolleres uafhængigt. |
| 256 payload-bytes og forsøg på byte 257 før sidste fragment | Buffergrænsen overskrides aldrig. 256 skal stadig bestå UTF-8- og tegntjek; byte 257 udløser lukning. |
| 20 beskeder inden for et sekund og derefter besked 21 | 20 består ratekontrollen; nummer 21 lukker uden behandling. |
| Beskeder på tværs af et klokkesekund samt præcis ét sekund gamle | Vinduet følger beskedernes alder, ikke klokkesekundets skift; præcis ét sekund gamle poster er udløbet. Brug styret tid i testen frem for ventepauser. |
| Debug, ugyldig tekst, tom tekst og READY i ratevinduet | Alle tæller med; debug og gyldige READY giver ikke Warning i sig selv. |
| 12 samtidige forbindelser og et 13. forsøg, også ved parallel accept | Højst 12 accepteres eller reserveres; det ekstra forsøg får 503. |
| Normal lukning, mislykket accept, netværksfejl eller annullering | Pladsen frigives præcis én gang og kan bruges af en ny forbindelse. |
| Modparten afslutter ikke lukkeproceduren | Forbindelsen afbrydes og ryddes op efter højst fem sekunder. |
| Gyldig kommando fordelt på fragmenter eller afbrudt midt i en besked | Præcis ét handlerkald efter sidste fragment; en ufuldstændig besked udfører ingen handling. |
| `ROLL\nHOLD 1`, `ROLL\n`, binær besked eller ugyldig UTF-8 | Ingen spilhandling. CR/LF giver én logget afvisning; binær besked og ugyldig UTF-8 lukker forbindelsen. |
| To forbindelser, samtidige svar og afsendelse fra lobbyens integrationspunkt | Ingen sammenblanding af modtagere og ingen samtidige afsendelser på samme WebSocket. |
| Langsom eller afbrudt modtager under svarafsendelse | Andre forbindelser og HTTP kan fortsætte; en allerede accepteret spilhandling genudføres ikke. |
| Eksisterende HTTP- og handler-tests | Bevarer den fælles session, spilreglerne, STATE-formatet og svarenes selvstændige kopier. |
| Transportens tidlige ekkoafprøvning | Historisk transporttest fra step 2–3. Ekkoet er fjernet og giver ingen adgang eller dokumentation for parring. |

## Færdigkriterier og næste step

**Step 1 er færdigt**, når denne specifikation er gennemgået mod protokollen, handleren og opgaverne #63, #59 og #44, og dokumentets lokale links og whitespace er kontrolleret. Dokumentet skal give en entydig ansvarsfordeling, beskrive dataflow og sessionstilstande samt fastlægge integrationsgrænser, afvisninger, oprydning og kontrolscenarier.

Funktionstests følger implementeringen i hvert step. Step 1 var alene en dokumentationsleverance; de efterfølgende steps har de automatiske kontroller beskrevet øverst. Testresultaterne og de historiske ekkoprøver dokumenterer ikke fysisk Oplà-integration.

**Implementering og lokal slutkontrol er gennemført** med endpointet i alle miljøer, forbindelsernes levetid, beskedkontroller, READY, serverintern spillerbinding, adgangskontrol og handler-/STATE-integration gennem den fælles `GameSession`. Ekkoet er fjernet. Step 6 afleverer arbejdet i én samlet PR med grøn CI til review; merge sker senere. #59 implementerer den rigtige lobbyparring, og #44 tilføjer turkontrol og multiplayer. #63 som helhed er fortsat uafsluttet indtil merge og grøn CI på main.

**#63 er først afleveret**, når alle implementeringssteps og relevante tests er gennemført, ændringerne er merget gennem en PR med `Closes #63`, og CI er grøn. Der bruges én branch og én samlet PR for opgaven. Step 1 afslutter ikke #59, #44 eller den fysiske integrationstest.
