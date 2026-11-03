# Game Design Document – Yatzi

Et digitalt Yatzy-spil, der styres med Arduino Oplà (MKR WiFi 1010).

| Del | Vores udspil |
|---|---|
| **Arbejdstitel** | Yatzi |
| **Kombinationen** | **Gameplay:** turbaseret terningspil, hvor man vurderer risiko og vælger strategi.<br>**Input:** Oplaens bevægelsessensor (accelerometer) mærker når man ryster = kast. De 5 touch-knapper = de 5 terninger; tryk for at holde/slippe en terning.<br>**Output:** hver spillers PC-skærm viser terninger og pointtavle. Oplaens LED'er lyser grønt over holdte terninger, buzzeren rasler ved kast, og den runde skærm viser "Slag 1/3".<br>**Målgruppe:** venner og familie, 1–6 spillere (pointtavlen har 6 kolonner), ca. 8 år og op. |
| **Idéen** | Du ryster Oplaen som et raflebæger og slår fem terninger på én gang. Hold de terninger, du vil beholde, slå op til tre gange, og skriv resultatet på pointtavlen. |
| **Spilleren** | 1–6 spillere ved en sammenkomst. Hver spiller har sin egen Oplà som raflebæger og sin egen PC, og alle følger det samme spil. Man joiner en lobby med sit navn og koden på sin Oplà. |
| **Udstyret** | Én Oplà og én PC pr. spiller, og spillerne sidder hver for sig. Én PC er vært og kører serveren, som gøres tilgængelig på internettet med en HTTPS-tunnel; alle åbner spillet i browseren. Oplaen forbindes med USB (via browseren) eller WiFi – det er ligegyldigt for spilleren (se [protocol.md](protocol.md)). Oplaen bruges kun til at kaste og holde terninger; pointtavlen ligger på PC'en. |
| **Mål og regler** | **Mål:** flest point, når alle 15 felter er udfyldt: 1'ere–6'ere, 1 par, 2 par, 3 ens, 4 ens, lille straight, stor straight, hus, chance og YATZY. Bonus på 50 point ved mindst 63 i 1'ere–6'ere.<br>**En tur:** op til 3 slag. Turen slutter, når spilleren placerer sit resultat i et ledigt felt, eller stryger et felt (0 point). Så er det næste spillers tur. |
| **Gameplay loop** | Ryst → se terningerne → hold de gode → ryst igen (maks. 3 slag) → vælg felt → næste spillers tur (de andre Oplà'er venter). |
| **Styring og feedback** | Ryst Oplaen → terningerne ruller på skærmen med lyd og lander efter et øjeblik.<br>Tryk touch 1–5 → terningen får grøn kant på skærmen og LED'en lyser. Tryk igen for at slippe.<br>Efter sidste slag → skærmen viser, hvor mange point hvert ledigt felt vil give. Spilleren vælger felt med musen. |
| **Held og færdigheder** | **Held:** hvad terningerne viser. **Færdighed:** hvilke terninger man holder, hvornår man stopper, hvilke felter man satser på (fx jagte bonus), og hvilket felt man ofrer, når slaget er dårligt. |
| **Den mindste udgave** | Én spiller. Ryst → fem tilfældige tal vises på PC'en. Touch 1–5 holder terninger, og spillet tæller 3 slag. Pointtavlen kan i første test være på papir. |
| **Det usikre** | 1. **Rystet:** reagerer sensoren på ét ryst som ét kast – ikke nul eller tre?<br>2. **Hold:** er touch-knapperne pålidelige, og kan man se, hvilken knap der hører til hvilken terning?<br>3. **Forbindelsen:** kommer data stabilt fra Oplaen til serveren over internettet – både via browseren (USB/Web Serial) og direkte over WiFi? Er tunnelen hurtig nok til, at et ryst føles øjeblikkeligt?<br>4. **Valg af felt:** mus på PC eller knapper på Oplaen? Test med 3–5 personer, om de forstår spillet uden forklaring. |
