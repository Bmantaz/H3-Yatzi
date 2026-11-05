# Oplà-beskedhåndtering

`OplaMessageHandler` omsætter Oplàens tekstlinjer til kast og hold/slip i backendens fælles spil. Handleren bestemmer, hvilken handling der kaldes, mens spilreglerne og tilstanden ligger i `GameSession` og `Yatzi.Core.Game`.

Handleren er implementeret med automatiske tests uden fysisk hardware. HTTP-API og handler bruger samme spilsession. WebSocket-endpointet `/ws/opla`, parring og den samlede fysiske forbindelse følger i særskilte opgaver.

## Ansvar og dataflow

| Komponent | Ansvar |
|---|---|
| [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) | Genkender `ROLL`, `HOLD n` og debug, logger afvisninger og danner `STATE`-svar. |
| [GameSession](../backend/Yatzi.Api/GameSession.cs) | Ejer serverens ene spil i hukommelsen. Udfører handlinger og kopierer tilstanden under samme lås. |
| [Yatzi.Core.Game](../backend/Yatzi.Core/Game.cs) | Håndhæver terningereglerne: kast, hold/slip og højst tre slag. |
| Det kommende transportlag i #63 | Modtager komplette linjer gennem `/ws/opla` og sender svar til den samme forbindelse. |

```mermaid
flowchart LR
    Input["Tekstlinje"] --> Handler["OplaMessageHandler.HandleLine"]
    Handler --> Session["Fælles GameSession"]
    Http["HTTP /api/game"] --> Session
    Session --> Core["Yatzi.Core.Game"]
    Core --> Snapshot["Kopieret spiltilstand under låsen"]
    Snapshot --> State["STATE til handlerens kalder"]
    Snapshot --> Json["JSON til HTTP-klienten"]
```

Begge indgange bruger samme session, så et hold gennem HTTP påvirker næste kast gennem handleren og omvendt. Hvert svar bygges fra handlingens eget øjebliksbillede. Handleren foretager ikke et ekstra `GetState()`-kald, som kunne læse en anden klients efterfølgende handling.

Spillet oprettes gennem `POST /api/game/new`, jf. [HTTP-kontrakten](api.md). Et nyt spil erstatter sessionens aktuelle spil; den eksisterende handler følger automatisk med. Tilstanden går tabt ved servergenstart.

## Oprettelse og offentlig metode

[Program.cs](../backend/Yatzi.Api/Program.cs) registrerer både `GameSession` og `OplaMessageHandler` som singletons. Dependency injection leverer den fælles session og `ILogger<OplaMessageHandler>` til handlerens konstruktør. Null som session eller logger giver `ArgumentNullException`.

```csharp
public IReadOnlyList<string> HandleLine(string? line)
```

Metoden er synkron og modtager én komplet tekstlinje uden afsluttende linjeskift. Den returnerer en samling med nul eller flere svarlinjer, også uden linjeskift. De nuværende kommandoer giver præcis én linje ved succes og en tom samling ved afvisning eller debug.

Handleren åbner ingen netværksforbindelse eller seriel-port og gemmer ingen spiller- eller forbindelsesspecifik tilstand. Den nuværende session indeholder én spiller; lobby og multiplayer tilføjes senere.

## Input og svar

Formatet følger den aftalte [kommunikationsprotokol](protocol.md). Kommandoer skal have store bogstaver og præcis ét mellemrum mellem felterne. Input bliver hverken trimmet eller ændret til store bogstaver.

| Input | Handling og svar |
|---|---|
| `ROLL` | Kalder `GameSession.Roll()` og returnerer `STATE r hhhhh`. |
| `HOLD 1`–`HOLD 5` | Oversætter terningenummeret til indeks 0–4 og kalder `ToggleHold(index)`. Returnerer `STATE r hhhhh`. |
| En linje med `#` først | Debug; giver ingen logning, svar eller spilhandling. |
| Øvrigt input, inklusive `null` og tom tekst | Logges som ugyldigt input og giver intet svar. |

`STATE 2 01010` betyder to brugte slag og terning 2 og 4 holdt. De fem tegn følger terning 1–5: `1` er holdt, og `0` er sluppet. Terningeværdierne findes i HTTP-API'ets JSON og sendes ikke i `STATE`.

Kast er tilladt indtil og med tredje slag. Hold/slip er kun tilladt efter første eller andet kast og ændrer hverken terningeværdier eller brugte slag. Et nyt tryk på samme terning slipper den igen.

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

## Afvisninger og logging

- Manglende spil og afviste spilhandlinger giver én Warning med den validerede kommando og årsagen. Handleren opretter ikke automatisk et spil.
- Ukendte eller fejlformaterede linjer giver én Warning med årsagen `Ukendt kommando eller ugyldigt format` og det strukturerede felt `Length`. Feltet er `null`, hvis input er `null`. Den ukendte tekst udskrives ikke.
- Debuglinjer og accepterede handlinger giver ingen logposter fra handleren.
- Kun kernens regelafvisninger af typen `InvalidOperationException` omkring spilhandlingen håndteres. Andre fejltyper videresendes.

Eksempelvis afvises `roll`, `ROLL `, `HOLD 02`, `HOLD 6`, ekstra felter og indlejrede linjeskift. ` # debug` er ugyldigt input, fordi `#` ikke står først. Handleren splitter ikke flere kommandoer i samme input.

## Tilkobling til transport og øvrige opgaver

Det kommende `/ws/opla` skal levere én komplet linje pr. `HandleLine`-kald og sende hver returneret svarlinje tilbage på den samme forbindelse. Browser-broens USB-læser skal fjerne linjeafslutningen og bevare linjens øvrige tegn; ved skrivning til USB tilføjer den et linjeskift igen.

| Opgave | Udestående integration |
|---|---|
| #63 | WebSocket-endpoint, forbindelsessessioner, kontrol før READY/parring og grænser for længde, beskedrate og antal forbindelser. |
| #59 | Lobby, parringskoder, `READY`, `PAIR` og `JOINED`. |
| #61 | Browser-bro mellem USB/Web Serial og `/ws/opla`. |
| #35 og #42 | Frontendens rigtige API-kald og løbende visning af ændringer. |
| #44 | Turskifte, flere spillere, `WAIT`, `END` og spillets afslutning. |
| #43 | Samlet fysisk test med Oplà, browser, server og frontend. |

Tilføjelsen af handleren opretter ikke `/ws/opla`. Den eksisterende `/ws/spike` er fortsat et særskilt ekko-endpoint. Ingen af dem skal fremstilles som en verificeret fysisk forbindelse til spillogikken i denne opgave.

## Kørsel og tests

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

Integrationstestene dækker en hel tur, handlinger gennem begge indgange, nyt spil med eksisterende handler samt samtidige kast og hold/slip. Samtidige handlinger frigives fra en fælles startport; resultaterne må komme i vilkårlig rækkefølge.

Automatiske tests verificerer backendens samspil uden en åben netværksport eller fysisk hardware. Den samlede fysiske forbindelse og integrationstesten i #43 er fortsat udestående. Aflevering af #36 kræver derudover en PR med `Closes #36`, merge til `main` og grøn CI.
