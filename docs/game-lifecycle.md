# Intern arbejdsplan for #44 – turskifte og spillets livscyklus

Planen dækker [#44 – S3.1](https://github.com/Bmantaz/H3-Yatzi/issues/44): et spil for 1–6 spillere, næste spiller efter feltvalg, STATE/WAIT/END, spilafslutning, vinder inklusive uafgjort og mulighed for et nyt spil.

**Status 10. november 2026:** Alle lokale trin 1–8 er gennemført og kontrolleret. Rigtig Chrome, Production-forløb med 1/2/6 deltagere, 39 frontendtests, 1.379 backendtests og Arduino-kompilering af fællessketchen består. Release-build har 0 fejl/0 advarsler, og dokumentation, JSON-eksempler, links og samlet diff er kontrolleret. Arbejdsbranchen `S3-1-turskifte` blev oprettet fra main på `a92c8a9` og har nu integreret WiFi/USB fra PR #86 på `db3abab`. Den nye [kontrol før PR](#kontrol-før-pr-efter-integration-af-main) består med samme testantal. Aflevering sker via PR; merge og grøn CI på main håndteres særskilt.

## Grundlag og afgrænsning

- Lobby/parring, WebSocket, pointberegning og frontendens lobby, pointtavle, polling og slutskærm er allerede på main. Vi udvider de eksisterende komponenter.
- Brugeren har oplyst, at Frederik har gennemført fysisk kontrol af USB → browserbro → backend → skærm. Kontrollen gentages ikke som forberedelse til #44. Det er brugeroplyst verifikation, ikke en fysisk test udført af Codex.
- #43 venter og indgår ikke som et selvstændigt arbejdstrin her. Arbejdet med #44 afslutter ikke #43.
- Direkte WiFi i #53/#54, database og brugerlogin indgår ikke i denne opgave.
- Den lokale `carrier.noCase()`-tilpasning skal bevares uden at indgå i commits til fællesprojektet.
- Baseline før trin 2: 1.052 beståede tests, ingen fejlede eller oversprungne. Det er et udgangspunkt, ikke dokumentation for endnu ikke implementeret funktionalitet i #44. Resultater efter kerneudvidelsen står under trin 2 nedenfor.

## Arbejdsform

Vi udfører ét nummereret trin ad gangen. Et trin markeres først færdigt, når dets resultat og relevante kontroller er dokumenteret. Efter hvert trin beskrives ændringer, testresultater og eventuelle udeståender, før næste trin påbegyndes. Trin 1 leverer dokumentation; de øvrige trin leverer implementering og verifikation.

Tests tilføjes sammen med den funktionalitet, de kontrollerer. Eksisterende tests, der bevidst forventer stop efter første feltvalg, opdateres til de nye regler; relevante kontroller af point, afvisninger og samtidighed bevares.

## Trinlisten

### 1. Fastlæg spiltilstand og kontrakter – kun dokumentation

- [x] Beskriv forløbet: lobby → aktivt spil → afsluttet spil → nyt spil.
- [x] Fastlæg betydningen af `currentPlayer` og `turnCompleted` efter et automatisk turskifte og ved spilafslutning. Bevar et gyldigt spillerindeks, som den eksisterende frontend kan læse.
- [x] Beskriv, hvordan spilafslutning og alle vindere udstilles i spil-JSON. Vinderidentitet skal kunne skelne spillere med samme navn.
- [x] Fastlæg én HTTP-kontrakt for feltvalg og øvrige spilhandlinger i både enkeltspiller og lobbyspil, herunder kontrol af spil, spiller og forventet tur. Genbrug lobbyens identiteter; der indføres ikke login.
- [x] Fastlæg API-kontrakten for omkamp efter afslutning med samme spillere, rækkefølge og parringer. Et igangværende lobbyspil må ikke overskrives. Beskriv også reglerne for frakoblede deltagere og samtidige anmodninger.
- [x] Bevar Oplà-protokollens eksisterende tekstbeskeder: `STATE r hhhhh`, `WAIT navn` og `END`.

**Færdigt når:** Tilstande, JSON, requests, fejl og beskedrækkefølge er beskrevet med konkrete eksempler. Implementering kan begynde uden uafklarede grænser mellem backend, lobby og frontend.

**Berørte materialer:** [API](api.md), [lobby](lobby.md), [protokol](protocol.md), [GameContracts](../backend/Yatzi.Api/GameContracts.cs) og [frontend](../frontend/app.js). Dette trin ændrer dokumentation, ikke programkode.

**Aftalegrundlag:** [Specifikationen nedenfor](#aftalt-specifikation-for-44), [den kommende spil-API-kontrakt](api.md#kommende-kontrakt-for-44) og [den kommende lobbykontrakt](lobby.md#kommende-kontrakt-for-44).

### 2. Gør én spillers næste tur mulig i Yatzi.Core

- [x] Tilføj en lille, kontrolleret overgang fra afsluttet tur til ny tur i `Game`.
- [x] Nulstil terninger til uslået tilstand, hold-markeringer, slag og turafslutning. Bevar pointtavle, bonus og total.
- [x] Gør det muligt at afgøre, om spillerens 15 felter er udfyldt. Gemte nuller tæller som udfyldte felter.
- [x] Bevar reglerne om højst tre slag, gyldigt hold/slip og ét feltvalg pr. tur. Afvis ugyldig turstart uden tilstandsændring.
- [x] Test flere ture, bevarede point, strygning og grænserne for ny tur.

**Færdigt når:** En enkelt `Game` kan gennemføre gentagne ture med samme pointtavle, og de relevante kernetests består.

**Primære filer:** [Game](../backend/Yatzi.Core/Game.cs), [Scorecard](../backend/Yatzi.Core/Scorecard.cs) og [GameTests](../backend/Yatzi.Tests/GameTests.cs).

**Implementering og kontrol:** Se [kerneudvidelsen i trin 2](#implementeret-kerne-i-trin-2). Ved afslutningen af trin 2 blev ny tur kun åbnet eksplicit i kernen. Trin 3 kobler denne metode til sessionens feltvalg og rotation.

### 3. Indfør spillerrotation i GameSession

- [x] Erstat den faste spiller 0 med et aktuelt spillerindeks, der følger tilmeldingsrækkefølgen.
- [x] Lad både pointvalg og strygning afslutte den aktive tur og gå videre til næste spiller.
- [x] Sørg for rotation fra sidste spiller tilbage til første. Med én spiller begynder samme spillers næste tur.
- [x] Lad terninger, hold, slag og mulige point i svaret tilhøre den nye aktive tur. Start ingen kast automatisk.
- [x] Ret Oplà-identitetskontrollen til at følge den aktive spiller.
- [x] Udfør feltvalg, turskifte og kopiering af svaret samlet under sessionens eksisterende lås.
- [x] Test rotation med 1, 2 og 6 spillere, separate pointtavler, spilleradgang og afvisninger uden ændringer.

**Færdigt når:** Flere spillerunder kan gennemføres gennem sessionen, uden at pointtabeller blandes eller turdata følger med til næste spiller.

**Primære filer:** [GameSession](../backend/Yatzi.Api/GameSession.cs), [GameLobbySessionTests](../backend/Yatzi.Tests/GameLobbySessionTests.cs) og [GameOplaIdentityTests](../backend/Yatzi.Tests/GameOplaIdentityTests.cs).

**Implementering og kontrol:** Se [spillerrotationen i trin 3](#implementeret-spillerrotation-i-trin-3). Den minimale stopregel ved alle fulde tavler er flyttet frem hertil, så sidste feltvalg ikke efterfølges af en fejl ved turstart. De samlede vinder-/slutstatusfelter hører fortsat til trin 4.

### 4. Afslut spillet og beregn vinder på serveren

- [x] Afslut først spillet, når alle 15 felter hos alle deltagere er udfyldt.
- [x] Undlad at åbne endnu en tur efter spillets sidste feltvalg.
- [x] Beregn vindere ud fra højeste total inklusive bonus. Bevar alle vindere ved uafgjort.
- [x] Udstil afslutning og resultat efter kontrakten fra trin 1, uden at blande turafslutning og spilafslutning.
- [x] Tilføj gameId og det globale turnNumber til alle spilsvar; bevar dem ved afvisning og annullering.
- [x] Afvis yderligere kast, hold og feltvalg efter spilafslutning uden at ændre resultatet.
- [x] Test sidste felt, sidste spiller, en fuld tavle mens andre mangler felter, bonus, uafgjort, ens navne og enkeltspiller.

**Færdigt når:** Sessionen kan gennemføre et helt spil og returnere et stabilt, korrekt slutresultat. Frontenden er ikke ansvarlig for at afgøre serverens spilstatus.

**Primære filer:** [GameSession](../backend/Yatzi.Api/GameSession.cs), [GameContracts](../backend/Yatzi.Api/GameContracts.cs) og de tilhørende sessionstests.

**Implementering og kontrol:** Se [spilafslutningen i trin 4](#implementeret-spilafslutning-i-trin-4). Efter aftale tilføjer dette trin også gameId og det globale turnummer til svarene; håndhævelse af HTTP-handlingskonteksten venter til trin 5.

### 5. Forbind HTTP-handlinger med STATE, WAIT og END

- [x] Koordinér browserens feltvalg og strygning med lobbyen, så turskifte også udløser beskeder til controllerne.
- [x] Send `STATE 0 00000` til den nye aktive spiller og `WAIT navn` til de øvrige. Send `END` til alle deltagernes aktuelle forbindelser ved afslutning.
- [x] Sørg for korrekt controllerstatus efter accepterede HTTP-kast og hold/slip, når browserknapperne bruges som reserve.
- [x] Lad READY/genforbindelse genskabe korrekt STATE, WAIT eller END uden at gentage en spilhandling.
- [x] Bevar eksisterende låserækkefølge, ordnede beskedkøer og afsendelse uden netværksarbejde under tilstandslåsene.
- [x] Håndhæv HTTP-kontrollen fra trin 1 og test samtidige feltvalg, forsinkede requests og samspillet med Oplà-input.
- [x] Test, at sendesvigt eller overtagelse ikke fortryder et accepteret feltvalg, genudfører handlinger eller sender gamle svar til en ny forbindelse.

- [x] Tilføj joinedPlayerId til join-svaret og bevar GET-formatet samt accepteret identitet under samtidige handlinger.

**Færdigt når:** HTTP, spilsession og Oplà-forbindelser følger samme tur og sluttilstand, med automatisk kontrol af modtagere og beskedrækkefølge.

**Primære filer:** [GameEndpoints](../backend/Yatzi.Api/GameEndpoints.cs), [LobbySession](../backend/Yatzi.Api/LobbySession.cs), [OplaMessageHandler](../backend/Yatzi.Api/OplaMessageHandler.cs) og HTTP-/WebSocket-tests.

**Implementering og kontrol:** Se [HTTP og controllerstatus i trin 5](#implementeret-http-og-controllerstatus-i-trin-5) og [omkamp i trin 6](#implementeret-omkamp-i-trin-6). Frontendtilpasningen er implementeret i trin 7; rigtig browserkontrol udestår.

### 6. Gør et nyt spil muligt efter afslutning

- [x] Implementér omkamp efter kontrakten fra trin 1 med samme deltagere, rækkefølge og parringer.
- [x] Nulstil pointtavler, summer, bonus, vindere og turdata samlet. Første spiller begynder uden et automatisk kast.
- [x] Send ny STATE/WAIT efter accepteret genstart og bevar READY/genforbindelse.
- [x] Afvis genstart af et igangværende lobbyspil. Håndtér frakobling og samtidige genstartsanmodninger efter kontrakten.
- [x] Sørg for, at gamle requests fra det afsluttede spil ikke kan påvirke omkampen.
- [x] Bevar selvstændig enkeltspillerbrug gennem den eksisterende oprettelse af nyt spil.
- [x] Test to på hinanden følgende spil, bevaret parring og afvisninger uden tab af slutresultatet.

**Færdigt når:** Et afsluttet spil kan efterfølges af et nyt gennem API'et uden servergenstart eller ny parring.

**Primære filer:** [GameSession](../backend/Yatzi.Api/GameSession.cs), [LobbySession](../backend/Yatzi.Api/LobbySession.cs), [LobbyEndpoints](../backend/Yatzi.Api/LobbyEndpoints.cs) og de tilhørende API-tests.

**Implementering og kontrol:** Se [omkamp i trin 6](#implementeret-omkamp-i-trin-6). Trin 7–8 er ikke påbegyndt.

### 7. Tilslut den eksisterende frontend til hele livscyklussen

- [x] Tilpas frontendens requests til kontrakten fra trin 1 og bevar korrekt kobling til egen spiller.
- [x] Brug backendens slutstatus og vinderresultat sammen med den eksisterende slutskærm, rangliste og turvisning.
- [x] Tilslut knappen til nyt lobbyspil efter afslutning. Vis fejl fra serveren, hvis genstart afvises.
- [x] Kontrollér, at polling og handlingssvar viser næste tur og omkamp korrekt, og at gammel turtilstand eller animation ikke hænger ved.
- [x] Kontrollér enkeltspiller, aktiv spiller, ventende spiller og tilskuer i browseren.

**Færdigt når:** Browseren kan vise hele forløbet fra start til vinder og starte et nyt spil med den eksisterende brugerflade.

**Primære filer:** [app.js](../frontend/app.js) og om nødvendigt [index.html](../frontend/index.html). Browserkontrol beskrives særskilt fra automatiske backendtests og fysisk hardwarekontrol.

**Aktuel kontrol:** Implementering, Node-tests, lokal integration og den [rigtige Chrome-kontrol i trin 8](#slutkontrol-af-44-i-trin-8) består. Trin 7 er nu helt færdigt.

### 8. Kontrollér hele #44 og opdatér dokumentationen

- [x] Gennemfør automatiske forløb med 1, 2 og 6 spillere gennem HTTP og WebSocket: start → flere spillerunder → sidste felt → vindere → nyt spil.
- [x] Kontrollér de relevante miljøer og regressioner for parring, spilleradgang, genforbindelse, samtidighed og sendesvigt.
- [x] Kør Release-build og hele backendens testsuite. Kontrollér også Arduino-kompilering som kompatibilitetskontrol, når værktøjerne er tilgængelige.
- [x] Gennemfør en lokal serverprøve med rigtige HTTP-/WebSocket-klienter og kontrollér frontendens forløb. Denne kontrol erstatter eller afslutter ikke #43.
- [x] Opdatér API-, lobby-, point-, handler- og WebSocket-dokumentation samt README, hvor #44 ændrer den beskrevne adfærd. Bevar historiske resultater som historik.
- [x] Sammenhold hvert krav i #44 med implementering og konkret verifikation. Markér fysisk eller anden manuel kontrol som udestående, hvis den ikke er udført.
- [x] Kontrollér diff, whitespace og at den lokale `noCase()`-tilpasning holdes uden for leverancen.

**Færdigt når:** #44 er implementeret og lokalt verificeret med en gennemgået diff og korrekt dokumentation. Resultaterne rapporteres, inden der gås videre til aflevering.

**Implementering og kontrol:** Se [slutkontrol, krav/test-matrix og kontrolgrænser](#slutkontrol-af-44-i-trin-8). Trin 7 og 8 er markeret færdige efter bestået browser- og slutkontrol. Se den efterfølgende [kontrol før PR](#kontrol-før-pr-efter-integration-af-main).

## Senere aflevering

Den lokale implementering afslutter ikke GitHub-issuet. #44 har som færdigkrav en PR med `Closes #44`, merge til main og grøn CI. Afleveringen sker fra `S3-1-turskifte` til `main` med en almindelig PR. PR-beskrivelsen dokumenterer den seneste commit og CI-status. Der bruges ingen auto-merge eller manuel issue-lukning; en senere merge og grøn CI på main afslutter færdigkravet.

## Implementeret kerne i trin 2

Dette afsnit beskriver den faktisk implementerede kerneudvidelse. Den samlede livscyklus og de nye HTTP-kontrakter i næste afsnit er fortsat mål for senere trin.

| Offentlig kernegrænse | Implementeret adfærd |
|---|---|
| `Scorecard.IsComplete` | Skrivebeskyttet status beregnet fra alle felters HasValue. Både beregnede og strøgne nuller tæller som udfyldte; der gemmes ingen ekstra tæller eller status. |
| `Game.IsScorecardComplete` | Videresender egen tavles fuldførelsesstatus. Den betyder ikke, at alle deltagere har afsluttet multiplayer-spillet. |
| `Game.StartNextTurn()` | Kræver en afsluttet tur og en tavle med ledige felter. Nulstiller de eksisterende terninge-/hold-arrays, RollsUsed og TurnCompleted uden at kaste eller ændre tavle, point, bonus eller tilfældighedskilde. |

Konstruktøren åbner første tur. `Roll → Score/Strike` registrerer fortsat ét felt og afslutter turen; kun et eksplicit StartNextTurn åbner den næste. Første kontrol afviser en stadig åben tur med `Den aktuelle tur skal afsluttes, før en ny tur kan starte.` Derefter afvises en fuld tavle med `Pointtavlen er fuld. Der kan ikke startes en ny tur.` Begge fejl bruger InvalidOperationException og bevarer hele tilstanden. Hverken accepteret eller afvist turstart bruger tilfældighed. Efter et accepteret kald er mulige point tomme, indtil spilleren selv kaster.

De skrivebeskyttede visninger følger fortsat samme arrays og pointtavle. Alle gamle feltværdier og optjent bonus bevares; udfyldte felter forbliver låste i næste tur. Game har fortsat ingen egen lås. Når GameSession senere genaktiverer en deltager, skal den kun kalde StartNextTurn for en afsluttet tur, ikke for en endnu ubrugt førstegangstur.

**Historisk integrationsgrænse ved afslutningen af trin 2:** GameSession, HTTP, lobby, frontend og Arduino var uændrede. API'et kaldte endnu ikke StartNextTurn, så første feltvalg låste stadig API-turen. Trin 3 nedenfor ændrer denne del. Samlet vinderstatus, nye spil-id'er, handlingsbodies og omkamp var fortsat kommende funktionalitet.

**Automatisk verificeret:** De fokuserede Game-/Scorecard-tests bestod med **165 tests**, 0 fejlede og 0 oversprungne. Derefter bestod Release-build med **0 fejl og 0 advarsler**, og hele backendens testsuite bestod med **1.070 tests**, 0 fejlede og 0 oversprungne. De eksisterende HTTP-/sessionstests bevarer deres hidtidige forventninger. TRX-resultaterne findes lokalt i `TestResults/step2-core.trx` og `TestResults/step2-all.trx` og indgår ikke i Git-leverancen.

Trin 2 tilføjer 18 testcases i de eksisterende Game-/Scorecard-tests. De kontrollerer score/strike efter 1–3 slag, nulstilling af gamle hold, låste felter, højst tre nye slag, bevaret bonus ved øvre sum 63, alle 15 ture, fuld tavle med nuller, uændrede afvisninger, referencebevarelse og isolation mellem spil. Snapshot-hjælperen omfatter nu også IsScorecardComplete. De faktiske scenarier for API-turskifte og multiplayer nedenfor er endnu ikke runtime-testet med en implementering af de senere trin.

**Slutkontrol af trin 2:** TRX bekræfter 165 beståede i den fokuserede kørsel og 1.070 i hele suiten, uden fejlede eller oversprungne tests; alle 18 nye cases indgår i begge kørsler. Kode, danske kommentarer og diff er gennemgået. De 30 JSON-blokke og 102 lokale links inklusive ankre i arbejdsplan, API- og lobbydokumentation består; streng UTF-8 og whitespace består. Hashkontrol bekræfter, at kun Game.cs, Scorecard.cs, de to tilhørende testfiler og denne arbejdsplan er ændret siden starten af trin 2. API-/lobbydokumentationen fra trin 1 og alle øvrige filer, inklusive lokal noCase, er bevaret. Trin 2 er markeret færdigt efter disse kontroller. Ændringerne er lokale uden staging, commit, push eller PR.

Hardwaretest og Arduino-kompilering blev ikke udført i trin 2. Frederiks tidligere brugeroplyste USB-kontrol er bevaret som grundlag, #43 venter, og den lokale noCase-tilpasning skal fortsat holdes uden for commits. Trin 3 blev ikke påbegyndt som del af trin 2.

## Implementeret spillerrotation i trin 3

Dette afsnit bevarer sessionsadfærden og kontrollerne ved afslutningen af trin 3 som historik. Trin 4 nedenfor tilføjer de nye spilfelter og eksplicit slutstatus; request-format og controllerudsendelser venter fortsat.

- GameSession ejer nu et privat aktivt spillerindeks. Første accepterede oprettelse vælger spiller 0; afvist eller annulleret oprettelse ændrer ikke det aktuelle spil eller indeks.
- Score og Strike registrerer feltet på den aktive deltagers tavle og kalder samme private rotation under den eksisterende sessionslås. Næste indeks følger tilmeldingsrækkefølgen, inklusive sidste → første. Med én deltager begynder samme spillers næste tur.
- En ny deltagers førstegangstur er allerede åben. En genaktiveret deltagers afsluttede tur åbnes med kernens StartNextTurn. Det nulstiller terninger, hold og slag, men bevarer tavle, point og bonus. Der kastes ikke automatisk.
- Feltvalg, rotation og kopiering af svaret er én låst operation. Spil-JSON har fortsat de samme syv felter, men currentPlayer er dynamisk. Efter almindeligt feltvalg viser det gemte point på den tidligere spillers tavle og næste spillers friske tur med turnCompleted false.
- Oplà-indgangen kontrollerer spiller-id mod det aktive indeks under samme lås som kast/hold. Tidligere, ventende, manglende og uvedkommende identiteter afvises uden at ændre spillet. Ens navne giver ingen særbehandling.
- Når alle tavler er fulde, bevarer sessionen sidste spillers indeks, terninger, hold og slag samt turnCompleted true. Den åbner ingen ekstra tur. Den minimale stopregel forhindrer en fejl efter et allerede gemt sidste felt; eksplicit spilstatus og vindere tilføjes i trin 4.

**READY og transport:** Den eksisterende READY-formatter læser currentPlayer og genskaber nu STATE hos den aktuelle spiller og WAIT hos øvrige, også efter rotation og overtagelse. READY giver ingen ekstra kast og ændrer ikke point. HTTP-feltvalg udsender endnu ikke status automatisk, og READY efter et helt afsluttet spil er endnu ikke tilpasset til END. Controllerudsendelser og END følger i trin 5. Der er ikke tilføjet netværkskald, nye låse eller callbacks til GameSession.

**HTTP-mellemtilstand:** Ruter, request-bodies og DTO-formater er ikke ændret. De hidtidige kald uden handlingskontekst arbejder fortsat på den aktive tur. De kan derfor ikke afvise requests alene ud fra et tidligere turnummer eller gameId; denne kontrol følger i trin 5. Ved konkurrerende kast og feltvalg kan kastet nu tilhøre den gamle eller den nye tur. Hvert svar bevarer sin egen accepterede tilstand; et svar fra feltvalget indeholder ikke længere det kast, pointene blev beregnet fra.

**Automatisk verificeret:** De berørte sessions-, identitets-, HTTP-, handler- og transporttests består med **728 tests**, 0 fejlede og 0 oversprungne. Derefter bestod Release-build med **0 fejl og 0 advarsler**, og hele backendens testsuite bestod med **1.104 tests**, 0 fejlede og 0 oversprungne. Det er 34 flere cases end efter trin 2. Resultaterne findes lokalt i `TestResults/step3-focused.trx` og `TestResults/step3-all.trx` og indgår ikke i Git-leverancen.

Testene dækker flere spillerunder og alle 15 felter med 1, 2 og 6 deltagere, score/strike, ens navne, dynamisk spilleradgang, annullering, kopierede svar, bevarede tavler, afvisninger, samtidige feltvalg og begge rækkefølger af kast/feltvalg. De to rækkefølger kontrolleres også særskilt med faste handlingsforløb, så dækningen ikke afhænger af schedulerens rækkefølge. Den rigtige HTTP-/WebSocket-app-pipeline i hukommelsen kontrollerer aktivt input efter rotation med 1, 2 og 6 deltagere i Development, Production, Staging og Test. READY/opla-overtagelse genskaber både næste spiller og en genaktiveret tidligere spiller uden ekstra kast.

De gamle sessionstests for permanent stop efter første feltvalg er omskrevet til at kontrollere næste tur. Kernefunktionerne og deres forventninger fra trin 2 er bevaret; kun deres nu udførte TODO-kommentarer er opdateret. HTTP-/handlerens samtidighedstests kontrollerer point fra den accepterede gamle tur og det korrekte efterfølgende svar frem for at beregne point fra nulstillede terninger.

**Slutkontrol af trin 3:** TRX-resultaterne bekræfter 728 henholdsvis 1.104 beståede tests uden fejlede eller oversprungne. Dækningen omfatter alle 12 miljø-/spillerantal-kombinationer i inputtesten, seks fuldspilscases og seks faste rækkefølgescenarier. Kode, kommentarer, testændringer og diff er gennemgået. Alle 30 JSON-blokke og 103 lokale links inklusive ankre i arbejdsplan, API- og lobbydokumentation består, ligesom streng UTF-8 og whitespace. Hashkontrol mod starten af trin 3 bekræfter afgrænsningen til 16 filer og bevarer tidligere ændringer i API-/lobbydokumentation samt lokal noCase. I Game.cs og GameTests.cs er kun to TODO-kommentarer ændret siden trin 2; deres funktioner og tests er ellers identiske. Der er ingen nye afhængigheder, ruter eller DTO-felter. Ændringerne er lokale uden staging, commit, push eller PR. Trin 3 er markeret færdigt efter disse kontroller.

Der er ikke udført hardwaretest, Arduino-kompilering eller en ny separat Kestrel-/tunnelprøve i trin 3. Frederiks brugeroplyste USB-kontrol gentages ikke, #43 venter, og trin 4 påbegyndes ikke som del af denne leverance.

## Implementeret spilafslutning i trin 4

Dette afsnit bevarer implementeringen og kontrollerne ved afslutningen af trin 4 som historik. Trin 5 nedenfor ændrer requests og controllerkoordinering. GameSession ejer fortsat den samlede overgang under sin eksisterende lås, mens hver Game ejer én spillers regler og tavle. HTTP og Oplà-handler bruger samme session. Der er ingen nye låse, callbacks eller netværkskald i sessionen.

| Svarfelt | Implementeret adfærd |
|---|---|
| `gameId` | Servergenereret, ikke-tomt GUID ved accepteret enkeltspilleroprettelse og første lobbystart. Bevares gennem alle handlinger og læsninger. |
| `turnNumber` | Fælles tæller fra 1. Øges præcis én gang ved et accepteret score/strike, som åbner næste tur, inklusive enkeltspiller. Sidste feltvalg øger den ikke. |
| `gameCompleted` | Beregnes fra alle deltagernes IsScorecardComplete under låsen; der gemmes intet ekstra statusflag. Gemte nuller tæller som udfyldte. |
| `winnerIndices` | Tomt under spillet. Ved afslutning alle indeks med højeste Game.Total, inklusive bonus, i stigende deltagerrækkefølge. Hvert slutsvar har sit eget vinderarray. |

Alle oprettelses-, GET-, handlings- og første lobbystartsvar indeholder nu de oprindelige syv felter plus disse fire. PlayerResponse og pointtavlens format er bevaret. Spiller-id'er tilføjes ikke til spillets players. Afvist eller annulleret oprettelse bevarer også id, turnummer og et eventuelt slutresultat.

Et almindeligt feltvalg gemmer feltet og returnerer næste spillers friske tur. Et svarudsnit efter første feltvalg i et tospillerspil kan derfor være:

```json
{
  "currentPlayer": 1,
  "turnCompleted": false,
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 2,
  "gameCompleted": false,
  "winnerIndices": []
}
```

Hvis alle tavler bliver fulde, åbnes ingen tur. Sidste indeks, terninger, hold og slag bevares; mulige point er tomme, og begge afslutningsfelter er true. Slutturnummeret er 15, 30 eller 90 for henholdsvis 1, 2 eller 6 spillere. Dette svarudsnit viser en uafgjort mellem indeks 1 og 4 i et spil med seks spillere; navnene kan være ens:

```json
{
  "currentPlayer": 5,
  "turnCompleted": true,
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 90,
  "gameCompleted": true,
  "winnerIndices": [1, 4]
}
```

De faste GUID'er og JSON-udsnittene er illustrative kontrakteksempler, ikke optagede HTTP-svar. Automatiske tests kontrollerer formatet og de beskrevne regler. Enkeltspiller giver altid winnerIndices [0] efter afslutning; ved lige 0 point vinder alle deltagere.

Gyldige kast, hold/slip, score og strike afvises derefter med InvalidOperationException og `Spillet er afsluttet. Start et nyt spil for at fortsætte.` HTTP's eksisterende fejlgrænse returnerer 409 med message-feltet. Ugyldigt ruteinput kontrolleres fortsat først og giver 400; uden spil bevares 404. Oplà bevarer identitets-/annulleringskontrollen og handlerens tomme svar ved afvisning. Hverken afvisning eller gentagen læsning ændrer point, resultat eller antal tilfældighedskald.

**Testbarhed:** Den offentlige parameterløse sessionskonstruktør bruger fortsat Random.Shared. En intern konstruktør med Random og intern adgang for testassemblyen gør faste kast mulige uden at udstille skrivbar spildata. Begge oprettelsesveje giver kilden videre til Game. Der er ingen nye afhængigheder, og kernens regler er uændrede; kun dens berørte kommentarer/TODO'er er opdateret.

**Automatisk verificeret:** Fokuserede sessions-, API-, identitets-, handler- og transporttests består med **761 tests**, 0 fejlede og 0 oversprungne. Release-build består med **0 fejl og 0 advarsler**, og hele backendens suite består med **1.137 tests**, 0 fejlede og 0 oversprungne. Det er 33 flere cases end efter trin 3: 27 nye deterministiske [sessionscases](../backend/Yatzi.Tests/GameLifecycleSessionTests.cs) og seks nye [HTTP-fuldspilscases](../backend/Yatzi.Tests/GameLifecycleApiTests.cs). TRX ligger lokalt i `TestResults/step4-focused.trx` og `TestResults/step4-all.trx` og indgår ikke i Git-leverancen.

Testene kontrollerer alle 11 JSON-felter ved oprettelse, GET, handlinger og første lobbystart, med eksplicit forventet spillerantal. De dækker 1, 2 og 6 spillere, score/strike, tavler som bliver fulde før de øvriges sidste ture, én vinder som ikke er først, uafgjort ved ens navne og alle på 0. Bonusprøven opbygger øvre sum 63 gennem gyldige ture: total 113 vinder over en modstanders 80 uden bonus. Den interne tilfældighedskilde tæller kald ved afvisninger, læsning, oprettelse og de kontrollerede slutkapløb.

Samtidige sidste feltvalg accepterer kun ét. Kast samtidig med sidste feltvalg kontrolleres både konkurrerende og med begge faste rækkefølger for 1, 2 og 6 spillere. Et kast efter accepteret afslutning afvises; et kast før feltvalget kan accepteres, og pointene kommer fra netop det kast. Kopitests ændrer et returneret vinderarray og pointdictionary og kontrollerer, at session, andre svar og senere enkeltspilleroprettelse bevares korrekt. Ugyldig oprettelse, annullering og lobbyens nulstillingsværn bevarer slutresultatet.

**Udestående integrationer:** HTTP-handlinger er stadig uden handlingskontekst og arbejder på den aktuelle tur. gameId og turnNumber udstilles, men sammenlignes endnu ikke med requests. Controllerudsendelser ved HTTP-handlinger og END ved afslutning/READY følger i trin 5. READY giver indtil da stadig STATE/WAIT ud fra det bevarede aktuelle indeks. Join-svarets joinedPlayerId er ikke tilføjet, omkamp venter til trin 6, og frontend bruger endnu sin eksisterende beregning af slutstatus og vindere indtil trin 7. Lobbyens started forbliver true; deltagerlisten genåbnes ikke.

**Slutkontrol af trin 4:** TRX bekræfter 761 henholdsvis 1.137 beståede tests, inklusive alle 33 nye cases, uden fejlede eller oversprungne. Kode, danske kommentarer, testændringer og diff er gennemgået. Alle 32 JSON-blokke og 108 lokale links inklusive ankre i arbejdsplan, API- og lobbydokumentation består, ligesom streng UTF-8, whitespace og git diff --check. Hashkontrol mod starten af trin 4 afgrænser ændringerne til 13 eksisterende filer og to nye testfiler; øvrige tidligere ændringer, inklusive lokal noCase, er bevaret. Game.cs og GameTests.cs har kun ændrede kommentarer og whitespace i dette trin. Der er ingen nye afhængigheder eller ruter. Trin 4 er markeret færdigt efter kontrollerne.

Der er ikke udført hardwaretest, Arduino-kompilering eller separat Kestrel-/tunnelprøve i trin 4. HTTP-/WebSocket-testene kører gennem app-pipelinen i hukommelsen. Frederiks brugeroplyste USB-kontrol gentages ikke, #43 venter, og lokal noCase bevares uden for fællesleverancen. Trin 5 påbegyndes ikke som del af trin 4; der udføres ingen staging, commit, push eller PR.

## Implementeret HTTP og controllerstatus i trin 5

**Historisk status ved afslutningen af trin 5.** Omkamp tilføjes i trin 6 nedenfor.

Dette afsnit beskriver den aktuelle backend efter trin 5. HTTP-ruterne er bevaret, men roll, hold, score og strike kræver nu JSON med gameId, turnNumber og playerId. Enkeltspiller kræver playerId null; lobby kræver den aktive deltagers id. Alle tre felter er obligatoriske, også når playerId er null. Der er ingen vej uden body. GET, `/api/game/new` med navn og første lobbystart uden body bevarer deres requests. De 11 spilfelter er uændrede.

**Kompatibilitet og udeståender:** Frontenden er bevidst ikke ændret i dette trin. Dens gamle browserknapper sender endnu ingen kontekst og får derfor 400 indtil trin 7. Oplà-input og korrekt formaterede API-klienter virker gennem den nye backend. Omkamp venter til trin 6; `/api/lobby/restart` er ikke oprettet. Rå Oplà-tekst har fortsat hverken gameId eller turnummer og vurderes mod tilstanden ved behandling.

### Ansvar og atomisk accept

- GameEndpoints læser JSON manuelt og kontrollerer rute, tilstedeværelse, typer, ikke-tomme GUID'er og positivt Int32-turnummer. Ukendte felter ignoreres. Manglende eller ugyldigt input giver dansk message med 400.
- GameActionContext er et typet input. GameSession kontrollerer den aktuelle spilrelation, afslutning, tur og aktive spiller under samme lås som kernekald og svarkopi. GameActionResult skelner mellem accept, manglende spil, null-playerId i lobby og konflikt; HTTP mapper status til 200/404/400/409 uden at sammenligne fejltekster.
- Fejlprioriteten følger den aftalte API-kontrakt. En gammel tur afvises også efter næste spillers kast og når oprindelig spiller igen har tur. Et gammelt enkeltspillerspils GUID kan ikke påvirke en ny oprettelse. Afvisning bruger ingen tilfældighed og ændrer intet.
- De tidligere kontekstfri sessionsmetoder er interne til rene sessionstests. Produktionens HTTP bruger lobbyens koordinering og den kontekstkrævende sessionsindgang. Oplà bevarer sine identitetskontrollerede metoder og samme private kernekald.
- Cancellation kontrolleres før ændring, også efter ventetid på sessionslåsen. Efter accept fortrydes eller gentages handlingen ikke. Der er ingen nye låse, eksterne callbacks eller netværkskald i GameSession.

### Modtagere og beskedrækkefølge

LobbySession koordinerer nu alle fire HTTP-handlinger med start, Oplà-input, READY og overtagelse. Låserækkefølgen er fortsat lobby → korte registeropslag/spilsession; registerlåsen holdes ikke ind i spilsessionen. Den kopierede accepterede tilstand bruges direkte til HTTP-svar og beskeder. Et senere GET kan ikke ændre et allerede reserveret batch.

| Hændelse | Implementeret besked |
|---|---|
| HTTP-kast eller hold/slip | Præcis én STATE til aktiv spillers aktuelle Oplà. |
| Normalt score/strike | STATE 0 00000 til næste spiller og WAIT med dennes navn til øvrige aktuelle deltagere. |
| Sidste feltvalg | END til alle aktuelle deltagere, uden en ekstra STATE først. |
| READY efter tilmelding/start | PAIR, JOINED og derpå STATE, WAIT eller END ud fra den aktuelle svarkopi. |
| Afvisning eller GET | Ingen controllerbesked. |

Køreservation sker under lobbylåsen; alle afsendelsesforsøg startes og afventes uden tilstandslås. En blokeret modtager forhindrer ikke de øvrige. Kun aktuelle åbne bindinger vælges. En frakoblet deltager bevarer sin tur og kan bruge korrekt HTTP-reserve; der er ikke indført spring over eller automatisk kast. Selvstændigt spil før lobbystart sender ikke til de ventende lobbyspillere.

Sendesvigt, lukket forbindelse og transportannullering fortryder ingen accepteret handling. Et fortsat aktivt HTTP-request får stadig 200. Oprydning lukker alene den indfangede gamle forbindelse. Request-annullering efter accept kan afbryde leveringen og videresendes som cancellation, men hverken genafspiller handlingen eller forgifter køen. Senere batches venter stadig på tidligere batches. READY genskaber status uden ekstra kast. Oplà-input køer fortsat kun sit ene handler-STATE og får ingen dobbeltsvar fra HTTP-vejen.

### Join og verificeret dækning

POST /api/lobby/join returnerer nu LobbyJoinResponse med players, started og joinedPlayerId. Det accepterede id fastholdes før JOINED-afsendelse og returneres sammen med det eksisterende friske lobbyøjebliksbillede. En anden spiller med samme navn eller en accepteret start under ventetiden ændrer ikke requestens id. GET /api/lobby har fortsat kun players og started. Frontendens brug af joinedPlayerId følger i trin 7.

**Automatisk verificeret:** Fokuserede sessions-, HTTP-, lobby-, identitets-, handler- og WebSocket-tests består med **940 tests**, 0 fejlede og 0 oversprungne. Release-build består med **0 fejl og 0 advarsler**, og hele backendens testsuite består med **1.316 tests**, 0 fejlede og 0 oversprungne. Det er **179 nye cases** siden trin 4: 154 HTTP-kontekstcases, fire deterministiske sessionscases og 21 controller-/leveringscases. Resultaterne findes lokalt i `TestResults/step5-focused.trx` og `TestResults/step5-all.trx` og indgår ikke i Git-leverancen.

[HTTP-konteksttestene](../backend/Yatzi.Tests/GameActionApiTests.cs) kontrollerer alle fire handlinger, body-/felttyper, ugyldige strenge, fejlprioritet, spillerrelation, nye spil og gamle ture. [Sessionstestene](../backend/Yatzi.Tests/GameActionSessionTests.cs) tæller tilfældighedskald ved accept, afvisning og cancellation. [Controller- og leveringstestene](../backend/Yatzi.Tests/LobbyHttpActionApiTests.cs) gennemfører alle 15 felter med 1, 2 og 6 spillere, præcise STATE/WAIT/END, READY efter afslutning, browserreserve efter afbrydelse, blokerede køer, transportfejl, request-cancellation og sen fejl efter overtagelse. En forsinket join-request bevarer sit eget id efter et andet join med samme navn og en accepteret start.

Eksisterende HTTP-tests er migreret med [en fælles requesthjælper](../backend/Yatzi.Tests/GameHttpActions.cs), der kun bruger det angivne snapshot og spiller-id og aldrig selv læser eller fornyer konteksten. Konkurrerende requests fastholder kontekst før frigivelse. Et HTTP-kast efter accepteret feltvalg afvises nu som gammel tur; point-/snapshotkontroller er bevaret. Rå Oplà-input beholder de mulige rækkefølger uden en turnøgle. De eksisterende 12 miljø-/spillerantal-kombinationer består i Development, Production, Staging og Test, og gamle tests kontrollerer nu de ekstra accepterede HTTP-STATE-beskeder frem for tavshed.

**Slutkontrol af trin 5:** TRX bekræfter 940 fokuserede og 1.316 samlede beståede tests, inklusive alle 179 nye cases, uden fejlede eller oversprungne. Kode, kommentarer, testændringer og diff er gennemgået. Streng UTF-8, whitespace, git diff --check, alle 32 JSON-blokke og 160 lokale links inklusive ankre i de fem berørte dokumenter består. Hashkontrol mod starten af trin 5 afgrænser ændringerne til 23 eksisterende filer og fire nye test-/hjælperfiler. Alle øvrige tidligere ændringer, herunder kernen, kernetests, frontend og lokal noCase, er bevaret. Trin 5 er markeret færdigt efter kontrollerne; trin 6–8 er ikke påbegyndt.

Der er ikke udført fysisk hardwarekontrol, Arduino-kompilering eller en separat Kestrel-/tunnelprøve. App-pipeline og WebSockets er testet i hukommelsen. Frederiks brugeroplyste USB-kontrol gentages ikke, #43 venter, og lokal noCase bevares. Ingen nye afhængigheder eller ændringer i Arduino-protokollen er tilføjet. Der udføres ingen staging, commit, push eller PR, og trin 6 påbegyndes ikke som del af leverancen.

## Implementeret omkamp i trin 6

**Historisk status ved afslutningen af trin 6.** Frontendtilpasningen beskrives i trin 7 nedenfor.

POST /api/lobby/restart kræver et JSON-objekt med gameId og playerId som ikke-tomme GUID-strenge. Et null-playerId afvises, turnNumber kræves ikke, og ukendte felter ignoreres. Manuel parsing bevarer dansk message-JSON, også ved ugyldige Unicode-escapes. LobbyRestartContext og det typede LobbyRestartResult/LobbyRestartStatus adskiller formatkontrol, regelresultat og HTTP-status uden fortolkning af fejltekst. Den fulde [request- og fejlkontrakt](lobby.md#omkamp) er implementeret uændret.

### Atomisk nulstilling og bevaret lobby

LobbySession.RestartAsync bruger samme lobbylås som READY, første start, HTTP-handlinger og Oplà-input. Den kontrollerer aktuelle åbne forbindelser med korrekt spillerbinding og fastholder de konkrete modtagere. Under GameSession-låsen kontrollerer RestartLobbyGame manglende spil → gameId → lobbyspil → medlemskab → afslutning → lobbyens forbindelsesforudsætning. Der kaldes hverken tilbage til lobbyen eller netværket under sessionslåsen.

Ved accept erstattes alle deltagernes Games samlet med friske instanser. Id’er, navne og rækkefølge kommer fra sessionens eksisterende deltagere, og samme tilfældighedskilde bevares uden et kast. GameId er nyt, turnNumber=1, currentPlayer=0, alle felter er null, og summer/bonus/total er 0. Terninger, hold og slag er nulstillet, possibleScores og winnerIndices er tomme, og begge afslutningsfelter er false. Gamle svar deler ingen skrivbar lagring med omkampen.

Lobbyens started forbliver true, og parringer og koder røres ikke. Første start, nye joins og /api/game/new kan fortsat ikke overskrive lobbyspillet. Afvisning eller annullering før accept bevarer hele slutresultatet og bruger ingen terningetilfældighed. Cancellation kontrolleres før låsene, efter ventetid og umiddelbart før udskiftningen. Et nyt gameId afviser konkurrerende omkampe og gamle spilhandlinger, også hvis spiller og turnummer matcher igen.

### Beskedrækkefølge, udfald og tests

Den accepterede svarkopi bygger STATE 0 00000 til første spiller og WAIT med dennes navn til resten. Batches reserveres før lobbylåsen slippes; netværket afventes derefter med alle modtagerforsøg startet. Tidligere END står foran omkampens status på samme forbindelse. READY og overtagelse genbruger den eksisterende kode og giver PAIR, JOINED og omkampens aktuelle STATE/WAIT.

Forbindelseskravet afgøres ved lobbyens kontrol. Udfald derefter fortryder ingen omkamp. Den eksisterende leveringshåndtering bevarer HTTP 200 ved kendte transportfejl, når requesten stadig er aktiv. Request-annullering efter accept kan afbryde leveringen, men hverken genudfører spiloprettelsen, forgifter køen eller lukker en nyere overtager. READY genskaber status.

**Automatisk verificeret:** De fokuserede sessions-, HTTP-, lobby-, identitets-, handler- og WebSocket-tests består med **1.003 tests**, ingen fejlede eller oversprungne. Release-build består med **0 fejl og 0 advarsler**. Hele backendens testsuite består med **1.379 tests**, ingen fejlede eller oversprungne. Trin 6 tilføjer **63 cases**: fire sessionscases, 49 HTTP-/miljøcases og ti leverings-/samtidighedscases. Lokale resultater findes i TestResults/step6-focused.trx og TestResults/step6-all.trx og indgår ikke i Git-leverancen.

[Sessionstestene](../backend/Yatzi.Tests/GameRestartSessionTests.cs) opbygger bonus og vindere gennem gyldige ture og kontrollerer fuld nulstilling, uændret tilfældighedskilde, svarkopier, afvisninger og gentagne spil. [API-testene](../backend/Yatzi.Tests/LobbyRestartApiTests.cs) dækker format/fejlprioritet, forbindelser, gammel kontekst og to hele spil efterfulgt af to omkampe i alle 12 miljø-/spillerantal-kombinationer. En forkert intern transportbinding injiceres kun i testen, fordi det offentlige API ikke kan ombinde en parret forbindelse. [Leveringstestene](../backend/Yatzi.Tests/LobbyRestartDeliveryTests.cs) dækker konkurrerende requests, blokeret END, senere Oplà-input, overtagelse, transportfejl og cancellation før/efter accept uden faste ventepauser.

**Slutkontrol af trin 6:** TRX bekræfter 1.003 fokuserede og 1.379 samlede beståede tests med 63 nye cases og ingen fejlede eller oversprungne. Kode, danske kommentarer, fejlprioritet, låserækkefølge og diff er gennemgået. Streng UTF-8, whitespace, git diff --check, alle 32 JSON-eksempler og 169 lokale links inklusive ankre i de fem livscyklus-/integrationsdokumenter består. Hashkontrol mod starten af trin 6 afgrænser ændringen til syv eksisterende filer og tre nye testfiler. De øvrige 92 baselinefiler, herunder alle tidligere testændringer, kernen, frontend og lokal noCase, er bevaret. Trin 6 er markeret færdigt efter kontrollerne.

**Afgrænsning:** Frontendtilpasning venter til trin 7; de eksisterende browserknapper uden handlingsbody får fortsat 400. Trin 7–8 er ikke påbegyndt. Yatzi.Core, frontend, afhængigheder og Arduino-protokol ændres ikke i trin 6. Lokal noCase bevares. Ingen fysisk hardwarekontrol, Arduino-kompilering eller særskilt Kestrel-/tunnelprøve er udført; HTTP og WebSockets er testet i app-pipelinen i hukommelsen. Frederiks brugeroplyste kontrol gentages ikke, #43 venter, og ingen staging, commit, push eller PR er udført.

## Implementeret frontend i trin 7

**Historisk status ved afslutningen af trin 7:** Implementeringen og de automatiske kontroller bestod, mens browseradapteren blokerede den rigtige browserkontrol. Trin 8 nedenfor gennemfører kontrollen via den aftalte reservevej og afslutter det sidste åbne punkt i trin 7. De oprindelige resultater og fejloplysninger bevares som historik.

### Requests, identitet og visning

[app.js](../frontend/app.js) sender nu JSON med gameId, turnNumber og eget playerId på roll, hold, score og strike. Enkeltspiller sender null-playerId. Feltknapper fastholder konteksten fra deres viste tur, og 0-point-bekræftelsen bruger strike. Busy beskytter mod dobbeltklik og deaktiverer knapper, terninger og feltvalg under handlingen. Ingen POST genafspilles automatisk.

Join gemmer joinedPlayerId direkte i sessionStorage, også ved ens navne og start før join-svaret. Lobbyens id’er bestemmer eget indeks; en startet lobby tilsidesætter enkeltspillerflaget. Manglende medlemskab giver tilskuervisning, og ugyldige gemte id’er ryddes. Frakoblet Oplà blokerer ikke den aktive deltagers browserreserve.

Slutskærmen bruger gameCompleted og winnerIndices fra serveren. Ranglisten sorterer en kopi med de oprindelige indeks bevaret og bruger serverens vinderindeks til fremhævning. Den eksisterende slutknap viser Omkamp for lobbydeltagere, fastholder det afsluttede gameId og eget id og viser den nye starttilstand ved accept. Tilskuere har ingen omkampsknap. Frakoblede deltagere deaktiverer knappen med forklaring i den eksisterende statuslinje. Enkeltspillerens Nyt spil bevarer forløbet gennem lobbyen og /api/game/new.

### Samordnede læsninger og animation

Opstart, polling og genindlæsning efter 409/404 bruger samme refresh-forløb. Lobbyen læses også under spil og afslutning, så medlemskab og forbindelser holdes aktuelle. Samtidige læsninger i samme handlingsgeneration deler ét forløb. Ved klik annulleres den gamle læsning; en generationskontrol beskytter også mod svar, der når frem trods abort. Lobby, identitet og spil anvendes først, når hele læsningen er afsluttet og stadig er aktuel. Lavere turnNumber i samme gameId ignoreres; GUID’er ordnes aldrig efter størrelse.

Netværksfejl bevarer senest kendte visning. Ved manglende spil efter servergenstart genlæses lobbyen, og ugyldig lokal tilstand ryddes. Handlingsfejl og almindelige statustekster holdes adskilt, så polling ikke skjuler en afvist omkamps forklaring.

Animation startes kun ved stigende slagnummer i samme spil og tur. Ny tilstand stopper tidligere timer og animationsklasser; en allerede planlagt callback kontrollerer også sit konkrete snapshot. Nyt spil, næste tur, afslutning, hold og lobby kan derfor ikke overskrives af en gammel animation.

### Faktiske kontroller

- **JavaScript:** node --check frontend/app.js består. Alle **39 Node-tests** består, ingen fejlede, annullerede eller oversprungne. Kør dem med `node --test tests/frontend/app.test.cjs`. [Testene](../tests/frontend/app.test.cjs) bruger en lille DOM-/fetch-/timerfixture uden nye pakker og kontrollerer afsendte requests, synlig tilstand, adgang, genstart, fejl, forsinkede svar og animation. TAP ligger lokalt i TestResults/step7-frontend.tap.
- **Backendregression:** Release-build består med **0 fejl og 0 advarsler**. Alle **1.379 backendtests** består, ingen fejlede eller oversprungne. Resultaterne står i TestResults/step7-all.trx; backendprogramkode og tests er uændrede i dette trin.
- **Lokal integration med DOM-fixture:** En særskilt Production-server på localhost:5177 serverede præcis den ændrede app.js. Det rigtige script kørte i DOM-fixturen med rigtige HTTP-kald og to software-Oplà-klienter gennem /ws/opla. Kontrollen bestod enkeltspillerens kast/hold/chance/næste tur/slut/nye spil; to Alex-spilleres id’er; aktiv/ventende/tilskuer; HTTP og Oplà-kast; bekræftet strygning; vinder; frakobling/genforbindelse og omkamp fra anden deltager med bevarede parringer. Den konkrete enkeltspillerprøve gav 26 point. Lokalt resultat: TestResults/step7-live.json. Dette er ikke en rigtig browserkontrol.
- **Rigtig browser:** Chrome blev fundet, og brugeren åbnede testadressen efter forespørgsel. Adapteren fejlede fortsat ved navigation, DOM-/tekstlæsning og skærmbillede, også efter nulstilling. Den konkrete navigations-/observationsfejl var `Cannot read properties of undefined (reading 'timeoutMs')`; selector-læsning nåede sin deadline. Ingen UI-kontrol eller skærmbillede er derfor registreret som bestået.

**Diff- og dokumentationskontrol:** Streng UTF-8, whitespace, git diff --check, alle 32 JSON-eksempler og 172 lokale links inklusive ankre i de fem livscyklus-/integrationsdokumenter består. Hashkontrol afgrænser trin 7 til fire eksisterende filer og to nye testfiler; de øvrige 98 baselinefiler er uændrede. Tidligere backend-/testændringer, HTML/CSS, browserbro og lokal noCase er bevaret. De midlertidige lokale testprocesser er stoppet efter kontrollen. Trin 7 afventer alene den beskrevne browserkontrol og markeres derfor ikke helt færdigt.

### Udestående målrettet browserkontrol

**Historisk udestående punkt – gennemført i trin 8.** Følgende forløb var browserkontrollens acceptgrundlag og er nu kontrolleret i rigtig Chrome mod en lokal backend med software-Oplà-klienter:

1. Enkeltspiller: opret, kast, hold og vælg felt. Næste tur skal vise fem uslåede terninger og Slag 0/3, mens pointet bevares. Slutskærmen skal vise backendens resultat, og Nyt spil skal give adgang til en frisk enkeltspilleroprettelse.
2. To deltagere og en tilskuer i hver sin fane: join med ens navne, start, skift tur og bekræft et 0-felt. Kun den aktive deltager må handle; egen identitet og markeringen (dig) skal følge id’et. Tilskueren skal kunne følge spillet uden spilhandlinger.
3. Afslut gennem API’et og kontrollér vindertekst og rangliste. Afbryd én software-Oplà: Omkamp skal blive deaktiveret med forklaring. Genforbind med samme kode, start omkamp fra anden deltager og kontrollér friske tavler, første spiller, ingen automatisk kast og fortsat tilskuervisning.
4. Genindlæs en deltagerfane, og kontrollér bevaret identitet samt synlig fejl ved et afvist forsøg. Kontrollér også browserkonsol og at gammel animation ikke hænger ved.

Den gennemførte kontrol i trin 8 gør nu trin 7 helt færdigt. Ingen fysisk USB-/Arduino-kontrol gentages; #43 venter. Layout, HTML, CSS, browserbro, backend, pakker og Arduino-protokol er bevaret. Lokal noCase er uændret. Ingen staging, commit, push eller PR er udført.

## Slutkontrol af #44 i trin 8

**Lokalt resultat 10. november 2026:** Den aftalte softwarelivscyklus er gennemgået fra Game og Scorecard til GameSession, LobbySession, HTTP/Oplà og frontend. Den samlede diff, inklusive nye testfiler fra tidligere trin, er gennemgået. Der blev ikke fundet funktionelle fejl, som krævede ændringer i programkode eller nye regressionstests i trin 8. Pointregler, HTTP-kontrakter, controllerens wireformat og tidligere testassertions er bevaret. Trin 8 ændrer dokumentation og tilføjer frontendtestjobbet i CI.

### Automatiske kontroller og CI

- **Frontend:** JavaScript-syntakskontrol og alle **39 Node-tests** består med 0 fejlede, annullerede eller oversprungne. Den præcise frontendkommando fra CI er kørt i Git Bash og skriver TestResults/frontend.tap. Testene installerer ingen npm-pakker.
- **Backend:** Release-build består med **0 fejl og 0 advarsler**. Alle **1.379 tests** består, ingen fejlede eller oversprungne. TestResults/step8-all.trx dokumenterer den nye samlede kørsel. De 12 eksisterende miljø-/spillerantal-forløb dækker to hele spil og to omkampe med 1, 2 og 6 deltagere i Development, Production, Staging og Test.
- **CI:** Et separat frontendjob i den eksisterende [workflowfil](../.github/workflows/ci.yml) bruger actions/setup-node@v7 og Node 22, kontrollerer syntaks og kører Node-testene uden npm-installation. TAP uploades med always(), og pipefail bevarer testens fejlkode gennem tee. En isoleret fejlprobe med exitkode 7 returnerede 7 som forventet. Backendjob, Arduinojob og workflowtriggere er bevaret uændret. Den lille workflowdiff er gennemgået, og dens frontendkommando er afviklet lokalt; GitHub-CI er ikke udløst i denne leverance. Lokalt kontrolresultat: TestResults/step8-ci.json.

Kommandoerne er `node --check frontend/app.js`, `node --test tests/frontend/app.test.cjs`, `dotnet build H3Yatzi.sln --configuration Release --no-restore` og `dotnet test H3Yatzi.sln --configuration Release --no-build --no-restore`.

### Rigtig browser og separat Production-prøve

Den normale browseradapter blev forsøgt først og gav samme timeoutMs-fejl som i trin 7. Den udtrykkeligt aftalte reservevej gennemførte derefter kontrollen i den installerede Chrome med headless-tilstand, separat midlertidig profil og DevTools-adgang på localhost. Node brugte sin indbyggede WebSocket-klient; ingen browserpakker blev installeret, og brugerens normale Chrome-profil blev ikke brugt.

Rigtig Chrome bestod enkeltspillerens oprettelse, kast, hold/slip, pointvalg, bekræftet 0-strygning, næste tur, afslutning og nyt spil. To Alex-deltagere og en tilskuer i separate faner bestod id-/dig-markering, aktiv/ventende spiller, HTTP og software-Oplà-input, rotation tilbage til første spiller, vinder/rangliste og senere uafgjort med begge vinderindeks. Den afsluttende browserprøve gav 23 point i enkeltspiller; lobbyens første spil gav spiller 0 sejr med 14 point, og omkampen endte uafgjort på 0. Skærmbillederne blev optaget efter de synlige CSS-animationer var afsluttet.

Frakobling deaktiverede Omkamp med forklaring, og READY med samme kode genoprettede adgangen. Anden deltager startede omkampen. Et tilbageholdt poll holdt første deltagers gamle slutskærm synlig, så dens omkampsklik gav den forventede 409. Visningen hentede omkampen og bevarede den danske fejltekst gennem efterfølgende vellykket polling. Genindlæsning genskabte eget id. Browseren havde **0 registrerede JavaScript-undtagelser og 0 console.error-beskeder**. Ved 390 px var der ingen vandret side-overløb, og alle fem terninger lå inden for visningen.

Skærmbilleder af næste tur, vinder, uafgjort, omkamp og 390 px visning er gemt under TestResults/step8-browser-*.png og visuelt gennemgået. Det maskinlæsbare resultat er TestResults/step8-browser.json. Browserkontrollen lukker det sidste åbne punkt i trin 7; DOM-fixturens tidligere resultater bevares særskilt som historik.

Separate Production-processer med rigtige HTTP-/WebSocket-klienter bestod følgende, uden DOM-fixture:

| Deltagere | Hele spil og omkampe | Afsluttende turnummer | Resultat |
|---:|---|---:|---|
| 1 | To hele spil og to omkampe | 15 | Én vinder, indeks 0. |
| 2 | To hele spil og to omkampe | 30 | Begge Alex-spillere lige på 0; indeks 0 og 1. |
| 6 | To hele spil og to omkampe | 90 | Sidste deltager vinder, indeks 5, i begge spil. |

Prøverne kontrollerede præcise STATE/WAIT/END, rå ROLL/HOLD, 11 svarfelter, point fra det accepterede kast, manglende forbindelses afviste omkamp, genforbindelse med samme kode, to konkurrerende genstarter med præcis én accept samt afvisning af gamle gameId’er på alle fire HTTP-handlinger. Spiller-id’er, navne, rækkefølge og parringer blev bevaret. Resultater og logs ligger i TestResults/step8-production.json og step8-production-*.log.

### Arduino-kompatibilitet og kontrolgrænser

En midlertidig kopi fra fællessketchen med **carrier.withCase()** er kompileret til **arduino:samd:mkrwifi1010** med det eksisterende Arduino CLI 1.5.1, SAMD 1.8.14, Arduino_MKRIoTCarrier 2.1.0 og WiFiNINA 2.1.1. Kompileringen består og bruger **98.492 bytes programhukommelse (37 %) og 7.092 bytes globale variable (21 %)**. Der er hverken installeret/opgraderet biblioteker eller uploadet til hardware. Kompileringsloggen er TestResults/step8-arduino.log.

Den lokale original med noCase er byte-for-byte bevaret med SHA-256 `25db97529a590a1b3638e23e63a32f66d93d71cac5d2dde83dbf5d559e98b58f`. Den personlige tilpasning holdes fortsat uden for en senere fællesleverance. Software-Oplà-klienter, Chrome, DOM-fixture, automatiske tests og kompilering er særskilte kontrolformer. Frederiks USB → browserbro → backend → skærm er brugeroplyst fysisk verifikation; den er ikke gentaget her. Fysisk hardware- og tunneltest i #43 venter fortsat.

### Krav/test-matrix

| Krav | Implementering | Automatisk test | Lokal/browserkontrol | Resultat |
|---|---|---|---|---|
| Næste spiller efter valgt felt, 1–6 deltagere | GameSession roterer efter score/strike; Game.StartNextTurn nulstiller turdata | [GameLobbySessionTests](../backend/Yatzi.Tests/GameLobbySessionTests.cs), [GameTests](../backend/Yatzi.Tests/GameTests.cs) | Production 1/2/6; Chrome enkeltspiller og rotation tilbage til første | Bestået |
| STATE til aktiv, WAIT til øvrige og END ved afslutning | LobbySession køer fra den accepterede svarkopi; rå Oplà-kast/hold giver ét svar | [LobbyHttpActionApiTests](../backend/Yatzi.Tests/LobbyHttpActionApiTests.cs), [OplaWebSocketGameIntegrationTests](../backend/Yatzi.Tests/OplaWebSocketGameIntegrationTests.cs) | Præcise linjer i Production; HTTP og software-Oplà i Chrome | Bestået |
| Slut efter alle 15 felter pr. deltager, også gemte nuller | Scorecard.IsComplete og samlet GameCompleted; sidste kast og indeks bevares | [ScorecardTests](../backend/Yatzi.Tests/ScorecardTests.cs), [GameLifecycleSessionTests](../backend/Yatzi.Tests/GameLifecycleSessionTests.cs) | Slutturnummer 15/30/90 i Production | Bestået |
| Vinder inklusive bonus og uafgjort | Total inklusive bonus og vinderindeks i deltagerrækkefølge | [GameLifecycleSessionTests](../backend/Yatzi.Tests/GameLifecycleSessionTests.cs), [GameLifecycleApiTests](../backend/Yatzi.Tests/GameLifecycleApiTests.cs) | Chrome én vinder og ens navne ved uafgjort; Production indeks 5 vinder | Bestået |
| Nyt spil efter afslutning med samme lobby og parringer | RestartLobbyGame, ny GameId, tur 1 og tomme tavler; enkeltspiller bruger /new | [GameRestartSessionTests](../backend/Yatzi.Tests/GameRestartSessionTests.cs), [LobbyRestartApiTests](../backend/Yatzi.Tests/LobbyRestartApiTests.cs) | To hele spil/to omkampe for hvert spillerantal; Chrome omkamp fra anden deltager | Bestået |
| Handlingskontekst, input og aftalt fejlprioritet | Manuel JSON-læsning og typede resultatstatus; kontrol/ændring under én sessionslås | [GameActionApiTests](../backend/Yatzi.Tests/GameActionApiTests.cs), [GameActionSessionTests](../backend/Yatzi.Tests/GameActionSessionTests.cs) | Production afviser alle fire gamle handlingstyper | Bestået |
| Entydigt join og adgang ved ens navne | joinedPlayerId, sessionStorage og aktuelle bindinger | [LobbyHttpActionApiTests](../backend/Yatzi.Tests/LobbyHttpActionApiTests.cs), [frontendtests](../tests/frontend/app.test.cjs) | To Alex-faner, (dig), genindlæsning og tilskuer i Chrome | Bestået |
| Afbrydelse bevarer tur/point; reserve og READY genskaber status | Ingen spillere springes over; browserreserve kræver ikke egen Oplà | [LobbyHttpActionApiTests](../backend/Yatzi.Tests/LobbyHttpActionApiTests.cs), [LobbyReconnectSessionTests](../backend/Yatzi.Tests/LobbyReconnectSessionTests.cs) | Production/Chrome frakobling, afvist omkamp og samme kode ved genforbindelse | Bestået |
| Samtidige valg og omkampe, afvisninger og svarkopier | Sessionens lås, GameId/TurnNumber og kopierede arrays/dictionaries | [GameLifecycleSessionTests](../backend/Yatzi.Tests/GameLifecycleSessionTests.cs), [LobbyRestartDeliveryTests](../backend/Yatzi.Tests/LobbyRestartDeliveryTests.cs) | Production to konkurrerende omkampe med én accept | Bestået |
| Sendesvigt og cancellation gentager ingen accept og rammer ikke nyere overtager | Konkrete forbindelses-id’er, ordnede batches og netværk uden tilstandslås | [LobbyRestartDeliveryTests](../backend/Yatzi.Tests/LobbyRestartDeliveryTests.cs), [LobbyHttpActionApiTests](../backend/Yatzi.Tests/LobbyHttpActionApiTests.cs) | Kontrollerede tasks i tests; genforbindelse kontrolleret mod rigtig server | Bestået |
| Frontend følger serverens resultat og beskytter mod gammel visning | Serverens gameCompleted/winnerIndices, generationer, busy og snapshotbundet animation | [39 frontendtests](../tests/frontend/app.test.cjs) | Chrome tilbageholdt poll, gammel omkamp/409, vedvarende fejl, animation og 390 px | Bestået |
| Protokol og controllerkompatibilitet | Uændrede tekstlinjer; rå input valideres ved behandling uden spil-/tur-id | [OplaMessageHandlerTests](../backend/Yatzi.Tests/OplaMessageHandlerTests.cs), [OplaWebSocketEndpointTests](../backend/Yatzi.Tests/OplaWebSocketEndpointTests.cs) | Production softwareklienter og kompilering af fællessketchen | Bestået |

**Endelig diff- og dokumentationskontrol:** Alle 36 JSON-eksempler og 234 lokale links inklusive ankre i README og de syv berørte dokumenter består. Streng UTF-8, whitespace og git diff --check består. Hashkontrol mod starten af trin 8 viser præcis ni ændrede eksisterende filer: CI-workflowet og de otte dokumenter. De øvrige 95 baselinefiler, inklusive al programkode, alle tests fra tidligere trin og lokal noCase, er byte-for-byte bevaret. Der er ingen nye kildefiler, nye npm-/NuGet-pakker eller staged ændringer i trin 8. Krav/test-matrixen og den gennemgåede diff giver grundlag for at markere de otte lokale trin færdige. Midlertidige profiler, hjælpescripts, resultater og skærmbilleder ligger alene under den ignorerede TestResults-mappe. Testprocesserne er stoppet.

#44 bidrager til projektets gameplay loop og livscyklus. Den lokale softwarekontrol lukker ikke andre DoD-issues, fysisk integration i #43 eller aflevering på GitHub. Der udføres ingen staging, commit, push, PR, merge eller manuel issue-lukning i trin 8. Den senere fællesleverance kræver fortsat PR med Closes #44, merge og grøn CI.

## Kontrol før PR efter integration af main

**Ny kontrol 10. november 2026:** Funktionscommit `66a86c1` samler de 49 delte kode-, test-, dokumentations- og CI-filer fra trin 1–8. Merge-commit `59b633b` integrerer `origin/main` på `db3abab`, herunder WiFi med USB som fallback fra [PR #86](https://github.com/Bmantaz/H3-Yatzi/pull/86). Integrationen krævede ingen rettelser i spillets programkode. Frontendjobbet og ArduinoHttpClient er begge bevaret i CI. Tidligere kontrolresultater ovenfor er historik fra før integrationen.

- **Automatisk:** Ny JavaScript-syntakskontrol og alle 39 frontendtests består. Nyt Release-build har 0 fejl og 0 advarsler; alle 1.379 backendtests består uden fejlede eller oversprungne tests. De 12 miljø-/spillerantal-forløb består fortsat. Resultater er gemt lokalt som TestResults/pr44-frontend.tap og pr44-all.trx.
- **Rigtig lokal server:** Production-prøver med rigtige HTTP-/WebSocket-klienter og 1, 2 og 6 deltagere består igen to hele spil og to omkampe pr. prøve. Slutturnumrene er 15/30/90; vinderindeks er henholdsvis [0], [0,1] og [5]. Gamle handlinger afvises, samtidige omkampe giver én accept, og parringer bevares. Resultat: TestResults/pr44-production.json.
- **Rigtig Chrome:** Den godkendte headless/CDP-reservevej med isoleret profil består igen enkeltspiller, ens navne og eget id, aktiv/ventende/tilskuer, rotation, slutresultat, uafgjort, frakobling/genforbindelse, omkamp fra anden deltager, gammel polling/409, vedvarende dansk fejl, genindlæsning og 390 px visning. Der er 0 JavaScript-undtagelser og 0 console.error. Den konkrete prøve gav 20 point i enkeltspiller og 22 point til vinderindeks 0 i første lobbyspil; næste spil endte [0,1] på 0. Skærmbillederne er visuelt gennemgået. Resultat og billeder: TestResults/pr44-browser.json og pr44-browser-*.png.
- **Arduino-kompilering:** En midlertidig kopi af den integrerede fællessketch med withCase og uden arduino_secrets.h består til arduino:samd:mkrwifi1010. Det eksisterende Arduino CLI 1.5.1, SAMD 1.8.14, Arduino_MKRIoTCarrier 2.1.0, WiFiNINA 2.1.1 og ArduinoHttpClient 0.6.2 bruges. Forbruget er 106.640 bytes programhukommelse (40 %) og 7.988 bytes globale variable (24 %). Ingen biblioteker er installeret/opgraderet, og intet er uploadet. Log: TestResults/pr44-arduino.log.
- **Personlig tilpasning:** Kun noCase-hunken blev gemt midlertidigt og gendannet efter merge. Den er stadig uden for commits, og den delte sketch bruger withCase. Filhashen er ændret som forventet af WiFi-integrationen; den personlige ændring er fortsat kun caseindstilling og kommentar.

**Ny diff- og dokumentationskontrol:** Alle 36 JSON-eksempler og 240 lokale links inklusive ankre består. De 49 delte filer er gennemgået mod origin/main; Arduino-kode, hemmeligheder, testprofiler og TestResults indgår ikke i PR-diffen. Streng UTF-8, whitespace og git diff --check består. Den lokale noCase-hunk er identisk med før integrationen; filens nye SHA-256 er 59e20646331fa6bf592fa01bf9bc5eebc9054a9ad4aa4d294db8628cc23c9b94. Kontrolresultat: TestResults/pr44-audit.json.

Krav/test-matrixen ovenfor gælder fortsat og er underbygget af disse nye kørsler. Testprocesserne er stoppet. DOM-fixture, rigtig browser, software-Oplà-klienter og kompilering er forskellige kontrolformer; de dokumenterer ingen ny fysisk WiFi-, USB- eller tunneltest. Frederiks brugeroplyste fysiske kontrol gentages ikke, og #43 venter. GitHub-PR'ens aktuelle CI-resultater dokumenteres i selve PR'en; lokale resultater erstatter ikke GitHub-CI.

## Aftalt specifikation for #44

**Samlet mål for #44 – implementeret og lokalt kontrolleret.** Aftalerne fra trin 1 er implementeret for rotation, spilfelter, slutstatus, vindere, HTTP-kontekst, joinedPlayerId, controllerbeskeder, omkamp og frontendtilslutning. Rigtig browser, lokal server, automatiske tests og Arduino-kompilering er kontrolleret i trin 8. GitHub-aflevering gennem PR/merge og grøn CI følger senere. De følgende eksempler er fortsat det aftalte acceptgrundlag; konkrete kontrolresultater står i implementeringsafsnittene ovenfor.

### Beslutninger og spiltilstand

Lobbyflowet er `lobby → aktivt spil → afsluttet spil → omkamp`. Enkeltspiller starter direkte med `POST /api/game/new`. Der er ét fælles spil i serverens hukommelse; genstart af serveren fjerner spil, lobby og parringer som hidtil.

- Lobbyen har 1–6 deltagere med stabile spiller-id'er i tilmeldingsrækkefølge. Efter første start er listen låst, også efter afslutning. `started` forbliver true under spil, slutskærm og omkamp.
- Omkamp bevarer deltagere, id'er, navne, rækkefølge og parringskoder. Enhver deltager kan starte den, når spillet er afsluttet og alle deltagernes Oplà-forbindelser er aktuelle og forbundne. Første deltager begynder igen. Ingen genstart af server, ny parring eller åbning af lobbyen er nødvendig.
- Ved afbrydelse bevares spiller, tur, point og terninger. Spilleren springes ikke over, og der stryges intet automatisk. En aktiv spiller kan bruge browserens reserveknapper, selv om egen Oplà er frakoblet. Forbindelseskravet gælder start og omkamp, ikke disse reservehandlinger.
- De eksisterende syv topfelter i spil-JSON bevares: `players`, `currentPlayer`, `dice`, `held`, `rollsUsed`, `possibleScores` og `turnCompleted`. Pointregler og felt-id'er følger fortsat [pointkontrakten](scoring.md).

| Nyt felt | Kontrakt |
|---|---|
| `gameId` | Ikke-tom servergenereret GUID som tekst. Ny ved hvert accepteret `/api/game/new`, første lobbystart og omkamp; ellers uændret. |
| `turnNumber` | Positivt heltal fælles for alle spillere. Starter på 1 og øges med 1, når et feltvalg åbner næste tur, også i enkeltspiller. Sidste feltvalg øger det ikke. |
| `gameCompleted` | False indtil alle 15 felter hos alle deltagere har en værdi. Gemte nuller tæller som udfyldte; kun JSON-null betyder ledigt. |
| `winnerIndices` | Tomt array under spillet. Efter afslutning alle indeks med højeste `scorecard.total`, i stigende deltagerrækkefølge. Navne bruges ikke som identitet. Enkeltspiller giver `[0]`. |

`currentPlayer` er altid et gyldigt indeks i `players`, også på slutskærmen. Spil-JSON indeholder fortsat ikke spiller-id'er i `players`; browseren kobler lobbyens id'er til samme faste rækkefølge. Join-svarets `joinedPlayerId` fortæller entydigt, hvilken spiller browseren tilmeldte, også ved samtidige joins med ens navne.

### Turskifte og afslutning

1. En spiller skal kaste mindst én gang før feltvalg. De eksisterende regler for maksimalt tre slag og hold/slip bevares.
2. Både pointvalg og strygning skriver præcis ét ledigt felt på den aktive spillers tavle. Beregnede og valgte nuller låser feltet.
3. Hvis ikke alle tavler er fulde, går turen straks til næste deltager. Sidste deltager efterfølges af første; med én deltager begynder samme spillers næste tur.
4. Den nye tur får `dice=[0,0,0,0,0]`, `held=[false,false,false,false,false]`, `rollsUsed=0`, `possibleScores={}` og `turnCompleted=false`. Point, bonus og summer bevares. Der kastes ikke automatisk.
5. HTTP-svaret viser tilstanden efter hele overgangen: det gemte felt på den tidligere spillers tavle og den nye aktive spillers friske tur. Der udstilles ingen midlertidig tilstand mellem feltvalg og turskifte.
6. Hvis alle tavler er fulde efter feltvalget, bliver `gameCompleted=true` og `turnCompleted=true`. Sidste spillers indeks, terninger, hold og slag bevares, `possibleScores` er tomt, og ingen ny tur åbnes. For et almindeligt spil er slutturnummeret derfor `15 × antal spillere`.
7. Vinderne findes fra højeste total inklusive bonus på 50 ved mindst 63 i de øvre felter. Der bruges ingen ekstra afgørelse ved uafgjort. Efter afslutning afvises kast, hold/slip og feltvalg uden ændring af resultatet.

`turnCompleted` i et svar tilhører den tur, svaret viser. Efter et normalt feltvalg er det derfor false for næste tur; true vises ved spilafslutning. Hver inaktiv Game-instans kan internt have en afsluttet tur uden at ændre denne betydning i det fælles HTTP-svar.

### HTTP, identitet og fejl

Den præcise [spil-API-kontrakt](api.md#kommende-kontrakt-for-44) kræver `gameId`, `turnNumber` og `playerId` i JSON-body på alle fire handlingstyper: roll, hold, score og strike. `playerId` er null i enkeltspiller og den tilmeldtes GUID i lobbyspil. Gamle kald uden body erstattes i begge spilformer; frontend og tests tilpasses i kodetrinene.

Serveren sammenligner spil-id, turnummer og relevant spillerrelation under samme lås som spilhandlingen og kopieringen af svaret. En korrekt formateret, men forældet request giver 409 uden tilstandsændring. Dette beskytter også enkeltspiller og en spillers senere tur, hvor `currentPlayer` igen er det samme indeks. Der indføres ikke generel idempotens for gentagne kast eller hold/slip inden for samme tur; klienten genafspiller ikke handlinger automatisk.

Id'erne er kontrol af spilrelationer, ikke login eller hemmelige adgangsoplysninger. Ens navne tillades. Tilskuere får ingen handlings-/omkampsknapper; serveren afviser handlinger med manglende eller uvedkommende spillerrelation efter [fejlkontrakten](api.md#fejl-i-den-kommende-kontrakt).

`POST /api/lobby/restart` bruger det afsluttede spils `gameId` og initiativtagerens `playerId`. Accept opretter et nyt gameId og nulstiller alle tavler og turdata samlet. To samtidige requests med samme gamle gameId kan højst give én accept; den anden møder et forældet gameId. Et afvist forsøg, herunder frakoblet deltager, bevarer hele slutresultatet. Se [lobbyens requests og fejl](lobby.md#kommende-kontrakt-for-44).

### Komponentansvar og ordnede beskeder

| Komponent | Ansvar efter implementering af #44 |
|---|---|
| `Game` og pointtavle | Én spillers kast, hold, felter, point og kontrollerede overgang til ny tur. |
| `GameSession` | Deltagere, aktiv spiller, gameId, turnummer, spilafslutning, vindere samt atomisk kontrol, ændring og svarkopi. |
| `LobbySession` | Tilmeldte spilleres aktuelle forbindelser, start/omkamp og koordinering af lobbyens HTTP-/Oplà-handlinger med ordnede svarbatches. |
| HTTP-endpoints | Validering af requests og oversættelse til 200 med kopieret svar eller danske 400/404/409-fejl. |
| Transport og beskedhandler | Serverens binding til den aktuelle Oplà, ROLL/HOLD-fortolkning og afsendelse til konkrete forbindelser. |
| Frontend og browserbro | Frontend viser serverens resultat og sender handlingens spil-/turkontekst. Broen videresender den eksisterende Oplà-tekstprotokol. |

Låserækkefølgen ved lobbyhandlinger bevares som lobby → spilsession. Registerets korte forbindelsesopslag må ikke skabe en omvendt låserækkefølge. GameSession kalder aldrig tilbage til lobbyen. Spilændring, svarkopi og reservation af modtagerbatches koordineres, før lobbylåsen slippes; alle netværksafsendelser afventes uden tilstandslås. Rækkefølgen er pr. forbindelse, ikke et krav om samtidig levering hos alle spillere.

| Hændelse | Beskeder til aktuelle, tilmeldte forbindelser |
|---|---|
| Første start eller omkamp | `STATE 0 00000` til spiller 0; `WAIT navn` med spiller 0's navn til resten. |
| Accepteret kast eller hold/slip fra HTTP eller Oplà | Én STATE med handlingens kopierede slag/hold til den aktive Oplà; ingen ændring hos ventende spillere. |
| Normalt feltvalg eller strygning | STATE til den nye aktive spiller; WAIT med dennes navn til alle andre. |
| Sidste feltvalg | END til alle tilmeldte spilleres aktuelle forbindelser. Der sendes ikke først STATE for en ekstra tur. |
| READY på en tilmeldt spillers forbindelse | PAIR, JOINED og derefter den aktuelle STATE, WAIT eller END, i denne rækkefølge. |
| Afvist spilhandling | Intet Oplà-svar; HTTP får den relevante JSON-fejl. |

Afbrydelse eller sendesvigt efter accept hverken fortryder, genudfører eller automatisk genafsender spilhandlingen. Øvrige spilleres afsendelsesforsøg fortsætter. Gamle batches beholder deres konkrete forbindelses-id; sen oprydning må ikke lukke en nyere overtager. Genforbindelse læser den aktuelle tilstand frem for at genafspille tidligere handlinger.

Oplà-protokollens formater ændres ikke: `READY`, `READY cccc`, `ROLL`, `HOLD n`, `PAIR cccc`, `JOINED navn`, `STATE r hhhhh`, `WAIT navn` og `END`. Oplàens ROLL/HOLD indeholder hverken gameId eller turnummer og valideres mod den serverbundne spiller og tilstanden ved behandling. Beskyttelsen mod HTTP-requests fra gamle ture kan derfor ikke loves for forsinkede rå Oplà-beskeder. Broen bevarer sin eksisterende regel om ikke at gemme ROLL/HOLD til senere genafsendelse.

### Konkrete acceptscenarier

A–H beskriver nu implementerede backendkontrakter og regler gennem trin 6. Eksemplerne er acceptgrundlag med illustrative værdier, ikke optagelser af konkrete testkørsler. JSON mærket **svarudsnit** viser udvalgte felter; de øvrige følger den [fulde spiltilstand](api.md#kommende-kontrakt-for-44). JSON er gyldig uden udeladelsesmarkører. De faste GUID'er er offentlige eksempelværdier, ikke hemmeligheder eller observerede runtime-id'er.

#### A. Første start

To deltagere er joinet: Andreas ved indeks 0 har id `11111111-1111-4111-8111-111111111111` og kode 4821; Frederik ved indeks 1 har id `22222222-2222-4222-8222-222222222222` og kode 1377. Begge er forbundne.

`POST /api/lobby/start` uden body giver HTTP 200. Svarudsnit:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
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

Alle 15 felter hos begge spillere er null; summer og bonus er 0. Andreas modtager `STATE 0 00000`, Frederik `WAIT Andreas`.

#### B. Feltvalg og rotation tilbage til første spiller

Fra A kaster Andreas med `POST /api/game/roll` og samme body som nedenfor. Antag et accepteret kast `[1,1,1,1,1]`. Han vælger derefter `POST /api/game/score/chance` med:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 1,
  "playerId": "11111111-1111-4111-8111-111111111111"
}
```

HTTP 200 gemmer 5 i Andreas' chancefelt. Svarudsnit:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 2,
  "currentPlayer": 1,
  "dice": [0, 0, 0, 0, 0],
  "held": [false, false, false, false, false],
  "rollsUsed": 0,
  "possibleScores": {},
  "turnCompleted": false,
  "gameCompleted": false,
  "winnerIndices": []
}
```

Andreas får `WAIT Frederik`; Frederik får `STATE 0 00000`. Frederik kaster med tur 2 og eget playerId og vælger derefter `POST /api/game/strike/ones` med samme tur-/spillerkontekst. Svaret er HTTP 200 med `turnNumber=3`, `currentPlayer=0` og frisk tur som ovenfor. Andreas' chancefelt er fortsat 5; Frederiks ones er 0. Andreas får `STATE 0 00000`, Frederik `WAIT Andreas`.

#### C. Enkeltspillerens næste tur

`POST /api/game/new` med `{"name":"Andreas"}` opretter et selvstændigt spil med gameId `cccccccc-cccc-4ccc-8ccc-cccccccccccc`, tom tavle og tur 1. Efter ét accepteret kast `[1,1,1,1,1]` kaldes `POST /api/game/score/chance` med:

```json
{
  "gameId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "turnNumber": 1,
  "playerId": null
}
```

HTTP 200 har `turnNumber=2`, `currentPlayer=0`, 5 i chance og den friske turtilstand fra B. Spillet er ikke afsluttet. Først efter spillerens 15. feltvalg er `turnNumber=15`, `gameCompleted=true`, `turnCompleted=true` og `winnerIndices=[0]`.

#### D. Sidste feltvalg og uafgjort

Et særskilt tospillerscenarie bruger gameId fra A. Ved tur 30 er alle felter udfyldt undtagen Frederiks chance. Andreas har chance 8 og 0 i alle andre felter. Frederik har 0 i alle øvrige felter og har kastet `[1,1,1,1,1]` én gang uden holdte terninger. `possibleScores` er derfor `{"chance":5}`.

`POST /api/game/score/chance` med gameId fra A, `turnNumber=30` og Frederiks playerId giver HTTP 200 og det [fulde slut-JSON i API-kontrakten](api.md#eksempel-paa-afsluttet-spil). Turnummeret bliver på 30, currentPlayer på 1, og alle modtager END. Andreas vinder med 8 mod 5. Et nyt kast med samme kontekst giver 409 og `{"message":"Spillet er afsluttet. Start et nyt spil for at fortsætte."}`.

Alternativt har begge spillere navnet Alex, og første spillers chance er 5 frem for 8. Samme sidste feltvalg giver dette svarudsnit; alle øvrige felter følger slut-JSON-eksemplet:

```json
{
  "players": [
    { "name": "Alex", "scorecard": { "total": 5 } },
    { "name": "Alex", "scorecard": { "total": 5 } }
  ],
  "currentPlayer": 1,
  "turnNumber": 30,
  "turnCompleted": true,
  "gameCompleted": true,
  "winnerIndices": [0, 1]
}
```

Begge er vindere. En spiller med lavere feltsum, men højere total efter bonus, skal tilsvarende vinde; totalen beregnes fortsat af pointtavlen og tæller bonus én gang.

#### E. Forældet HTTP-kontekst og samtidige feltvalg

Efter B afvises Andreas' gentagne score-request fra tur 1 med HTTP 409 og:

```json
{ "message": "Turen er ikke længere aktuel. Hent spiltilstanden igen." }
```

Det gælder også, hvis Frederik allerede har kastet, eller Andreas igen har tur ved turnNumber 3. Ingen af tavlerne ændres. To samtidige score/strike-requests for samme tur kan højst gemme ét felt; taberen afvises med samme turfejl, eller slutfejlen fra D, hvis vinderen afsluttede spillet.

Efter omkampen i G afvises en gammel request fra gameId `aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa`, selv om turnummer og spiller igen matcher. HTTP 409:

```json
{ "message": "Spillet er ikke længere aktuelt. Hent spiltilstanden igen." }
```

#### F. Afbrydelse, browserreserve og genforbindelse

Start fra tur 2 i B før Frederiks første kast. Frederiks Oplà afbrydes; GET /api/lobby viser ham som `connected=false`, mens spillet stadig er på tur 2 med samme gameId. Han kan kalde `POST /api/game/roll` med:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "turnNumber": 2,
  "playerId": "22222222-2222-4222-8222-222222222222"
}
```

HTTP 200 viser ét brugt slag og fem værdier fra 1 til 6. Den accepterede handling bevares trods manglende Oplà-modtager. Ved `READY 1377` på hans nye forbindelse sendes `PAIR 1377`, `JOINED Frederik`, `STATE 1 00000`. GET /api/game er uændret af READY. En ventende deltager genforbinder tilsvarende til WAIT; efter spilafslutning er sidste besked END.

#### G. Accepteret omkamp

Efter D, med begge deltagere forbundne, kalder Andreas eller Frederik `POST /api/lobby/restart`. Eksempel med Andreas:

```json
{
  "gameId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "playerId": "11111111-1111-4111-8111-111111111111"
}
```

HTTP 200 har nyt gameId `bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb`. Alle øvrige startfelter er som i A: turnNumber 1, currentPlayer 0, tomme tavler, 0 i summer/bonus, gameCompleted false og tomme winnerIndices. Samme playerId'er, navne og koder bevares. Andreas får STATE 0 00000, Frederik WAIT Andreas. GET /api/lobby har fortsat `started=true`; et nyt join er fortsat afvist.

#### H. Afvist eller samtidig omkamp

Hvis Frederik er frakoblet efter D, giver requesten fra G HTTP 409:

```json
{ "message": "Alle tilmeldte spillere skal være forbundet før omkamp." }
```

Spillet beholder det afsluttede gameId, alle point og vinderne. Ved to samtidige requests fra tilmeldte deltagere med samme afsluttede gameId og alle forbundne gives præcis én HTTP 200. Den anden giver den gamle-spil-fejl fra E; den må ikke nulstille den nye omkamp igen. En omkamp under et aktivt spil giver 409 med `{"message":"Spillet skal være afsluttet før omkamp."}`. En velformet, men uvedkommende spiller-id giver 409 med `{"message":"Spilleren deltager ikke i dette lobbyspil."}`.

### Kontrolgrundlag for trin 1

Dokumentkontrollen omfatter sammenhæng mellem de tre specifikationer, JSON-syntaks og eksemplernes point-/turdata, lokale links inklusive ankre, UTF-8, whitespace og filafgrænsning. Derudover sammenlignes de øvrige repository-filer og den lokale noCase-ændring med udgangspunktet. Ingen ny backendtest eller fysisk test er nødvendig for denne dokumentationsleverance; scenarier A–H er krav til de senere kodetrin, ikke kørte runtimeprøver.

**Resultat af dokumentkontrollen, 10. november 2026:** Alle 30 JSON-blokke og 101 lokale links inklusive ankre bestod. De to fulde kommende spiltilstande blev også kontrolleret for alle 15 felt-id'er, summer, bonus, turnummer og vinderindeks. De otte interne trin og scenarier A–H findes. Streng UTF-8, whitespace og diffkontrol bestod. Hashsammenligning bekræftede, at alle øvrige repository-filer, inklusive den eksisterende lokale noCase-tilpasning, er uændrede siden starten af trin 1. Kun denne fil, api.md og lobby.md indgår i dokumentationsarbejdet. Ingen backendtests, builds, hardwareprøver, commits eller push er udført i trin 1. Trin 2 er ikke påbegyndt.
