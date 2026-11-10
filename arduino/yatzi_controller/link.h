// Forbindelsen til serveren – det ENESTE sted beskeder sendes og modtages.
// Ansvarlig: Bjarke (S2.2). WiFi tilføjes her i S3.9 (Abdi) uden at ændre resten.
#pragma once
#include <Arduino.h>

void linkSetup();

// Er der en forbindelse til serveren lige nu? (USB: en browser/monitor har porten åben)
bool linkConnected();

// Sender én protokol-linje til serveren, fx "ROLL".
void linkSend(const String& line);

// Debug-linje ("# …") – altid på USB, ignoreres af modtageren.
void linkDebug(const String& text);

// Returnerer true og udfylder line, når en hel linje er modtaget (uden linjeskift).
// Blokerer aldrig. Linjer over 64 tegn kasseres.
bool linkReceive(String& line);

// S3.9: kald først i loop() – holder WiFi/WebSocket i live og vælger USB eller WiFi.
void linkUpdate();

// true når beskederne går over WiFi, false når de går over USB.
bool linkUsesWifi();
