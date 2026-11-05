#include <Arduino_MKRIoTCarrier.h>
#include "shake.h"

extern MKRIoTCarrier carrier;

// S1.2: 1 = skriv accelerometer-data til Serial Plotter (Værktøjer → Serial Plotter, 115200 baud).
// Skal være 0, når Oplaen taler med serveren – ellers blander tallene sig med protokol-beskederne.
#define SHAKE_LOG 0

// S1.3: målt i #28 – stille ≈ 1 g, let ryst ≈ 1.8 g, kraftigt ryst ≈ 6 g
const float SHAKE_THRESHOLD = 2.5;            // g – over dette tæller som ryst
const unsigned long SHAKE_QUIET_MS = 1000;    // så længe skal Oplaen have været rolig før næste kast

static unsigned long lastPeak = 0;  // sidste gang total var over tærsklen

// Touch-knapperne reagerer på hånden, når man ryster (set i S2.4-testen). Derfor regnes
// Oplaen som "i bevægelse", når total afviger så meget fra 1 g, og så længe efter.
const float MOVE_THRESHOLD = 0.4;             // g
const unsigned long MOVE_QUIET_MS = 700;

static unsigned long lastMove = 0;  // sidste gang Oplaen var i bevægelse

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

  if (fabs(total - 1.0) > MOVE_THRESHOLD) {
    lastMove = millis();
  }

  if (total < SHAKE_THRESHOLD) {
    return false;
  }

  // Et ryst giver mange toppe frem og tilbage. Kun den første top efter en rolig pause
  // er et kast – resten af rystet (også et langt ryst) forlænger bare pausen.
  unsigned long now = millis();
  bool newShake = now - lastPeak >= SHAKE_QUIET_MS;
  lastPeak = now;
  return newShake;
}

bool shakeMoving() {
  return millis() - lastMove < MOVE_QUIET_MS;
}

void shakeSound() {
  // S2.4: terninger der rasler – korte klik i tilfældig tone (ca. ¼ sek.)
  for (int i = 0; i < 6; i++) {
    carrier.Buzzer.sound(random(700, 1800));
    delay(15);
    carrier.Buzzer.noSound();
    delay(random(15, 40));
  }
}
