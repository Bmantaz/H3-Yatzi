#include <Arduino_MKRIoTCarrier.h>
#include "dice_input.h"

extern MKRIoTCarrier carrier;

void diceInputSetup() {
  // TODO: evt. lysstyrke for LED'erne
}

int diceTouched() {
  // TODO (S1.4): carrier.Buttons.update() og tjek TOUCH0–TOUCH4 med onTouchDown
  return 0;
}

void showHeldDice(const bool held[5]) {
  // TODO (S1.5): grøn LED over holdte terninger – find ud af hvilken LED der sidder ved hvilken knap
  (void)held;
}
