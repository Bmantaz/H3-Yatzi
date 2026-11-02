/*
  H3 Yatzi – Arduino Oplà controller (MKR WiFi 1010 + MKR IoT Carrier)

  Skabelon. Se docs/GDD.md og issues med label "arduino".

  Input (DoD 4 – mindst to handlinger):
    - Ryst Oplaen (accelerometer) = kast
    - Touch 1-5 = hold/slip terning
  Output (NTH: Arduino-feedback):
    - LED'er, buzzer, rund skærm

  Kommunikation med backend (DoD 5): se docs/protocol.md.
  Bibliotek: Arduino_MKRIoTCarrier (Library Manager).
*/

#include <Arduino_MKRIoTCarrier.h>

MKRIoTCarrier carrier;

void setup() {
  Serial.begin(115200);
  carrier.begin();

  // TODO: initialisér skærm, LED'er m.m.
}

void loop() {
  // TODO: læs touch-knapper (carrier.Buttons) og send besked til backend
  // TODO: læs accelerometer (carrier.IMUmodule) og registrér ryst = kast
  // TODO: læs beskeder fra backend og opdatér LED'er/skærm
}
