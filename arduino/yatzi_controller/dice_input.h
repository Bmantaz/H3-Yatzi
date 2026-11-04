// Touch 1–5 = hold/slip terning. Ansvarlig: Abdi (S1.4, S1.5, S2.3, S3.4)
#pragma once

void diceInputSetup();

// Returnerer terningens nummer (1–5) når en knap trykkes, ellers 0.
int diceTouched();

// Sætter LED'erne ud fra hvilke terninger der holdes (held[0..4]).
void showHeldDice(const bool held[5]);

// Skifter hold/slip for terning 1–5 (S1.5): gemmer status og opdaterer LED'erne.
void diceToggleHeld(int die);
