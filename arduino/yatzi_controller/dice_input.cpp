#include <Arduino_MKRIoTCarrier.h>
#include "dice_input.h"

extern MKRIoTCarrier carrier;

// Touch-knap -> terning: nu omvendt, så terning 1 = TOUCH4 ... terning 5 = TOUCH0.
static const touchButtons TOUCH_FOR_DIE[5] = { TOUCH4, TOUCH3, TOUCH2, TOUCH1, TOUCH0 };

// Touch-signalet kan flimre, især med plastikcase. Et nyt tryk tæller derfor først,
// når knappen har været sluppet så længe (ms). Ret tallet, hvis det er for træg/følsomt.
static const unsigned long RELEASE_MS = 120;

static bool touchedBefore[5]          = { false, false, false, false, false };
static unsigned long releasedAt[5]    = { 0, 0, 0, 0, 0 };

// LED -> terning, også omvendt, så LED'en stadig sidder ved sin knap.
static const int LED_FOR_DIE[5] = { 4, 3, 2, 1, 0 };

// Grøn LED for en holdt terning. Lav værdi = ikke blændende kraftigt.
static const int GREEN_R = 0, GREEN_G = 120, GREEN_B = 0;

void diceInputSetup() {
  // Start med alle LED'er slukket. Derefter følger de serverens STATE.
  const bool none[5] = { false, false, false, false, false };
  showHeldDice(none);
}

int diceTouched() {
  // Opdaterer touch-tilstanden. Må kun kaldes én gang pr. loop().
  carrier.Buttons.update();

  unsigned long now = millis();
  int result = 0;

  for (int i = 0; i < 5; i++) {
    bool touched = carrier.Buttons.getTouch(TOUCH_FOR_DIE[i]);

    if (!touched) {
      if (touchedBefore[i]) {
        releasedAt[i] = now;  // knappen blev lige sluppet
      }
      touchedBefore[i] = false;
      continue;
    }

    // Rørt nu. Kun skiftet fra "ikke rørt" -> "rørt" er et nyt tryk,
    // så en finger der hviler på knappen giver ikke flere beskeder.
    bool newPress = !touchedBefore[i];
    touchedBefore[i] = true;

    if (newPress && result == 0 && now - releasedAt[i] >= RELEASE_MS) {
      result = i + 1;  // terning 1–5
    }
  }
  return result;
}

// Sætter LED'erne ud fra hvilke terninger der er holdt. Kaldes med serverens STATE/WAIT/END.
void showHeldDice(const bool held[5]) {
  for (int i = 0; i < 5; i++) {
    if (held[i]) {
      carrier.leds.setPixelColor(LED_FOR_DIE[i], GREEN_R, GREEN_G, GREEN_B);
    } else {
      carrier.leds.setPixelColor(LED_FOR_DIE[i], 0, 0, 0);
    }
  }
  carrier.leds.show();
}
