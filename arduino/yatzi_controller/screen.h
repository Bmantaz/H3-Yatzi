// Den runde skærm. Ansvarlig: Bjarke (S2.2 kalder, S2.4 tegner)
#pragma once
#include <Arduino.h>

void screenSetup();                   // viser "Forbinder..." indtil serveren svarer

void showConnecting();                // "Forbinder..." – ved opstart og når forbindelsen er væk

void showPair(const String& code);    // PAIR cccc   – koden der skal skrives i lobbyen
void showJoined(const String& name);  // JOINED navn – parret med spilleren
void showWait(const String& name);    // WAIT navn   – "Vent – navn"
void showTurn(int rolls);             // STATE r …   – "Slag r/3"
void showEnd();                       // END         – "Slut"
void showTransport();                 // S3.9: lille "USB"/"WiFi" nederst (efter linkUsesWifi())
