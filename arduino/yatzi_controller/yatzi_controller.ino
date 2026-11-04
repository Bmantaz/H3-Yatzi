/*
  H3 Yatzi – Arduino Oplà controller (MKR WiFi 1010 + MKR IoT Carrier)

  Filerne er delt op, så vi ikke laver merge-konflikter:
    shake.h / shake.cpp            – Bjarke: ryst = kast (accelerometer, buzzer)
    screen.h / screen.cpp          – Bjarke: den runde skærm
    link.h / link.cpp              – Bjarke: forbindelsen til serveren (USB nu, WiFi i S3.9)
    dice_input.h / dice_input.cpp  – Abdi:   touch 1–5 = hold/slip, hold-status, LED'er
    yatzi_controller.ino           – Bjarke: samler det hele og tolker serverens beskeder (S2.2)

  Protokol: se docs/protocol.md.
  Bibliotek: Arduino_MKRIoTCarrier (Library Manager).
*/

#include <Arduino_MKRIoTCarrier.h>
#include "shake.h"
#include "dice_input.h"
#include "screen.h"
#include "link.h"

MKRIoTCarrier carrier;

// READY sendes når forbindelsen åbnes og igen med dette interval, indtil serveren svarer.
// (Over USB åbner browseren tit porten efter Oplaen er startet, så første READY kan gå tabt.)
const unsigned long READY_RETRY_MS = 3000;

static String pairCode = "";       // fra PAIR – sendes med READY ved genforbindelse
static bool serverAnswered = false;
static bool wasConnected = false;
static unsigned long readySentAt = 0;

static const bool NO_DICE_HELD[5] = { false, false, false, false, false };

void setup() {
  linkSetup();
  carrier.withCase();      // brug carrier.withCase(); hvis casen sidder på
  carrier.begin();

  shakeSetup();
  diceInputSetup();
  screenSetup();
}

void sendReady() {
  linkSend(pairCode.length() > 0 ? "READY " + pairCode : "READY");
  readySentAt = millis();
}

// "STATE r hhhhh" – fx "STATE 2 10100". Returnerer false hvis linjen ikke passer.
bool handleState(const String& line) {
  if (line.length() != 13 || line[7] != ' ') return false;

  int rolls = line[6] - '0';
  if (rolls < 0 || rolls > 3) return false;

  bool held[5];
  for (int i = 0; i < 5; i++) {
    char c = line[8 + i];
    if (c != '0' && c != '1') return false;
    held[i] = c == '1';
  }

  showHeldDice(held);
  showTurn(rolls);
  return true;
}

void handleMessage(const String& line) {
  if (line.startsWith("#")) return;  // debug

  bool ok = true;
  if (line.startsWith("PAIR ")) {
    pairCode = line.substring(5);
    showPair(pairCode);
  } else if (line.startsWith("JOINED ")) {
    showJoined(line.substring(7));
  } else if (line.startsWith("WAIT ")) {
    showHeldDice(NO_DICE_HELD);
    showWait(line.substring(5));
  } else if (line.startsWith("STATE ")) {
    ok = handleState(line);
  } else if (line == "END") {
    showHeldDice(NO_DICE_HELD);
    showEnd();
  } else {
    ok = false;
  }

  if (ok) {
    serverAnswered = true;
  } else {
    linkDebug("ukendt besked: " + line);
  }
}

void loop() {
  // Ny forbindelse (fx browseren har lige åbnet porten): send READY igen med det samme.
  // Ikke alle programmer melder at de har åbnet porten, så READY sendes også uden.
  bool connected = linkConnected();
  if (connected && !wasConnected) {
    serverAnswered = false;
    readySentAt = millis() - READY_RETRY_MS;
  }
  wasConnected = connected;

  if (!serverAnswered && millis() - readySentAt >= READY_RETRY_MS) {
    sendReady();
  }

  String line;
  while (linkReceive(line)) {
    handleMessage(line);
  }

  if (shakeDetected()) {
    linkSend("ROLL");  // ét ryst = ét kast
  }

  int die = diceTouched();  // 1–5, eller 0 hvis ingen knap er trykket
  if (die > 0) {
    // Tryk på touch-knap n = hold/slip terning n. Serveren skifter selv tilstand.
    linkSend("HOLD " + String(die));
  }
}
