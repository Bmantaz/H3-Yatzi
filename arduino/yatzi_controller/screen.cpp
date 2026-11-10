#include <Arduino_MKRIoTCarrier.h>
#include "screen.h"
#include "link.h"

extern MKRIoTCarrier carrier;

// Den runde skærm er 240 × 240. Standardskriften er 6 × 8 pixels pr. tegn ved størrelse 1,
// og kan ikke vise æ/ø/å – derfor står teksterne uden.
static const int16_t SIZE = 240;

// Skriver tekst vandret centreret ved y. Størrelsen vælges så teksten kan være der (maks. maxSize).
static void printCentered(const String& text, uint8_t maxSize, int16_t y, uint16_t color) {
  int size = (SIZE - 20) / (6 * max(1, (int)text.length()));
  size = constrain(size, 2, maxSize);

  carrier.display.setTextSize(size);
  carrier.display.setTextColor(color);

  int16_t x1, y1;
  uint16_t w, h;
  carrier.display.getTextBounds(text, 0, 0, &x1, &y1, &w, &h);
  carrier.display.setCursor((SIZE - w) / 2, y);
  carrier.display.print(text);
}

// S3.9: lille "USB"/"WiFi" nederst, så man kan se hvilken vej Oplaen snakker med serveren.
static void drawTransport() {
  carrier.display.fillRect(60, 190, 120, 24, ST77XX_BLACK);
  printCentered(linkUsesWifi() ? "WiFi" : "USB", 2, 192, ST77XX_WHITE);
}

// Lille overskrift øverst og stor tekst i midten.
static void draw(const String& title, const String& big, uint16_t color) {
  carrier.display.fillScreen(ST77XX_BLACK);
  printCentered(title, 3, 70, ST77XX_WHITE);
  printCentered(big, 5, 115, color);
  drawTransport();
}

void screenSetup() {
  carrier.display.setTextWrap(false);
  showConnecting();
}

void showConnecting() {
  draw("Yatzi", "Forbinder...", ST77XX_CYAN);
  linkDebug("skærm: Forbinder...");
}

void showPair(const String& code) {
  draw("Kode", code, ST77XX_YELLOW);
  linkDebug("skærm: kode " + code);
}

void showJoined(const String& name) {
  draw("Hej", name, ST77XX_GREEN);
  linkDebug("skærm: " + name);
}

void showWait(const String& name) {
  draw("Vent", name, ST77XX_ORANGE);
  linkDebug("skærm: Vent – " + name);
}

void showTurn(int rolls) {
  draw("Din tur", "Slag " + String(rolls) + "/3", ST77XX_GREEN);
  linkDebug("skærm: Slag " + String(rolls) + "/3");
}

void showEnd() {
  draw("Spillet er", "Slut", ST77XX_CYAN);
  linkDebug("skærm: Slut");
  drawTransport();
}

void showTransport() {
  drawTransport();
}
