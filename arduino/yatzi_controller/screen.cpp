#include <Arduino_MKRIoTCarrier.h>
#include "screen.h"
#include "link.h"

extern MKRIoTCarrier carrier;

// TODO (S2.4): tegn på den runde skærm (carrier.display). Indtil da skrives det som debug.

void screenSetup() {
}

void showPair(const String& code) {
  linkDebug("skærm: kode " + code);
}

void showJoined(const String& name) {
  linkDebug("skærm: " + name);
}

void showWait(const String& name) {
  linkDebug("skærm: Vent – " + name);
}

void showTurn(int rolls) {
  linkDebug("skærm: Slag " + String(rolls) + "/3");
}

void showEnd() {
  linkDebug("skærm: Slut");
}
