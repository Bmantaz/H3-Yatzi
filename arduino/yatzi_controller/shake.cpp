#include <Arduino_MKRIoTCarrier.h>
#include "shake.h"

extern MKRIoTCarrier carrier;

void shakeSetup() {
  // TODO: evt. opsætning af accelerometer
}

bool shakeDetected() {
  // TODO (S1.2): læs x/y/z med carrier.IMUmodule.readAcceleration(x, y, z) og log over Serial
  // TODO (S1.3): returnér true ved ét ryst (tærskel + pause mellem kast)
  return false;
}
