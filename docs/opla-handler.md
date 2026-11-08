# Oplà-beskedhåndtering

`OplaMessageHandler` omsætter Oplàens tekstlinjer til kast og hold/slip i backendens fælles spil. Handleren bestemmer, hvilken handling der kaldes, mens spilreglerne og tilstanden ligger i `GameSession` og `Yatzi.Core.Game`.

Handleren er implementeret med automatiske tests uden fysisk hardware. HTTP-API, handler og lobby bruger samme spilsession. WebSocket-tilkoblingen fra #63 kræver serverparring i alle miljøer. Lobbyen ejer PAIR/JOINED, start og genforbindelse med STATE/WAIT. Trin 6 af #59 fører serverens PlayerId gennem handleren til sessionslåsen: kun første spillers aktuelle Oplà kan påvirke lobbyspillet efter start. Lobbyen koordinerer input med overtagelse og køer svar i rækkefølge. READY genbruger STATE-formatteringen uden at udføre spilhandlinger. Afleveringsstatus, merge og CI for #63 dokumenteres i [PR #80](https://github.com/Bmantaz/H3-Yatzi/pull/80). Fysisk integration udestår; se [lobbyens status](lobby.md).

## Ansvar og dataflow

| Komponent | Ansvar |
|---|---|
| [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) | Genkender `ROLL`, `HOLD n` og debug, logger afvisninger og danner `STATE`-svar. |
| [GameSession](../backend/Yatzi.Api/GameSession.cs) | Ejer enkeltspillerspillet eller lobbyens 1–6 deltagere. Kontrollerer Oplà-identitet, udfører handlingen og kopierer tilstanden under samme lås. |
| [Yatzi.Core.Game](../backend/Yatzi.Core/Game.cs) | Håndhæver kast, hold/slip, pointvalg og låsning af turen efter ét feltvalg. |
| [Transportlaget i #63](../backend/Yatzi.Api/OplaWebSocketEndpoints.cs) | Modtager komplette linjer gennem `/ws/opla`, validerer beskeder og adgang og sender hver handler-svarlinje til den samme forbindelse. |
| [LobbySession](../backend/Yatzi.Api/LobbySession.cs) | Genkontrollerer bindingen, afviser lobbyinput før start og koordinerer handling/svar med start, READY og overtagelse. |

```mermaid
flowchart LR
    Input["WebSocket /ws/opla"] --> Access["Beskedkontrol, READY og parring"]
    Access --> Lobby["LobbySession: binding og beskedrækkefølge"]
    Lobby --> Handler["OplaMessageHandler.HandleLine med PlayerId"]
    Handler --> Session["GameSession: identitet og handling under samme lås"]
    Http["HTTP /api/game"] --> Session
    Session --> Core["Yatzi.Core.Game"]
    Core --> Snapshot["Kopieret spiltilstand under låsen"]
    Snapshot --> Queue["Lobbyens ordnede svarbatch"]
    Queue --> State["STATE til samme WebSocket"]
    Snapshot --> Json["JSON til HTTP-klienten"]
```

Begge indgange bruger samme session, så et hold gennem HTTP påvirker næste kast gennem handleren og omvendt. Hvert svar bygges fra handlingens eget øjebliksbillede. Handleren foretager ikke et ekstra `GetState()`-kald, som kunne læse en anden klients efterfølgende handling.

Et selvstændigt spil oprettes gennem `POST /api/game/new`, jf. [HTTP-kontrakten](api.md). Et nyt selvstændigt spil kan erstatte det tidligere, og handleren følger automatisk med. Lobbyens start opretter derimod 1–6 deltagere og beskytter spillet mod efterfølgende nulstilling med `/api/game/new`. Tilstanden går tabt ved servergenstart.

## Oprettelse og offentlig metode

[Program.cs](../backend/Yatzi.Api/Program.cs) registrerer både `GameSession` og `OplaMessageHandler` som singletons. Dependency injection leverer den fælles session og `ILogger<OplaMessageHandler>` til handlerens konstruktør. Null som session eller logger giver `ArgumentNullException`.

```csharp
public IReadOnlyList<string> HandleLine(string? line, Guid? playerId = null,
    CancellationToken cancellationToken = default)
```

Metoden er synkron og modtager én komplet tekstlinje uden afsluttende linjeskift. Den returnerer en samling med nul eller flere svarlinjer, også uden linjeskift. De nuværende kommandoer giver præcis én linje ved succes og en tom samling ved afvisning eller debug.

Handleren åbner ingen netværksforbindelse eller seriel-port og gemmer ingen spiller- eller forbindelsesspecifik tilstand. Sessionen kan indeholde ét selvstændigt spil eller 1–6 lobbydeltagere med egne pointtavler. Første deltager er fortsat aktiv; turskifte følger i #44.

`playerId` kommer fra serverens forbindelsesregister, aldrig fra tekstkommandoen. Handleren kalder `RollFromOpla` eller `ToggleHoldFromOpla`, som kræver første deltagers id i et lobbyspil. Manglende/tomt id, ukendte spillere og øvrige deltagere afvises med én Warning og intet svar. Annullering kontrolleres før handlingen, også efter ventetid på sessionslåsen, og videresendes uden Warning.

Kald uden id og kontrolleret serverbinding bevarer selvstændig enkeltspillerbrug. Den identitetsløse signatur kan ikke omgå kontrollen i et lobbyspil. LobbySession afviser sine tilmeldte spillere før start, så de heller ikke kan ændre et eksisterende enkeltspillerspil. HTTP bruger sine hidtidige sessionsmetoder og påvirker fortsat første deltager.

## Input og svar

Formatet følger den aftalte [kommunikationsprotokol](protocol.md). Kommandoer skal have store bogstaver og præcis ét mellemrum mellem felterne. Input bliver hverken trimmet eller ændret til store bogstaver.

| Input | Handling og svar |
|---|---|
| `ROLL` | Kalder `GameSession.RollFromOpla(playerId, cancellationToken)` og returnerer `STATE r hhhhh`. |
| `HOLD 1`–`HOLD 5` | Oversætter terningenummeret til indeks 0–4 og kalder `ToggleHoldFromOpla(index, playerId, cancellationToken)`. Returnerer `STATE r hhhhh`. |
| En linje med `#` først | Debug; giver ingen logning, svar eller spilhandling. |
| Øvrigt input, inklusive `null` og tom tekst | Logges som ugyldigt input og giver intet svar. |

`STATE 2 01010` betyder to brugte slag og terning 2 og 4 holdt. De fem tegn følger terning 1–5: `1` er holdt, og `0` er sluppet. Terningeværdierne findes i HTTP-API'ets JSON og sendes ikke i `STATE`.

Kast er tilladt i en åben tur indtil og med tredje slag. Hold/slip er kun tilladt i en åben tur efter første eller andet kast og ændrer hverken terningeværdier eller brugte slag. Et nyt tryk på samme terning slipper den igen.

## Sammenhængende eksempel

Opret først et spil med `POST /api/game/new` og JSON-bodyen `{"name":"Andreas"}`. Derefter kan handleren modtage følgende linjer:

| Input | Svar | Betydning |
|---|---|---|
| `ROLL` | `STATE 1 00000` | Første kast; ingen terninger holdt. |
| `HOLD 2` | `STATE 1 01000` | Hold terning 2. |
| `HOLD 4` | `STATE 1 01010` | Hold også terning 4. |
| `ROLL` | `STATE 2 01010` | Andet kast; terning 2 og 4 bevarer deres værdier. |
| `HOLD 2` | `STATE 2 00010` | Slip terning 2. |
| `ROLL` | `STATE 3 00010` | Tredje kast; terning 4 bevarer sin værdi. |
| `ROLL` | Intet svar | Afvises, fordi tre slag er brugt. |
| `HOLD 4` | Intet svar | Afvises, fordi hold/slip er lukket efter tredje slag. |

`GET /api/game` viser tilstanden efter hver handling. Afviste handlinger bevarer den eksisterende tilstand. En sluppet terning kan godt få samme værdi ved næste kast; tilfældige værdier behøver ikke ændre sig.

## Pointvalg gennem HTTP

`POST /api/game/score/{category}` og `/api/game/strike/{category}` bruger samme session som handleren. Efter første, andet eller tredje kast afslutter ét accepteret feltvalg turen og bevarer terninger, hold og brugte slag. Flere `ROLL`- og `HOLD n`-linjer afvises med én Warning pr. linje og en tom svarliste. Der sendes intet `STATE` ved afvisning.

Et nyt selvstændigt spil gennem `/api/game/new` nulstiller pointtavle og turstatus. Et startet lobbyspil kan ikke nulstilles her og giver 409. Handleren bruger den fælles session. Se [API-kontrakten](api.md) og [pointreglerne](scoring.md).

`STATE r hhhhh` er uændret. Pointvalg tilføjer ingen Oplà-kommandoer eller nye udsendelser. WAIT bruges ved lobbystart og READY for øvrige deltagere; turskifte og END følger i #44.

## Afvisninger og logging

- Manglende spil og afviste spilhandlinger giver én Warning med den validerede kommando og årsagen. Handleren opretter ikke automatisk et spil.
- Ukendte eller fejlformaterede linjer giver én Warning med årsagen `Ukendt kommando eller ugyldigt format` og det strukturerede felt `Length`. Feltet er `null`, hvis input er `null`. Den ukendte tekst udskrives ikke.
- Debuglinjer og accepterede handlinger giver ingen logposter fra handleren.
- Kun identitets- og regelafvisninger af typen `InvalidOperationException` omkring spilhandlingen håndteres. Andre fejltyper videresendes.

Eksempelvis afvises `roll`, `ROLL `, `HOLD 02`, `HOLD 6`, ekstra felter og indlejrede linjeskift. ` # debug` er ugyldigt input, fordi `#` ikke står først. Handleren splitter ikke flere kommandoer i samme input.

## Tilkobling til transport og øvrige opgaver

`/ws/opla` leverer én komplet linje pr. `HandleLine`-kald efter transportkontrol og servervalideret spillerbinding og sender hver svarlinje tilbage på samme forbindelse. Teksten ændres ikke. READY og debug håndteres før handleren. En tom svarliste giver ingen afsendelse, ekstra spilopslag eller ekstra Warning fra transporten. Browser-broens USB-læser skal fjerne linjeafslutningen og bevare linjens øvrige tegn; ved skrivning til USB tilføjer den et linjeskift igen.

LobbySession genkontrollerer binding og aktuel forbindelse under lobbylåsen og kalder handleren uden netværksafventning. Sessionslåsen beskytter identitet og handling samlet. Det kopierede STATE køes før lobbylåsen slippes, så JOINED, start og READY ikke overhales. Låserækkefølgen er lobby → korte registeropslag → spilsession; registerlåsen holdes ikke under handlerkaldet. Afsendelse afventes uden tilstandslås. Sendesvigt eller senere annullering hverken fortryder/gentager handlingen eller flytter svaret til overtageren. Se [WebSocket-kontrakten](opla-websocket.md).

| Opgave | Ansvar og status |
|---|---|
| #63 | Endpoint i alle miljøer, forbindelsessessioner, READY/adgang, ressourcegrænser og handler-/STATE-integration er implementeret og lokalt slutkontrolleret. PR #80 dokumenterer afleveringsstatus og CI; opgavens færdigkrav er merge og grøn CI på main. |
| #59 | Lobby, parring, genforbindelse, minimal start og identitetskontrol i Oplà-handlinger. |
| #61 | Browser-bro mellem USB/Web Serial og `/ws/opla`. |
| #35 og #42 | Frontendens rigtige API-kald og løbende visning af ændringer. |
| #44 | Næste tur, turskifte, END, vinder og spillets livscyklus. |
| #43 | Samlet fysisk test med Oplà, browser, server og frontend. |

`/ws/opla` er tilkoblet den fælles handler, men producerer ingen rigtig parring før #59. Den eksisterende `/ws/spike` er fortsat et særskilt ekko-endpoint med en fast testkode. Ingen af de lokale backendtests dokumenterer en fysisk forbindelse til spillogikken.

## Kørsel og tests

Trin 6 af #59 tilføjer 45 cases i [GameOplaIdentityTests](../backend/Yatzi.Tests/GameOplaIdentityTests.cs), [LobbyGameInputTests](../backend/Yatzi.Tests/LobbyGameInputTests.cs) og [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs). De dækker spilleradgang, beskedrækkefølge, samtidighed, overtagelse og fejl/annullering. Rigtige HTTP-/WebSocket-klienter følger READY → join → start → ROLL/HOLD → genforbindelse i alle fire miljøer. Se den aktuelle samlede kontrol i [lobbydokumentationen](lobby.md); nedenstående resultater fra #40/#63 bevares som historik.

Trin 7 supplerer med 10 cases i [LobbyBackendIntegrationTests](../backend/Yatzi.Tests/LobbyBackendIntegrationTests.cs) og en separat Production-prøve med rigtige .NET-klienter. Ved kontrollen af trin 7 bestod den samlede suite med 1.048 tests og 0 buildfejl/0 advarsler. Produktionskode og handlerkontrakt er uændrede i trin 7. Historiske resultater nedenfor er bevaret.

Kør fra repositoryets rod:

```bash
dotnet build H3Yatzi.sln --configuration Release
dotnet test H3Yatzi.sln --configuration Release
```

Kør kun beskedhåndteringens tests med:

```bash
dotnet test H3Yatzi.sln --configuration Release --filter FullyQualifiedName~OplaMessageHandler
```

[Handler-testene](../backend/Yatzi.Tests/OplaMessageHandlerTests.cs) kontrollerer kommandoer, svar, grænser, debug og logging direkte mod en session. [Integrationstestene](../backend/Yatzi.Tests/OplaMessageHandlerIntegrationTests.cs) henter handleren fra applikationens rigtige servicecontainer og kombinerer den med HTTP gennem en testserver i hukommelsen. Hver testcase har sin egen testvært og session.

Integrationstestene dækker en hel tur, handlinger gennem begge indgange, nyt spil med eksisterende handler samt samtidige kast og hold/slip. Step 7 af #40 tilføjer HTTP-feltvalg efter handlerkast, afvisning efter placering og strygning, genbrug af handleren efter nyt spil samt HTTP-feltvalg samtidig med handlerens `ROLL` og `HOLD`. Handlerens direkte tests kontrollerer én Warning pr. afvist linje efter begge slags feltvalg. Samtidige handlinger frigives fra en fælles startport; resultaterne må komme i vilkårlig rækkefølge.

[WebSocket-spiltestene](../backend/Yatzi.Tests/OplaWebSocketGameIntegrationTests.cs) tilføjer 32 testcases gennem det rigtige endpoint eller kontrollerede transportsockets med samme servicecontainer. De kontrollerer STATE, HTTP-samspil, nyt spil, regelafvisninger uden dobbeltlogning, modtagere, fragmentering, samtidige kast og sendesvigt. READY og logs signaleres gennem channels, og netværksarbejde synkroniseres med tasks uden faste ventepauser. Et ventende STATE spærrer ikke HTTP eller en anden forbindelse; et udført kast gentages ikke ved fejl eller annullering.

**Lokalt efter step 5 af #63:** Release-build bestod med 0 fejl og 0 advarsler, og hele testsuiten bestod med **791 tests**, ingen fejlede eller blev sprunget over. Dette historiske resultat fra den lokale branch lå før GitHub-afleveringen og er ikke en fysisk Oplà-test.

**Lokal slutkontrol i step 6:** Restore og Release-build bestod med 0 fejl og 0 advarsler, og **794 tests bestod** uden fejl eller oversprungne tests. Miljøtestene omfatter Development, Production, Staging og Test samt kontrolleret ROLL/STATE i Production. En separat Production-server bestod lokal transportprøve med rigtig WebSocket-klient; CI-resultater dokumenteres i PR'en. Den fysiske Oplà-integration er fortsat ikke verificeret.

Ved slutkontrollen af #40 bestod hele løsningen med 612 tests og et Release-build med 0 fejl og 0 advarsler. Automatiske tests verificerer backendens samspil uden en åben netværksport eller fysisk hardware. Den samlede fysiske forbindelse og integrationstesten i #43 er fortsat udestående. Afleveringsstatus og CI dokumenteres i [PR #76](https://github.com/Bmantaz/H3-Yatzi/pull/76).
