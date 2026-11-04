/*
  H3 Yatzi – Arduino Oplà controller (MKR WiFi 1010 + MKR IoT Carrier)

  Filerne er delt op, så vi ikke laver merge-konflikter:
    shake.h / shake.cpp            – Bjarke: ryst = kast (accelerometer, buzzer, skærm)
    dice_input.h / dice_input.cpp  – Abdi:   touch 1–5 = hold/slip, hold-status, LED'er
    yatzi_controller.ino           – samles i S2.2 (seriel-kommunikation med backend)

  Protokol: se docs/protocol.md.
  Bibliotek: Arduino_MKRIoTCarrier (Library Manager).
*/

#include <Arduino_MKRIoTCarrier.h>
#include "shake.h"
#include "dice_input.h"

MKRIoTCarrier carrier;

void setup() {
  Serial.begin(115200);
  carrier.withCase();      // brug carrier.withCase(); hvis casen sidder på
  carrier.begin();

  shakeSetup();
  diceInputSetup();
}

void loop() {
  if (shakeDetected()) {
    Serial.println("ROLL");  // ét ryst = ét kast (docs/protocol.md)
  }

  int die = diceTouched();  // 1–5, eller 0 hvis ingen knap er trykket
  if (die > 0) {
    // Tryk på touch-knap n = hold/slip terning n. Serveren skifter selv tilstand (docs/protocol.md).
    Serial.print("HOLD ");
    Serial.println(die);
    diceToggleHeld(die);  // S1.5: gem status og tænd/sluk grøn LED (erstattes af serverens STATE i S2.3)
  }

  // TODO (S2.2): læs status-beskeder fra backend og send dem videre til dice_input/skærm
}
