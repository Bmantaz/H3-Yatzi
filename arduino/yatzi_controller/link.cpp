#include "link.h"

// Samme grænse som serveren (docs/protocol.md – sikring af /ws/opla)
static const size_t MAX_LINE = 64;

static char rx[MAX_LINE + 1];
static size_t rxLen = 0;
static bool rxTooLong = false;
static unsigned long rxLastCharAt = 0;

// En halv linje, der har ventet så længe, er rester fra en gammel forbindelse og kasseres.
// Serveren og browser-broen sender altid hele linjer på én gang.
static const unsigned long RX_STALE_MS = 500;

void linkSetup() {
  Serial.begin(115200);
}

bool linkConnected() {
  // Native USB: true når et program på PC'en har åbnet porten
  return (bool)Serial;
}

void linkSend(const String& line) {
  Serial.println(line);
}

void linkDebug(const String& text) {
  Serial.print("# ");
  Serial.println(text);
}

bool linkReceive(String& line) {
  while (Serial.available() > 0) {
    char c = Serial.read();

    unsigned long now = millis();
    if (rxLen > 0 && now - rxLastCharAt > RX_STALE_MS) {
      rxLen = 0;
      rxTooLong = false;
    }
    rxLastCharAt = now;

    if (c == '\r') continue;  // \r\n accepteres

    if (c == '\n') {
      bool ok = rxLen > 0 && !rxTooLong;
      rx[rxLen] = '\0';
      rxLen = 0;
      rxTooLong = false;
      if (ok) {
        line = rx;
        return true;
      }
      continue;
    }

    if (rxLen < MAX_LINE) {
      rx[rxLen++] = c;
    } else {
      rxTooLong = true;  // resten af linjen smides væk
    }
  }
  return false;
}
