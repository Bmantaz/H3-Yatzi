#include <Arduino_MKRIoTCarrier.h>
#include "shake.h"

extern MKRIoTCarrier carrier;

// S1.2: 1 = skriv accelerometer-data til Serial Plotter (Værktøjer → Serial Plotter, 115200 baud).
// Skal være 0, når Oplaen taler med serveren – ellers blander tallene sig med protokol-beskederne.
#define SHAKE_LOG 1

void shakeSetup() {
  // Accelerometeret startes af carrier.begin() i .ino
}

bool shakeDetected() {
  if (!carrier.IMUmodule.accelerationAvailable()) {
    return false;  // ingen ny måling endnu
  }

  float x, y, z;  // i g – ligger Oplaen stille, er z ca. 1 (tyngdekraften)
  carrier.IMUmodule.readAcceleration(x, y, z);

  // Samlet styrke uanset retning: ca. 1.0 i ro, meget mere når man ryster
  float total = sqrt(x * x + y * y + z * z);

#if SHAKE_LOG
  Serial.print("x:");     Serial.print(x);
  Serial.print(" y:");    Serial.print(y);
  Serial.print(" z:");    Serial.print(z);
  Serial.print(" total:"); Serial.println(total);
#endif

  // TODO (S1.3): returnér true ved ét ryst (tærskel + pause mellem kast)
  return false;
}
