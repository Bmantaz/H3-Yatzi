# Lobby og parring til opgave #59

Denne specifikation beskriver den aftalte løsning til [#59 – Lobby og parring i backend](https://github.com/Bmantaz/H3-Yatzi/issues/59). Den fastlægger API-kontrakten til Frederiks lobby i #60, forbindelsen til WebSocket-transporten fra #63 og den interne rækkefølge for implementeringen.

**Læsestatus efter #44 trin 8, 10. november 2026:** Afsnittene frem til [#44-kontrakten](#kommende-kontrakt-for-44) bevarer #59 og dens historiske kontroller. På `a92c8a9` fungerede lobby/parring og første start, mens turskifte og omkamp manglede. Frederik har ifølge brugeren kontrolleret USB → browserbro → backend → skærm; kontrollen gentages ikke, og #43 venter.

**Aktuel lokal adfærd efter #44 trin 8:** De 11 spilfelter fra trin 4 bevares. HTTP-handlinger kræver nu spil-/tur-/spillerkontekst og koordineres med Oplà-input, start og READY gennem samme lobbylås og ordnede kø. HTTP-kast/hold giver STATE til aktiv Oplà, feltvalg STATE/WAIT og afslutning END til aktuelle deltagere. READY giver PAIR, JOINED og STATE/WAIT/END. Join returnerer nu joinedPlayerId for netop requestens accepterede spiller, mens GET stadig kun har players og started. Frakobling bevarer turen og forhindrer ikke korrekt browserreserve. `/api/lobby/restart` er nu implementeret: enhver deltager kan efter afslutning starte omkamp, når alle er forbundne, med samme id’er, navne, rækkefølge og parringer. Frontenden bruger nu joinedPlayerId direkte og viser Omkamp for tilmeldte deltagere på slutskærmen. Knappen er deaktiveret ved frakoblede deltagere, mens browserreserven under spillet stadig kan bruges. Polling opdaterer også forbindelser på spil- og slutskærmen. Se [implementering og samlet lokal slutkontrol](game-lifecycle.md#slutkontrol-af-44-i-trin-8).

**Historisk leverance af #59: trin 1–8 er gennemført.** Leverancen og dens merge-/CI-status dokumenteres i [PR #81](https://github.com/Bmantaz/H3-Yatzi/pull/81). READY/PAIR, GET/join, genforbindelse og spillestart for 1–6 deltagere fungerer. Første spillers aktuelle Oplà kan kaste og holde efter start; øvrige spillere afvises. STATE/WAIT genskabes ved READY, og `/api/game/new` kan ikke overskrive lobbyspillet. Færdigkriteriet for #59 er merge og grøn CI på main. Turskifte og næste tur følger i #44.

**Kontrol før PR:** Nyeste main med WiFi/USB fra PR #86 er integreret uden ændring af HTTP-kontrakterne. Se [nye kontrolresultater](game-lifecycle.md#kontrol-før-pr-efter-integration-af-main); tidligere resultater nedenfor bevares som historik.

## Grundlag og afgrænsning

- **Grundlag før implementering, kontrolleret 8. november 2026:** #59 var åben og tildelt Andreas. Afhængighederne #36 og #63 var lukkede, og der var ingen tekniske blokeringer for at begynde.
- Lokal `main`, `origin/main` og GitHubs `main` pegede ved branchoprettelsen på `10c692fa42943ddce2a5840568713a6f61b2d2c7`. Arbejdstræet var rent.
- Arbejdsbranchen er `S2-9-lobby-parring`, oprettet fra denne `main`. Alle trin er leveret gennem PR #81 med `Closes #59`.
- #59 omfatter parring, tilmelding, lobbylæsning og minimal spillestart for 1–6 spillere. Efter aftale oprettes alle deltagere og deres pointtavler her, og kun første spillers Oplà får adgang til spilhandlinger.
- Ved genforbindelse overtager den nye forbindelse fra den gamle. Dette er også det valgte grundlag for et senere skift mellem USB og WiFi.
- #44 tilføjer næste tur, turskifte, spillets afslutning, vinder og ny lobby eller nyt spil efter afslutning. #60 tilføjer frontendens lobby. #43 står for den samlede fysiske integrationstest. Direkte WiFi følger i #53/#54.
- Database og login indføres ikke i #59. De fire cifre er en parringskode, ikke dokumentation for en brugers identitet.

Opgavens færdigkrav er fortsat merge gennem en PR og grøn CI på `main`. En specifikation eller et gennemført internt trin afslutter ikke issuet.

## Implementeret i trin 2

Denne del beskriver resultatet ved afslutningen af trin 2. Trin 3 udvider kodeposterne med spillere og bevarer tilmeldte spilleres koder efter afbrydelse som beskrevet nedenfor.

[LobbySession](../backend/Yatzi.Api/LobbySession.cs) implementerer IOplaReadyHandler. Program registrerer samme singleton under begge typer. Den ejer én kodepost pr. forbindelses-id og bruger en kort lås til oprydning, opslag og udstedelse. Spiller-id, navn og deltagerrækkefølge tilføjes først ved tilmelding i trin 3.

- En ny accepteret READY-forbindelse får `PAIR cccc`. Gentaget READY på samme forbindelse giver samme kode, også hvis klienten medsender en anden kode.
- En ny forbindelse med `READY cccc` får endnu sin egen kode og overtager ingen anden forbindelse. Genkendelse og overtagelse implementeres i trin 4.
- Starttallet kommer fra `RandomNumberGenerator.GetInt32(10000)`. En kollision undersøges med en begrænset lineær søgning og overløb fra 9999 til 0000. Formatering med invariant kultur bevarer fire ASCII-cifre. Testkonstruktørens `Func<int>` giver reproducerbare kollisioner.
- Før hvert READY-opslag fjernes poster for manglende eller lukkende forbindelser. Lukkede forbindelser er straks ugyldige i registeret; kodeposten slettes ved næste READY-kald. Der bruges ingen timer eller baggrundsopgave.
- Ukendte, reserverede, lukkede eller endnu ikke READY forbindelser får ingen kode eller svar. READY giver ikke adgang til ROLL/HOLD og opretter intet spil.
- PAIR sendes gennem registerets afsendelsesvej uden lobbylåsen. Sendesvigt udsteder ikke en ny kode eller genkører afsendelsen; undtagelser går til transportens fejlgrænse. Annullering før registrering efterlader ingen ny kodepost.

**Automatisk verificeret:** Release-build med 0 fejl og 0 advarsler samt **814 beståede tests**, ingen fejlede eller blev sprunget over. De 23 nye [LobbySessionTests](../backend/Yatzi.Tests/LobbySessionTests.cs) erstatter den tomme standards tre tests. Den fokuserede kørsel af lobby-, endpoint- og levetidstests bestod med 136 cases. App-testene verificerer PAIR, gentagelser, samme singleton og fortsat afvist ROLL/HOLD i Development, Production, Staging og Test. READY opretter heller ikke et spil gennem den rigtige app-pipeline.

## Implementeret i trin 3

Denne del beskriver resultatet ved afslutningen af trin 3. Trin 4 tilføjer genkendelse på en ny forbindelse og overtagelse som beskrevet nedenfor.

[LobbyEndpoints](../backend/Yatzi.Api/LobbyEndpoints.cs) registrerer GET og join i den rigtige app-pipeline. [LobbyContracts](../backend/Yatzi.Api/LobbyContracts.cs) beskriver kopierede svar og sessionens regelresultater. LobbySession ejer fortsat tilstanden, mens registeret ejer forbindelserne.

- GET giver en tom liste ved serverstart. Join validerer JSON, navn og kode, opretter et stabilt GUID, binder forbindelsen og tilføjer spilleren i accepteret rækkefølge. Ens navne tillades. Koder og forbindelses-id'er vises ikke i HTTP-svar.
- Navnets kontroltegn afvises før trimning. Længden vurderes derefter i UTF-16-kodeenheder. Koden trimmes ikke; kun fire ASCII-cifre accepteres. Ukendte JSON-felter ignoreres. Ugyldigt input giver 400 før kodeopslag; en ukendt/bortfaldet kode giver 404, og dobbelt tilmelding, fuld lobby eller mislykket binding giver 409.
- Kodekontrol, kapacitetskontrol, serverbinding og registrering udføres under én kort lobbylås uden netværksafventning. Seks-spillergrænsen inkluderer frakoblede spillere. Mislykket binding opretter ingen deltager; lukning efter accepteret binding fjerner ikke spilleren.
- Lukkede forbindelsers ubrugte koder fjernes før READY og join. Tilmeldte spilleres koder og navne bevares og optager fortsat deres pladser. GET beregner `connected` fra den aktuelle åbne spillerbinding. Svarenes arrays og records deler ikke skrivbar tilstand med lobbyen.
- HTTP afventer forsøget på `JOINED navn` og returnerer derefter 200 med et friskt lobbyøjebliksbillede. Netværksfejl logges og lukker forbindelsen gennem transportens eksisterende lukkevej; tilmeldingen bevares, og et aktivt HTTP-request får stadig 200. Annullering efter accept fortryder ingen tilmelding.
- Gentaget READY på en tilmeldt forbindelse sender samme PAIR efterfulgt af JOINED. Batches reserveres under låsen og sendes uden låsen i rækkefølge pr. forbindelse. Fejl og annullering forgifter ikke køen; en annulleret ventende operation lader heller ikke senere batches overhale et tidligere batch. Ingen automatisk genafsendelse udføres.
- En ny forbindelse genkender endnu ingen kode. Den får sin egen PAIR, også hvis den medsender en tilmeldt spillers kode. Genkendelse og overtagelse følger i trin 4.
- `started` er altid false. Join opretter intet spil. Endpointet afviser lobbyspillernes ROLL/HOLD, så bindingen ikke giver adgang til et eksisterende enkeltspillerspil. Den fulde identitetskontrol i GameSession følger i trin 6.

**Automatisk verificeret:** Release-build med **0 fejl og 0 advarsler** samt hele testsuiten med **889 beståede tests**, ingen fejlede eller blev sprunget over. De 75 nye cases i [LobbyJoinSessionTests](../backend/Yatzi.Tests/LobbyJoinSessionTests.cs) og [LobbyApiTests](../backend/Yatzi.Tests/LobbyApiTests.cs) dækker blandt andet validering, samtidige joins, sidste plads, frakobling, kopierede svar, beskedrækkefølge, sendesvigt og annullering. READY → PAIR → join → JOINED og gentaget READY er verificeret gennem app-pipelinen i Development, Production, Staging og Test. Spil-, transport- og levetidsregressionerne består fortsat.

## Implementeret i trin 4

Denne del beskriver resultatet ved afslutningen af trin 4. Trin 5 udvider genforbindelsens svar med aktuel STATE/WAIT efter spillestart som beskrevet nedenfor.

- Kodeposten bevares med samme kode, spiller-id, navn og deltagerrækkefølge, mens dens aktuelle ConnectionId kan flyttes. En tilmeldt spiller genforbinder uden endnu et join; en stadig gyldig, ubrugt kode kan overtages før join. Ubrugte koder fra lukkede eller fjernede forbindelser bortfalder fortsat.
- Registerets serverinterne `TryTakeOver` validerer den nye READY-modtager, binder en eventuel spiller og tilbagekalder den gamle adgang under samme registerlås. Guid.Empty betyder en ubrugt kode og giver ingen spillerbinding. Afvist modtager ændrer ikke den gamle tilknytning. Låserækkefølgen er lobby → register → forbindelsestilstand.
- Den gamle forbindelse markeres straks lukket for READY, binding, spilleradgang og nye afsendelser. Endpointet ejer fortsat disposal, fjernelse fra registeret og Completion. Den gamle plads tæller med i grænsen på 12, indtil oprydningen er færdig.
- Hver overtaget forbindelse får en selvstændig afsendelseskæde. Gamle batches beholder deres konkrete gamle modtager og kan ikke blive sendt til den nye socket. PAIR og eventuelt JOINED sendes uden at vente på den gamle sockets afsendelse eller lukkehåndtryk.
- Gammel lukning og ny afsendelse startes begge før afventning. READY-behandleren afventer begge opgaver uden tilstandslås. Lukningen beholder den fælles femsekundersfrist og Abort ved timeout og fortsætter ved senere request-annullering. Der tilføjes ingen uovervåget baggrundsopgave.
- Transporten håndterer nu også forventet afbrudt modtagelse under serverinitieret lukning og en allerede terminal socket under lukkehåndtrykket. Det forhindrer, at en forventet fejl fra gammel lukning videresendes til den nye forbindelses READY-behandling.
- Sen gammel oprydning eller sendefejl, også fra et allerede accepteret HTTP-join, lukker kun den konkrete gamle forbindelse. Ved konkurrerende overtagelser vinder den sidst accepterede under lobbylåsen. Annullering eller sendesvigt efter accept genskaber ikke en tidligere binding og udløser ingen automatisk genafsendelse.
- En forbindelse med sin egen kodepost beholder altid tilknytningen, også ved en anden medsendt kode. Genforbindende klienter skal sende deres bevarede kode i første READY. En ukendt/bortfaldet kode giver en ledig PAIR uden automatisk tilmelding.
- HTTP-kontrakten er bevaret. `connected` følger den aktuelle binding; lobbyspillernes ROLL/HOLD er fortsat afvist. Ingen spiloprettelse, STATE/WAIT eller genafspilning af spilhandlinger er tilføjet.

**Automatisk verificeret:** Release-build med **0 fejl og 0 advarsler** og hele testsuiten med **946 beståede tests**, ingen fejlede eller blev sprunget over. Trin 4 tilføjer 57 cases i [LobbyReconnectSessionTests](../backend/Yatzi.Tests/LobbyReconnectSessionTests.cs), [OplaConnectionTakeoverTests](../backend/Yatzi.Tests/OplaConnectionTakeoverTests.cs), de eksisterende lobby-API-tests og forbindelsestests. De dækker blandt andet samtidig overtagelse/join, gammel beskedkø, annullering, netværksfejl, lukke-timeout og sen oprydning. App-pipelinen verificerer både genforbindelse efter lukning og overtagelse fra en åben forbindelse i alle fire miljøer. De fem app-cases med åben gammel forbindelse er desuden gentaget tre gange med bestået resultat efter rettelsen af lukkegrænsen.

## Implementeret i trin 5

Denne del beskriver resultatet ved afslutningen af trin 5. Den midlertidige blokering af lobbyens spilinput erstattes i trin 6 nedenfor.

- `POST /api/lobby/start` bruger ingen request-body og returnerer spil-JSON med HTTP 200. Ingen deltagere, frakoblet deltager eller gentaget start giver danske JSON-fejl med 409. Afvist start bevarer tidligere spil og lobby.
- LobbySession kræver samme GameSession som spil-API'et og handleren. Under lobbylåsen kontrolleres startstatus og spillerbindinger; under spilsessionslåsen oprettes alle deltagere samlet. Lobbyen markeres startet og reserverer alle beskedbatches, før låsen slippes. Annullering kontrolleres også efter ventetid på spilsessionslåsen og før ændringen.
- GameSession opbevarer GUID/navn og en selvstændig Game-instans pr. deltager. Kernen er uændret. Hver instans ejer sin egen pointtavle; første instans leverer terninger, hold, slag, mulige point og turstatus. Start kopierer deltagerrækkefølgen og opretter alle tavler med 15 ledige felter og nul summer. Spiller-id'er tilføjes ikke til spil-JSON.
- Start sender `STATE 0 00000` til første deltager og `WAIT navn` til resten. Alle afsendelser startes parallelt uden tilstandslåsen og bevarer rækkefølgen efter eventuelle ventende JOINED-batches. HTTP afventer afsendelsesforsøgene og returnerer øjebliksbilledet fra accepteret start, også hvis senere HTTP-handlinger allerede har ændret spillet.
- Sendesvigt lukker kun den konkret fangede modtager og stopper ikke andre deltageres beskeder. Starten bevares; et aktivt HTTP-request får stadig 200. Senere annullering fortryder heller ikke start. Forsinket gammel afsendelse efter overtagelse kan ikke lukke den nye forbindelse. Ingen automatisk genafsendelse udføres.
- `GET /api/lobby` viser started=true, og join afvises efter eksisterende inputvalidering. Nye READY-forbindelser kan stadig få koder, og bevarede spillere kan genforbinde. Efter start genskaber READY PAIR, JOINED og aktuel STATE til første spiller eller WAIT til resten. En afsluttet tur genåbnes ikke.
- En lobbystart erstatter et eventuelt selvstændigt enkeltspillerspil. Beskyttelsen af `/api/game/new` er flyttet frem fra trin 6: GameSession afviser nulstilling af et lobbyspil under sin lås, og endpointet giver 409. Ugyldigt request giver fortsat 400 før denne regelkontrol.
- HTTP-kast, hold og feltvalg arbejder på første deltager. Feltvalg låser fortsat turen uden turskifte. Lobbyspillernes Oplà-input forbliver afvist, indtil trin 6 fører PlayerId gennem handler og GameSession. Der tilføjes hverken næste tur, vinder eller END.

**Automatisk verificeret:** Release-build med **0 fejl og 0 advarsler** og hele testsuiten med **993 beståede tests**, ingen fejlede eller blev sprunget over. De 47 nye cases findes i [GameLobbySessionTests](../backend/Yatzi.Tests/GameLobbySessionTests.cs), [LobbyStartSessionTests](../backend/Yatzi.Tests/LobbyStartSessionTests.cs) og [LobbyStartApiTests](../backend/Yatzi.Tests/LobbyStartApiTests.cs). Start med én og seks deltagere og gentaget READY er afprøvet gennem app-pipelinen i alle fire miljøer. Tests dækker blandt andet egne tavler, kopierede svar, samtidige start-/join-/genforbindelseskald, beskedrækkefølge, netværksfejl, annullering, nulstillingsværn og bevaret turafslutning. Tidligere regressionstests består fortsat.

## Implementeret i trin 6

- WebSocket-endpointet leverer serverens PlayerId sammen med forbindelses-id og linje til LobbySession. Lobbyen genkontrollerer den aktuelle binding under sin lås, afviser tilmeldte spillere før start og ordner accepten med start, READY og overtagelse.
- Handleren fører identiteten til GameSessions særskilte Oplà-indgange. Under samme sessionslås kontrolleres annullering, aktiv spiller og spilregler, handlingen udføres, og svaret kopieres. Kun første deltager må handle i lobbyspillet; manglende, tomme, ukendte og øvrige spilleridentiteter afvises uden ændring.
- Accepterede spilsvar reserveres i lobbyens eksisterende kø før låsen slippes. JOINED, startens STATE og READY-batches kan derfor ikke overhales af nyere spilsvar. Netværket afventes uden tilstandslåse; langsomme forbindelser spærrer ikke andre modtagere eller lobbylæsning.
- Gamle batches beholder deres konkrete forbindelses-id. Vinder en overtagelse før inputaccept, afvises gammelt input ved genkontrollen. Senere lukning, sendesvigt eller annullering hverken genafspiller handlingen eller flytter svaret til overtageren. Endpointet ejer fortsat fejlgrænse og oprydning.
- Identitetsløse handlerkald og kontrolleret serverbinding bevarer selvstændig enkeltspillerbrug, men identitetsløse kald kan ikke påvirke lobbyspillet. HTTP-handlinger påvirker fortsat første deltager uden nye felter eller automatiske WebSocket-udsendelser. Feltvalg afslutter turen uden næste spiller.

De 45 nye testcases findes i [GameOplaIdentityTests](../backend/Yatzi.Tests/GameOplaIdentityTests.cs), [LobbyGameInputTests](../backend/Yatzi.Tests/LobbyGameInputTests.cs) og [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs). Release-build med 0 fejl/0 advarsler og alle **1.038 tests** består; ingen fejlede eller blev sprunget over. Det fulde forløb med én og seks deltagere er verificeret gennem HTTP/WebSocket i Development, Production, Staging og Test. Den efterfølgende samlede kontrol står i trin 7 nedenfor.

## Samlet backendkontrol i trin 7

Trin 7 tilføjer 10 cases i [LobbyBackendIntegrationTests](../backend/Yatzi.Tests/LobbyBackendIntegrationTests.cs). Alle spillere bindes gennem rigtig HTTP-join, og spillet starter gennem lobby-API'et. Testværten styrer kun tid og kodekandidat. Produktionskode, kontrakter, afhængigheder og konfiguration er uændrede i dette trin.

- Fem transportcases afprøver rateoverskridelse, 65 kodepunkter, 257 payload-bytes, binære beskeder og ugyldig UTF-8 under et startet tospillerspil. Kun afsenderen lukkes med den eksisterende lukkekode; hele spiltilstanden bevares. Genforbindelse genskaber samme GUID, navn, rækkefølge og STATE, og næste gyldige hold-handling accepteres.
- Kapacitetscasen fylder registerets 12 pladser med to lobbyspillere og ti ubrugte forbindelser. Forbindelse 13 afvises med 503 uden ændring af bindinger. Efter frigivelse og Completion for én ubrugt forbindelse kan en ny socket overtage første spiller og fortsætte spillet.
- Fire cases starter en ny testvært efter lukning af den gamle i Development, Production, Staging og Test. Lobby og spil er væk, og en tidligere kode giver kun PAIR uden automatisk spilleradgang. Samme cifre genbruges bevidst af testgeneratoren; nyt HTTP-join giver et nyt GUID og et friskt spil.

Transporttests med RecordingReadyHandler bevares som isolerede tests. Deres tomme READY-behandling er ikke produktionslobbyens adfærd; det er nu tydeliggjort i testklassens kommentar og navnet på testen for eksplicit testbinding efter genforbindelse. Ingen assertion eller transportgrænse er fjernet.

### Krav/test-matrix

| Krav | Automatiske tests og kontrol |
|---|---|
| Koder, ASCII/nuller, kollisioner og gentaget READY | [LobbySessionTests](../backend/Yatzi.Tests/LobbySessionTests.cs) og [LobbyApiTests](../backend/Yatzi.Tests/LobbyApiTests.cs). |
| JSON/navne/koder, danske fejl, dobbelt join og deltagerrækkefølge | [LobbyApiTests](../backend/Yatzi.Tests/LobbyApiTests.cs) og [LobbyJoinSessionTests](../backend/Yatzi.Tests/LobbyJoinSessionTests.cs). |
| Seks deltagere, sidste plads og frakoblede deltagere | [LobbyJoinSessionTests](../backend/Yatzi.Tests/LobbyJoinSessionTests.cs), herunder samtidige joins. |
| Start med nul/én/seks, frakobling, gentagelse og join efter start | [LobbyStartSessionTests](../backend/Yatzi.Tests/LobbyStartSessionTests.cs) og [LobbyStartApiTests](../backend/Yatzi.Tests/LobbyStartApiTests.cs). |
| Uafhængige pointtavler, kopierede svar og uændret spil-JSON | [GameLobbySessionTests](../backend/Yatzi.Tests/GameLobbySessionTests.cs) og [LobbyStartApiTests](../backend/Yatzi.Tests/LobbyStartApiTests.cs). |
| Serverens identitet, kun første spiller og ingen adgang før start | [GameOplaIdentityTests](../backend/Yatzi.Tests/GameOplaIdentityTests.cs) og [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs). |
| Højst tre slag, hold/slip, afsluttet tur og nulstillingsværn | [GameOplaIdentityTests](../backend/Yatzi.Tests/GameOplaIdentityTests.cs), [GameLobbySessionTests](../backend/Yatzi.Tests/GameLobbySessionTests.cs) og [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs). |
| Genforbindelse, åben gammel socket og sen oprydning | [LobbyReconnectSessionTests](../backend/Yatzi.Tests/LobbyReconnectSessionTests.cs), [OplaConnectionTakeoverTests](../backend/Yatzi.Tests/OplaConnectionTakeoverTests.cs) og [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs). |
| JOINED/start/READY/spilsvar i rækkefølge og samtidige handlinger | [LobbyJoinSessionTests](../backend/Yatzi.Tests/LobbyJoinSessionTests.cs), [LobbyStartSessionTests](../backend/Yatzi.Tests/LobbyStartSessionTests.cs) og [LobbyGameInputTests](../backend/Yatzi.Tests/LobbyGameInputTests.cs). |
| Sendesvigt, annullering, Completion, disposal og lukke-timeout | [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs), [LobbyReconnectSessionTests](../backend/Yatzi.Tests/LobbyReconnectSessionTests.cs) og [OplaWebSocketLifetimeTests](../backend/Yatzi.Tests/OplaWebSocketLifetimeTests.cs). |
| Fragmentering, UTF-8, størrelse, ratevindue og forbindelsesgrænser | [OplaWebSocketEndpointTests](../backend/Yatzi.Tests/OplaWebSocketEndpointTests.cs) og [OplaWebSocketGameIntegrationTests](../backend/Yatzi.Tests/OplaWebSocketGameIntegrationTests.cs) bevarer transportregressionerne; [LobbyBackendIntegrationTests](../backend/Yatzi.Tests/LobbyBackendIntegrationTests.cs) tilføjer samspillet med rigtig lobby. |
| Ny serverinstans og bortfaldet identitet, også ved samme kodecifre | [LobbyBackendIntegrationTests](../backend/Yatzi.Tests/LobbyBackendIntegrationTests.cs) i alle fire miljøer. |
| Hele HTTP-/WebSocket-forløbet med én/seks spillere i alle miljøer | [LobbyGameInputApiTests](../backend/Yatzi.Tests/LobbyGameInputApiTests.cs), suppleret med den separate Production-prøve nedenfor. |

### Separat Production-prøve

En separat Kestrel-proces med standardregistreringer og den rigtige kodegenerator bestod prøven med .NETs HttpClient og ClientWebSocket på `127.0.0.1` og en automatisk tildelt port. Serverloggen bekræfter Production; prøveklienten kørte på .NET 8.0.19. To forskellige firecifrede koder blev tilmeldt gennem HTTP, hvorefter start gav STATE til Andreas og WAIT til Bjarke.

Første spiller kastede og holdt. Anden spillers ROLL/HOLD gav ingen svar før den efterfølgende READY-markør og ændrede ikke spillet. En ny forbindelse overtog første spillers kode, GUID, navn, rækkefølge og STATE uden ekstra kast; den gamle fik et normalt lukkehåndtryk. Den nye forbindelse kunne derefter holde endnu en terning. HTTP-strygning afsluttede turen uden næste spiller, senere Oplà-input blev afvist, og `/api/game/new` gav 409 uden ændring.

Klienterne blev lukket, kun prøveprocessen blev stoppet, og en ny TCP-listener kunne binde dens frigivne port. Denne prøve er adskilt fra TestServer-tests og er ikke fysisk Oplà-, USB/WiFi- eller tunnelintegration.

Lokale, ignorerede artefakter: `TestResults/step7-results.trx`, `TestResults/step7/production-probe.json` og `TestResults/step7/production-server.log`. Den midlertidige prøves kode ligger også under TestResults og indgår ikke i leverancen.

## Slutreview og aflevering i trin 8

Hele leverancen, inklusive de nye filer, er gennemgået mod kontrakten og krav/test-matrixen. Reviewet fulgte kodeudstedelse, serverbinding, tilmeldingsrækkefølge, start, spillerkontrol, låserækkefølge, ordnede batches, annullering og oprydning efter overtagelse. Transportgrænser, kopierede svar, selvstændig enkeltspillerbrug og nulstillingsværn er bevaret.

Reviewet fandt én konkret inputfejl: JSON med et uparret UTF-16-surrogat i navn eller kode kunne parses, men GetString kastede senere en InvalidOperationException. [LobbyEndpoints](../backend/Yatzi.Api/LobbyEndpoints.cs) håndterer nu kun denne tekstaflæsning ved inputgrænsen og returnerer den eksisterende danske JSON-fejl med 400. Fejl fra lobbyens tilstandsændringer bliver fortsat ikke omklassificeret til klientfejl. Fire nye cases i [LobbyApiTests](../backend/Yatzi.Tests/LobbyApiTests.cs) fejlede før rettelsen og består efter den; hver case kontrollerer også, at koden stadig kan tilmeldes bagefter.

**Lokal slutkontrol:** Restore og Release-build består med **0 fejl og 0 advarsler**, og alle **1.052 tests** består uden fejlede eller oversprungne tests. TRX gemmes som `TestResults/step8-results.trx`. Production-prøven fra trin 7 er genkørt mod den rettede API og bestod igen med standardregistreringer, rigtig kodegenerator og .NET-klienter. Prøveprocessen er stoppet, og porten er frigivet. Resultat og log er kopieret til `TestResults/step8/`; artefakter og midlertidig prøvekode indgår ikke i Git-leverancen.

Featurecommitten er `b348c16e4c206bdd6cfafa4f15bfa2a20e1da98f`. Første CI-kørsel bestod backend, men Arduino-opsætningen fejlede, fordi ubuntu-latest var flyttet til Ubuntu 26.04 uden den krævede Python 3.12.4. CI-commit `cff98c0` fastlåser kun Arduino-jobbet til Ubuntu 24.04; pakken er verificeret i actions/python-versions' officielle manifest. Controllerkode, biblioteker og actionversioner er uændrede.

En efterfølgende CI-kørsel afslørede en timingafhængig assertion i starttesten: JOINED kunne stadig være sidste besked, når WAIT blev kontrolleret. Testcommit `4fe624c` tilføjer et eksplicit afsendelsessignal uden at fjerne assertions eller ændre produktionsadfærd. De 31 berørte start-/spilinputtests bestod tre lokale gentagelser, og hele den lokale suite bestod igen med 1.052 tests.

**Verificeret første grønne PR-hoved:** [CI-kørsel 42498725499](https://github.com/Bmantaz/H3-Yatzi/actions/runs/42498725499) på `4fe624c9f5139be5a9e5cb6d798db03e51254eb9` bestod backend med 0 buildfejl/0 advarsler og 1.052 beståede tests samt Arduino-kompilering til MKR WiFi 1010. GitGuardian bestod også. Trin 8 er markeret efter denne kontrol. Senere PR-CI og CI på merge-committen dokumenteres med præcis SHA og links i PR-beskrivelsen. Den første grønne PR-kørsel ovenfor bevares som historik; opgavens færdigkriterium kræver også merge og grøn CI på main.

## Ansvar og dataflow

| Komponent | Ansvar efter implementering |
|---|---|
| `LobbySession` | Én delt singleton med koder, stabile `Guid`-spilleridentiteter, deltagerrækkefølge, startstatus og koordinering af spilinput/beskedrækkefølge. Samme instans registreres som `IOplaReadyHandler`. |
| [OplaConnectionRegistry](../backend/Yatzi.Api/OplaConnectionRegistry.cs) | Finder accepterede forbindelser, registrerer READY, binder/overfører spilleridentiteter atomisk og sender til den præcise forbindelse. Transportens forbindelser og grænser bevares. |
| [OplaWebSocketEndpoints](../backend/Yatzi.Api/OplaWebSocketEndpoints.cs) | Validerer transporten, kalder READY-behandleren og leverer serverens `PlayerId` gennem lobbyens koordinering til spilhandlingen. Ejer transportfejl og oprydning. |
| [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) | Genkender ROLL/HOLD og danner STATE gennem spilsessionen. Identitetsbærende kald bruges fra WebSocket. |
| [GameSession](../backend/Yatzi.Api/GameSession.cs) | Ejer deltagernes Game-instanser, kontrollerer Oplà-identitet og handling under samme lås, kopierer spiltilstanden og beskytter mod nulstilling. Første deltager er aktiv. |
| Lobbyens HTTP-endpoints | GET, join og start oversætter resultatet til lobby-/spiltilstand eller danske fejl. Kun join læser JSON-input. |

```text
Oplà → USB → browser-bro → WSS /ws/opla → READY → LobbySession
Browser → HTTP /api/lobby/join → LobbySession → spillerbinding → JOINED → Oplà
Browser → HTTP /api/lobby/start → GameSession → spil-JSON til browseren
                                             → STATE/WAIT til Oplàerne
Oplà → ROLL/HOLD → transportens PlayerId → LobbySession → handler → GameSession
                                        → ordnet STATE-batch → samme Oplà
```

Serveren læser ikke en USB-port. Browser-broen og senere direkte WiFi bruger samme tekstprotokol. Browserens terningeværdier og point kommer fra spil-API'et; STATE indeholder kun brugte slag og hold-markeringer.

## HTTP-kontrakt til frontend

Brug relative URL'er fra samme adresse som spillet. Requests med JSON bruger `Content-Type: application/json`. Alle tre endpoints nedenfor er implementeret og returnerer HTTP 200 og JSON ved succes. Start bruger ikke request-body, ligesom spil-API'ets handlingsruter.

| Endpoint | Request-body | Response ved succes |
|---|---|---|
| `GET /api/lobby` | Ingen | Lobbytilstanden, også når ingen har tilmeldt sig. |
| `POST /api/lobby/join` | `{"name":"Andreas","code":"0012"}` | Den opdaterede lobbytilstand efter accepteret tilmelding. |
| `POST /api/lobby/start` | Ingen | Spiltilstanden efter det eksisterende [spil-JSON-format](api.md#spiltilstand-i-response). |

### Lobbytilstand

En tom lobby returneres sådan:

```json
{
  "players": [],
  "started": false
}
```

Efter en tilmelding kan svaret se sådan ud:

```json
{
  "players": [
    {
      "id": "72da3348-4488-4c81-9256-1356f4ca5093",
      "name": "Andreas",
      "connected": true
    }
  ],
  "started": false
}
```

- `id` er spillerens stabile, servergenererede GUID, serialiseret som tekst. Det ændres ikke ved genforbindelse og er hverken kode, forbindelses-id eller placering i listen.
- `name` er det validerede og trimmede visningsnavn. Ens visningsnavne tillades, fordi identiteten ligger i `id`.
- `connected` angiver, om spilleren på svartidspunktet har en accepteret forbindelse, som stadig giver adgang. En frakoblet spiller bliver i listen og optager stadig sin plads.
- `started` starter som `false` og bliver `true`, når spillestart accepteres.
- `players` følger rækkefølgen af accepterede tilmeldinger. Denne rækkefølge bruges også i spillets `players`; første deltager får `currentPlayer: 0`.
- Koder og forbindelses-id'er udstilles ikke i spillerlisten. Svar er selvstændige kopier og deler ikke skrivbare arrays eller objekter med sessionerne.

### Tilmelding og validering

Browseren sender navn og kode som tekst:

```json
{
  "name": "Andreas",
  "code": "0012"
}
```

1. Body skal være et gyldigt JSON-objekt med tekstfelterne `name` og `code`.
2. Kontroltegn afvises i det oprindelige navn, også i begyndelsen og slutningen. Navnet trimmes derefter og skal indeholde 1–20 UTF-16-kodeenheder, ligesom længdekontrollen i det eksisterende spil-API. Navnet kan dermed sikkert indgå i JOINED og WAIT.
3. Koden skal være præcis fire ASCII-cifre fra `0` til `9`. Den behandles som tekst, så `0012` og `0000` er syntaktisk gyldige. Et JSON-tal accepteres ikke som kode.
4. Koden skal tilhøre en kendt, endnu ikke tilmeldt Oplà med en åben READY-forbindelse. En kode kan kun tilmeldes én gang, også hvis en request gentages med samme navn.
5. Højst seks spillere kan tilmeldes, inklusive frakoblede spillere. Nye tilmeldinger afvises efter start.
6. En accepteret tilmelding opretter én spilleridentitet, binder den til forbindelsen og sender `JOINED navn` til Oplàen.

Kodeforbrug, kontrol af kapacitet og registrering sker atomisk. To samtidige requests kan derfor hverken tilmelde samme kode to gange eller skabe en syvende spiller.

### Spillestart

Start kræver en lobby med 1–6 tilmeldte spillere, som alle er forbundne. Start fryser deltagerlisten og opretter én frisk pointtavle pr. spiller, fem uslåede terninger, ingen holdte terninger og nul brugte slag. Spil-API'ets eksisterende JSON-felter bevares.

Serveren sender `STATE 0 00000` til første deltager og `WAIT navn` til de øvrige, hvor navnet er den aktive spillers navn. **Midlertidigt efter trin 5:** Alle tilmeldte Oplàers ROLL/HOLD afvises fortsat. Trin 6 tilføjer identitetskontrol og åbner kun for første spillers Oplà. HTTP-handlinger arbejder allerede på første deltager.

Et feltvalg afslutter fortsat den første tur efter [pointkontrakten](scoring.md). Turen forbliver derefter låst; næste tur, feedback ved turskifte og END følger i #44. Et startet lobbyspil kan ikke nulstilles med endnu et startkald.

Det eksisterende `POST /api/game/new` bevares til selvstændig enkeltspillerbrug. Når et lobbyspil er startet, afvises denne rute med 409, så lobbyspillet ikke overskrives.

### HTTP-fejl

| Status | Betydning |
|---|---|
| 400 | Ugyldig JSON, forkert body-type, manglende/ugyldigt tekstfelt, ugyldigt navn eller forkert kodeformat. |
| 404 | Koden er syntaktisk gyldig, men ukendt eller bortfaldet. |
| 409 | Koden er allerede tilmeldt, lobbyen er fuld/startet, en nødvendig forbindelse er væk, start mangler deltagere, eller start gentages. |

Fejl bruger samme format som spil-API'et:

```json
{
  "message": "Koden er allerede tilmeldt."
}
```

Afviste handlinger ændrer ikke lobby eller spil. En accepteret tilstandsændring og en efterfølgende netværksfejl er forskellige forhold; reglerne for afsendelsesfejl står nedenfor.

## READY, parring og genforbindelse

Tekstbeskederne bevarer formatet fra [kommunikationsprotokollen](protocol.md). Serverens udgående WebSocket-beskeder har ingen afsluttende linjeskift; browser-broen tilføjer linjeskift ved skrivning til USB.

| Situation | Aftalt adfærd |
|---|---|
| Første `READY` | Udsted en unik firecifret kode og send `PAIR cccc`. Opret ikke et spil eller en spillerbinding. |
| Gentaget READY på samme kendte forbindelse | Genbrug forbindelsens eksisterende tilknytning og genskab dens svar. En anden medsendt kode må ikke skifte dens spilleridentitet. |
| `READY cccc` med en kendt kode på en ny forbindelse | Overtag kodens tilknytning. En tilmeldt spiller beholder samme spiller-id, navn og pointtavle uden endnu et join. |
| Gammel forbindelse ser stadig åben ud | Tilbagekald straks dens adgang og luk den. Gamle objektreferencer og sen oprydning må ikke tilbagekalde den nye forbindelses adgang. |
| Ukendt eller bortfaldet kode | Udsted en ny PAIR-kode uden automatisk tilmelding eller adgang. Det giver også et forløb efter servergenstart. |
| Lukket forbindelse uden tilmeldt spiller | Fjern den ubrugte kode. Oprydning må kun ramme den forbindelse, koden stadig tilhører. |
| Lukket forbindelse med tilmeldt spiller | Bevar kode og spiller til genforbindelse. Spilleren vises med `connected: false`. |

Ved genforbindelse genskabes svar i rækkefølgen PAIR, eventuelt JOINED og efter spillestart aktuel STATE/WAIT. Dette er implementeret. Browseren får også en PAIR-hændelse, der kan udfylde kodefeltet igen. Tidligere kast og hold-kommandoer genafspilles aldrig. En allerede kendt forbindelse skifter ikke tilknytning; den bevarede kode skal medsendes i første READY på den nye forbindelse.

## Samtidighed, afsendelse og levetid

- Lobbyens tilstand beskyttes med en kort lås. Bindingsoperationer er serverinterne; klientens PAIR/JOINED kan aldrig give adgang.
- GameSession kontrollerer den aktive spiller og udfører handlingen under samme sessionslås, så start eller andre handlinger ikke kan ligge mellem identitetskontrol og kast.
- Lobbyens udgående svar samles i uforanderlige batches og køes i tilstandsændringernes rækkefølge pr. forbindelse. Et forsinket JOINED må eksempelvis ikke overskrive en nyere STATE på Oplàens skærm.
- Afsendelse og lukning afventes uden lobby-, register-, forbindelsestilstands- eller spilsessionslås. Transportens eksisterende fælles afsendelsesvej anvendes.
- En tilmelding eller start, som allerede er accepteret, rulles ikke tilbage eller udføres igen ved sendesvigt eller senere annullering. Genforbindelse genskaber status ud fra den accepterede tilstand.
- Forbindelsesoprydning bruger den konkrete forbindelses identitet og kan ikke slette en nyere overtagelse. Serverens kode- og spillerrelation er adskilt fra den enkelte sockets levetid.
- Lobby og spil ligger alene i hukommelsen og går tabt ved servergenstart. Fjernelse af deltagere samt ny lobby/nyt spil indgår i #44. Indtil da nulstiller en servergenstart hele tilstanden.
- [Transportens ressourcegrænser](opla-websocket.md) bevares: 12 forbindelser/reservationer, 256 payload-bytes, 64 Unicode-kodepunkter og 20 komplette tekstbeskeder pr. rullende sekund pr. forbindelse.

## Intern trinliste

Vi implementerer kun ét aftalt trin ad gangen. Hvert trin afsluttes med relevante kontroller og en kort forklaring af resultatet. Listen opdateres efter faktisk udført arbejde; den opretter ingen ekstra GitHub-issues.

- [x] **Trin 1 – Branch og kontrakt:** Kontrollér rent arbejdstræ, afhængigheder og GitHubs main. Opret `S2-9-lobby-parring` og dette dokument med API, parring, afgrænsning og trinliste. Kontrollér JSON-eksempler, links og whitespace. Ingen programkode ændres.
- [x] **Trin 2 – Lobbytilstand og kodeudstedelse:** Tilføj LobbySession med kodeposter pr. ConnectionId og kollisionskontrollerede koder. Tilkobl READY/PAIR uden spillerbinding eller spiloprettelse. Test gentagelser, oprydning, samtidighed og afsendelsesfejl. Spilleridentiteter og deltagerrækkefølge oprettes ved join i trin 3.
- [x] **Trin 3 – Tilmelding og lobbylæsning:** Implementér join og GET med validering, serverbinding og JOINED. Test ukendt kode, dobbelt tilmelding, grænsen på seks og samtidige tilmeldinger.
- [x] **Trin 4 – Genforbindelse og overtagelse:** Genfind tilknytningen, tilbagekald gammel adgang, luk den gamle forbindelse, og genskab svar på den nye. Test sen oprydning og samtidige overtagelser.
- [x] **Trin 5 – Minimal spillestart:** Implementér start med 1–6 spillere og hver sin pointtavle. Første spiller er aktiv og modtager STATE; øvrige modtager WAIT. Bevar spil-JSON-formatet og turafslutningen. Beskyt samtidig mod `/api/game/new`; dette værn er flyttet frem fra trin 6.
- [x] **Trin 6 – Identitet i spilhandlingen:** Før PlayerId fra transport til handler/session og kontrollér identitet sammen med handlingen. Afvis andre spilleres Oplà-input. Nulstillingsværnet er allerede implementeret i trin 5.
- [x] **Trin 7 – Samlet backendkontrol:** Test hele forløbet gennem HTTP og WebSocket med den rigtige app-pipeline. Tilpas kun de tidligere tests, som beskriver den midlertidige READY-standard, og bevar transportens øvrige regressionstests.
- [x] **Trin 8 – Gennemgang og reviewleverance:** Kontrollér kode, danske kommentarer, dokumentation, JSON og links. Kør Release-build, hele testsuiten og diff-kontrol. Forbered én PR med `Closes #59` og faktiske resultater. Merge og grøn CI på main er særskilte afslutningskrav.

## Kontrolscenarier og aflevering

Følgende er acceptscenarier for hele #59. Funktionaliteten er implementeret i trin 2–6. Trin 7 samler dækningen i krav/test-matrixen og kontrollerer transportafvisning, kapacitet, ny serverinstans og en separat Production-server. Reviewleverancen i trin 8 findes i PR #81, som også dokumenterer merge og CI på main:

| Område | Scenarier |
|---|---|
| Koder og READY | Kodekollision, `0012`, `0000`, gentaget READY, forskellig medsendt kode på kendt forbindelse samt bortfaldet kode efter servergenstart. |
| Tilmelding | Tomme/for lange navne, kontroltegn, forkerte JSON-typer, forkert kodeformat, ukendt kode, dobbelt tilmelding, ens navne med forskellige identiteter og syvende spiller. |
| Start | Nul, én og seks deltagere, frakoblet deltager, gentaget start samt tilmelding efter start. |
| Samtidighed | Samme kode tilmeldes samtidigt, to requests om sidste plads, samtidige startkald, join/start/genforbindelse og bevaret beskedrækkefølge. |
| Overtagelse | Gammel forbindelse er stadig åben, gammel adgang tilbagekaldes, tilstanden bevares, ny binding overlever sen oprydning og kun én forbindelse har den aktuelle adgang. |
| Spil | Kun første spillers Oplà kan påvirke lobbyspillet, ingen Oplà-handling før start, egne pointtavler, kopierede svar og beskyttelse mod overskrivning gennem `/api/game/new`. |
| Fejl og miljø | Afsendelsesfejl og annullering gentager ingen accepteret handling; samme adfærd i Development og Production; eksisterende transportgrænser bevares. |

**Baseline før #59:** Release-build med 0 fejl og 0 advarsler samt 794 beståede tests er tidligere verificeret lokalt på `10c692f` i denne samtale. [CI efter merge af #63](https://github.com/Bmantaz/H3-Yatzi/actions/runs/42428468625) bestod på samme commit, inklusive Arduino-kompilering. Disse resultater dokumenterer udgangspunktet og er ikke verifikation af den endnu ikke implementerede lobby.

**Kontrol af trin 1:** Streng UTF-8-afkodning, whitespace, alle fire JSON-eksempler, otte lokale links inklusive anker og rækkefølgen af de otte interne trin er kontrolleret. Der er alene oprettet dette dokument. Backendtests er ikke genkørt for dokumentændringen; baseline ovenfor er fra den tidligere kontrol.

**Kontrol af trin 2 (historisk):** Release-build og hele testsuiten bestod som dokumenteret ovenfor. Streng UTF-8, scope, fire JSON-eksempler, 60 lokale dokumentationslinks inklusive anker og diff-/whitespacekontrol bestod. READY/PAIR og fortsat manglende adgang blev testet gennem den rigtige app-pipeline. Der blev ikke udført fysisk hardwaretest eller en ny GitHub-CI-kørsel. Trin 3 var ikke påbegyndt ved denne kontrol.

**Kontrol af trin 3 (historisk):** Release-build med 0 fejl/0 advarsler og alle 889 tests bestod. Kode, danske kommentarer, afgrænsning og diff blev gennemgået. Streng UTF-8 og whitespace blev kontrolleret i alle 17 nye/ændrede tekstfiler fra trin 1–3; den forventede sletning af den gamle tomme handlers testfil var bevaret. Alle 13 JSON-eksempler og 73 lokale links i README samt lobby-, API-, WebSocket- og handlerdokumentationen bestod. Der blev ikke udført fysisk hardwaretest eller en ny GitHub-CI-kørsel. Ændringerne var lokale og ikke committed/pushet. Trin 4 var ikke påbegyndt ved denne kontrol.

**Kontrol af trin 4 (historisk):** Release-build med 0 fejl/0 advarsler og alle 946 tests bestod. Kode, danske kommentarer, afgrænsning og diff blev gennemgået. De fem app-tests med åben gammel forbindelse bestod også ved tre gentagelser efter lukkerettelsen. Streng UTF-8, whitespace og scope blev kontrolleret i alle 22 nye/ændrede tekstfiler fra trin 1–4; den forventede sletning fra trin 2 var bevaret. Alle 13 JSON-eksempler og 75 lokale dokumentationslinks bestod. Frontend, Arduino, spilregler, GameSession og spil-endpoints var uændrede. Der blev ikke udført fysisk hardwaretest eller en ny GitHub-CI-kørsel. Ændringerne var lokale og ikke committed/pushet. Trin 5 var ikke påbegyndt ved denne kontrol.

**Kontrol af trin 5 (historisk):** Release-build med 0 fejl/0 advarsler og alle 993 tests bestod. Kode, danske kommentarer, dataflow og diff blev gennemgået. Streng UTF-8, whitespace og scope blev kontrolleret i alle 30 nye/ændrede tekstfiler fra trin 1–5; den forventede sletning fra trin 2 var bevaret. Alle 17 JSON-eksempler og 96 lokale links i README samt lobby-, spil-, point-, WebSocket- og handlerdokumentationen bestod. Yatzi.Core, frontend, Arduino og pakker var uændrede. Der blev ikke udført fysisk hardwaretest eller en ny GitHub-CI-kørsel. Ændringerne var lokale og ikke committed/pushet. Trin 6 var ikke påbegyndt ved denne kontrol; kun nulstillingsværnet var efter planen flyttet frem til trin 5.

**Kontrol af trin 6 (historisk):** Release-build med 0 fejl/0 advarsler og hele testsuiten med **1.038 beståede tests**, ingen fejlede eller oversprungne. Trin 6 tilføjer 45 cases, og de 12 samtidigheds-/køtests bestod desuden tre gentagelser efter rettelse af testens afsendelsessynkronisering. Kode, danske kommentarer, identitetsflow, låserækkefølge og diff er gennemgået. Streng UTF-8, whitespace og scope består i alle 33 nye/ændrede tekstfiler fra trin 1–6; den forventede sletning fra trin 2 er bevaret. Alle 17 JSON-eksempler og 104 lokale Markdown-links inklusive ankre består. HTTP-kontrakter, Yatzi.Core, frontend, Arduino og pakker er uændrede i dette trin. Ændringerne er lokale og ikke staged, committed eller pushet; ingen PR eller ny GitHub-CI er oprettet. Fysisk Oplà-integration i #43 og trin 7–8 udestår.

**Kontrol af trin 7 (historisk):** Restore bestod, Release-build gav **0 fejl og 0 advarsler**, og hele testsuiten bestod med **1.048 tests**, ingen fejlede eller blev sprunget over. TRX bekræfter 1.048 udførte/beståede og 0 fejlede/ikke udførte. Trin 7 tilføjer 10 integrationstestcases. Den separate Production-prøve med rigtig Kestrel, standardregistreringer og HttpClient/ClientWebSocket bestod; kun prøveprocessen blev stoppet, og porten blev frigivet. Krav/test-matrix, kommentarer og testkode er gennemgået. Diff-, scope-, streng UTF-8- og whitespacekontrol består for alle 34 nye/ændrede tekstfiler fra trin 1–7 samt den forventede sletning fra trin 2. Alle 17 JSON-eksempler og 135 lokale Markdown-links inklusive ankre består. Hashkontrol mod starten af trin 7 bekræfter, at kun den nye integrationstestfil, transporttestens kommentar/navn og fire dokumentationsfiler er ændret; produktionskode, kontrakter, konfiguration og pakker er uændrede. Ændringerne er lokale uden staging, commit, push eller PR. Trin 8 er ikke påbegyndt; fysisk integration i #43 og turskifte/livscyklus i #44 udestår.

**Kontrol af trin 8 (historisk, før merge):** Hele leverancen og den staged diff er gennemgået, inklusive nye filer og den forventede sletning. Lokal restore og Release-build består med 0 fejl/0 advarsler og 1.052 beståede tests; den separate Production-prøve er genkørt efter valideringsrettelsen. Scope, streng UTF-8, whitespace, alle 17 JSON-eksempler og 137 lokale dokumentationslinks består for 35 nye/ændrede tekstfiler samt én forventet sletning. Testartefakter er udeladt fra Git. Feature-, runner- og testsynkroniseringscommits er pushet til PR #81; første samlede grønne CI er verificeret på 4fe624c som beskrevet ovenfor. CI på denne dokumentationscommit dokumenteres i PR-beskrivelsen. Alle interne trin er afsluttet som reviewleverance. Ingen merge er udført, og #59 er fortsat åben.

Ved kodetrinene køres relevante tests, og ved samlet slutkontrol køres hele løsningen. Ingen test markeres bestået, før den er kørt med den relevante implementering.

Fysisk Oplà, browserens lobby, USB/WiFi og tunnel kræver fortsat særskilt integrationstest. Når disse dele er klar, skal en praktisk prøve vise kode på Oplàen, korrekt JOINED, STATE til første spiller, WAIT til de øvrige samt genforbindelse med bevarede point og uden ekstra kast. Denne prøve er endnu ikke udført.

## Kommende kontrakt for #44

**Aftalt i trin 1 – implementeret og lokalt slutkontrolleret gennem trin 8.** Afleverings-, merge- og CI-status dokumenteres i [PR #87](https://github.com/Bmantaz/H3-Yatzi/pull/87). Join-svarets joinedPlayerId, HTTP-kontekst, de udvidede spilsvar, controllerudsendelser og omkampsruten nedenfor er implementeret. Se [samlede kontrolresultater og krav/test-matrix](game-lifecycle.md#slutkontrol-af-44-i-trin-8). De tidligere afsnit bevarer #59 som historik.

### Tilmelding og entydigt spiller-id

GET /api/lobby bevarer præcis sin nuværende form med `players` og `started`. `POST /api/lobby/join` beholder requesten `{"name":"Alex","code":"1377"}`, de eksisterende validerings-/fejlregler og HTTP 200 med den opdaterede lobby. Kun join-svaret udvides med et obligatorisk `joinedPlayerId`, der er den spilleridentitet, netop denne request oprettede.

Et fuldt join-svar kan indeholde begge Alex-spillere efter samtidige tilmeldinger:

```json
{
  "players": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "name": "Alex",
      "connected": true
    },
    {
      "id": "22222222-2222-4222-8222-222222222222",
      "name": "Alex",
      "connected": true
    }
  ],
  "started": false,
  "joinedPlayerId": "22222222-2222-4222-8222-222222222222"
}
```

Browseren gemmer joinedPlayerId som eget playerId i sessionStorage og finder sit indeks ved id i den faste deltagerrækkefølge. Dette er implementeret i trin 7; join med started=true skifter til spillet efter at have gemt netop requestens id. En startet lobby tilsidesætter et gammelt enkeltspillerflag, og et id, der ikke længere er tilmeldt, ryddes. Den må ikke vælge den sidste spiller med samme navn. Join-resultatet fastholder den accepterede identitet, selv om et andet join eller start når at ændre lobbyen, før det friske lobbyøjebliksbillede returneres. JOINED sendes fortsat kun til den tilmeldtes konkrete Oplà-forbindelse. Ens navne er tilladte og giver forskellige id'er.

### Første start og låst lobby

`POST /api/lobby/start` bruger fortsat ingen body og kræver 1–6 tilmeldte, forbundne deltagere. Første start opretter nyt gameId og det [udvidede spil-JSON](api.md#udvidet-spiltilstand). Første spiller får STATE 0 00000, resten WAIT med første spillers navn. Et nyt startkald erstatter ikke omkampsruten.

Efter start forbliver `started=true`, også på slutskærmen og efter omkamp. Deltagerlisten, rækkefølgen, id'erne og parringskoderne genbruges. Nye joins er stadig afvist med den eksisterende 409-fejl. `/api/game/new` forbliver afvist med 409 i et startet lobbyspil; den rute gælder kun selvstændig enkeltspiller.

### Omkamp

**Backend implementeret i #44 trin 6; frontendknappen tilsluttet i trin 7.** Requests, svar og fejlcases er dækket automatisk. Node-tests, rigtig Chrome og separate lokale Production-prøver med HTTP/WebSocket består. Browserprøven omfatter omkamp fra anden deltager, afvisning af gammel omkamp, fejltekst gennem polling og genindlæst identitet; Production-prøverne dækker 1, 2 og 6 deltagere med to hele spil og to omkampe hver.

Den nye rute `POST /api/lobby/restart` bruger `Content-Type: application/json` og følgende body:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "playerId": "11111111-1111-4111-8111-111111111111"
}
```

Begge felter er obligatorisk tekst med gyldige, ikke-tomme GUID'er. gameId identificerer det afsluttede spil; playerId er initiativtagerens tilmeldte id. turnNumber bruges ikke, fordi omkampen vedrører hele det afsluttede spil. Ukendte JSON-felter ignoreres.

Accept kræver samtidigt:

- gameId matcher serverens aktuelle, afsluttede lobbyspil.
- Initiativtageren er en af de tilmeldte deltagere. Der er ingen særskilt værtsrolle; enhver deltager kan starte omkamp.
- Alle deltagere har en aktuel, åben Oplà-forbindelse med den korrekte serverbinding.

LobbySession indsamler konkrete modtagere og kontrollerer deres åbne bindinger under lobbylåsen. GameSession.RestartLobbyGame kontrollerer spil-id, lobbytilstand, medlemskab og afslutning under sessionslåsen og vurderer derefter lobbyens booleske forbindelsesforudsætning. Metoden opretter nye Game-instanser ud fra de eksisterende deltagere med samme tilfældighedskilde. Annullering kontrolleres før og efter ventetid på låsene og lige før udskiftning. Kontrol, frisk spiloprettelse og reservation af STATE/WAIT-batches koordineres under den eksisterende låserækkefølge lobby → spilsession. Ingen netværksafventning sker under tilstandslåsene. HTTP 200 indeholder et fuldt, kopieret spiløjebliksbillede med følgende startværdier, her vist som svarudsnit:

```json
{
  "gameId": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
  "turnNumber": 1,
  "currentPlayer": 0,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false,
  "gameCompleted": false,
  "winnerIndices": []
}
```

Alle 15 felter hos alle spillere er null, og upperSum, bonus og total er 0. Samme id'er, navne, koder og rækkefølge bevares; intet kast sker automatisk. Første spiller modtager STATE 0 00000, øvrige spillere WAIT med første spillers navn. Et sendesvigt efter accept fortryder ikke omkampen eller et ellers aktivt HTTP-requests 200-resultat. Genforbindelse genskaber den accepterede tilstand.

To samtidige omkampsrequests med samme afsluttede gameId kan højst give én accept. Efter accept er det gamle gameId forældet, så den anden request afvises. Serveren genstarter aldrig det nye spil igen som følge af den gamle request. Klienten genafspiller ikke omkampsrequesten automatisk. Den fastholder gameId og eget playerId ved klik, opdaterer visningen ved 409 og bevarer den danske fejltekst gennem efterfølgende polling. Tilskuere har ingen omkampsknap. Enkeltspillerens Nyt spil går fortsat tilbage til lobbyen og bruger /api/game/new ved næste oprettelse.

### Fejl ved omkamp

Fejl er danske JSON-objekter med `message`. Hele slutresultatet og lobbyen bevares ved afvisning. Fejlprioriteten er JSON-format og felter i rækkefølgen gameId, playerId → manglende spil → gameId-match → startet lobbyspil → deltagerrelation → afsluttet spil → aktuelle forbindelser.

| Situation | Status | Aftalt message |
|---|---|---|
| Body mangler, er ugyldig JSON eller er ikke et objekt | 400 | Requesten skal indeholde et gyldigt JSON-objekt. |
| Manglende eller ugyldigt gameId | 400 | gameId skal være et gyldigt, ikke-tomt GUID. |
| Manglende eller ugyldigt playerId, inklusive null | 400 | playerId skal være et gyldigt, ikke-tomt GUID. |
| Intet spil er oprettet | 404 | Der er ikke oprettet et spil. Start et nyt spil først. |
| gameId matcher ikke det aktuelle spil | 409 | Spillet er ikke længere aktuelt. Hent spiltilstanden igen. |
| Det aktuelle spil er ikke et startet lobbyspil | 409 | Der er ikke et startet lobbyspil at genstarte. |
| Initiativtageren er ikke tilmeldt | 409 | Spilleren deltager ikke i dette lobbyspil. |
| Lobbyspillet er stadig aktivt | 409 | Spillet skal være afsluttet før omkamp. |
| Mindst én deltager er frakoblet eller mangler sin aktuelle binding | 409 | Alle tilmeldte spillere skal være forbundet før omkamp. |

playerId angiver en spilrelation, ikke et login eller en hemmelig værtsnøgle. Tilskuere har ingen deltagerknap i frontend, og en manglende eller uvedkommende identitet afvises som beskrevet. Der indføres ingen ny autentifikation i #44.

### Afbrydelse, READY og beskedrækkefølge

Ved afbrydelse bevares deltager, tur, point og parring. Den aktive spiller springes ikke over. Browserens reservehandlinger kan accepteres med korrekt spil-/turkontekst, selv om Oplàen er frakoblet. Start og omkamp kræver derimod alle tilmeldte deltagere forbundne ved serverens accepterede kontrol; et senere udfald fortryder ikke accepten.

READY på den aktuelle tilmeldte forbindelse sender altid PAIR og JOINED, derefter:

| Servertilstand | Sidste besked i READY-batchet |
|---|---|
| Lobby før start | Ingen ekstra besked. |
| Aktivt spil, spilleren har tur | STATE med aktuelle slag og hold. |
| Aktivt spil, en anden har tur | WAIT med den aktive spillers navn. |
| Afsluttet spil | END. |

De eksisterende formater og transportsikringer bevares. Gamle batches beholder deres konkrete modtager, og sen gammel oprydning må ikke lukke en ny overtager. Feltvalg og omkamp reserverer svar i samme kø som READY og Oplà-input, så statusbeskeder ikke overhaler hinanden på samme forbindelse. Afsendelsesfejl eller annullering genudfører ingen accepteret handling. Oplàens tekstbeskeder indeholder fortsat ikke gameId/turnNumber og fortolkes ved serverens behandling.

### Acceptgrundlag og status

[Scenarier A–H](game-lifecycle.md#konkrete-acceptscenarier) beskriver første start, rotation, enkeltspiller, sidste felt, uafgjort, gamle requests, afbrydelse og omkamp. Join-eksemplet ovenfor er yderligere acceptgrundlag for samtidige tilmeldinger med ens navne. Kontrakterne i A–H er nu dækket gennem trin 6. De faste eksempelværdier er illustrative og beskrives ikke som optagne testsvar. To hele spil og to omkampe med bevarede parringer er kontrolleret via HTTP/WebSocket med 1, 2 og 6 spillere i Development, Production, Staging og Test. Se [trin 6 og faktiske kontroller](game-lifecycle.md#implementeret-omkamp-i-trin-6).

Den brugeroplyste USB-kontrol fra Frederik er grundlaget for at gå videre uden en ny fysisk indledende prøve. #43 venter og lukkes ikke af #44 trin 1. Der tilføjes ikke WiFi, database eller login, og den lokale noCase-tilpasning holdes uden for fællesprojektets leverance.
