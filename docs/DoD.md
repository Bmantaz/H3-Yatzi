# Mellemprojekt – Definition of Done (DoD)

Hvert punkt har et tilhørende GitHub-issue (label `dod`, milestone **DoD – Mellemprojekt**). Luk issuet, når punktet er opfyldt.

1. **Game Design Document (GDD):** Gruppen har udarbejdet et mini-GDD, der beskriver spilidé, målgruppe, regler, gameplay loop, styring og feedback. → [docs/GDD.md](GDD.md)
2. **Gameplay loop:** Spillet har et tydeligt gameplay loop, hvor spilleren foretager handlinger, får feedback og kan reagere på resultatet.
3. **Spillets livscyklus:** Spillet kan startes, spilles og afsluttes. Der er tydelige regler og en defineret betingelse for, hvornår spillet slutter.
4. **Arduino-controller:** Arduino Oplà fungerer som fysisk controller. Mindst to forskellige handlinger fra Arduinoen skal kunne påvirke spillet.
5. **Kommunikation:** Arduinoen kommunikerer med backend via en relevant kommunikationsprotokol. Gruppen kan forklare deres valg og demonstrere kommunikationen. → [docs/protocol.md](protocol.md)
6. **Backend og spillogik:** Spillet har en selvstændig backend, der håndterer spillets tilstand og mindst én central del af spillogikken, eksempelvis point, liv eller regler.
7. **Frontend og feedback:** Spillet har en grafisk frontend, der viser spillets tilstand og giver tydelig feedback på spillerens handlinger.
8. **Integration:** Arduino, backend og frontend fungerer sammen. Gruppen kan demonstrere og forklare dataflowet gennem hele løsningen.
9. **Brugertest:** Spillet er testet af mindst én person uden for gruppen. Gruppen har indsamlet feedback og gennemført mindst én forbedring.
10. **GitHub og præsentation:** Projektets kode ligger på GitHub, alle gruppemedlemmer har bidraget, og gruppen kan demonstrere spillet samt forklare deres tekniske valg.

## ⭐ Nice to Have (NTH)

Label `nth`, milestone **Nice to Have**.

- **MQTT** – broker, topics og dokumenteret beskedformat mellem Arduino og backend.
- **Highscore og database** – gem resultater og vis et leaderboard.
- **Multiplayer** – flere spillere med hver deres Arduino-controller.
- **SignalR** – realtidskommunikation mellem backend og frontend.
- **Flere sværhedsgrader** – tilpas udfordringer, tempo eller regler.
- **Avancerede sensorer** – accelerometer, gyroskop, lys eller gestus i styringen.
- **Arduino-feedback** – LED'er, buzzer eller skærm giver feedback.
- **Animationer og lyde** – animationer, lydeffekter og visuelle forbedringer.
- **Automatiserede tests** – tests af backend/spillogik.
- **Deployment** – spillet kan tilgås fra andre computere.
