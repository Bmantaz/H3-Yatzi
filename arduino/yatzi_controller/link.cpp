#include "link.h"
#include <WiFiNINA.h>
#include <ArduinoHttpClient.h>

// WiFi-oplysninger ligger i arduino_secrets.h, som IKKE er i git (kopiér arduino_secrets.example.h).
// Uden filen (fx i CI) kompilerer koden stadig, men kører kun over USB.
#if __has_include("arduino_secrets.h")
#include "arduino_secrets.h"
#else
#define SECRET_SSID ""
#define SECRET_PASS ""
#define SECRET_HOST ""
#define SECRET_PORT 443
#endif

// Samme grænse som serveren (docs/protocol.md – sikring af /ws/opla)
static const size_t MAX_LINE = 64;

static char rx[MAX_LINE + 1];
static size_t rxLen = 0;
static bool rxTooLong = false;
static unsigned long rxLastCharAt = 0;

// En halv linje, der har ventet så længe, er rester fra en gammel forbindelse og kasseres.
// Serveren og browser-broen sender altid hele linjer på én gang.
static const unsigned long RX_STALE_MS = 500;

// --- WiFi / WebSocket (S3.9) ---

static const char* WS_PATH = "/ws/opla";
static const unsigned long WIFI_JOIN_MS = 10000;   // højst så længe at komme på WiFi
static const unsigned long WS_TIMEOUT_MS = 5000;   // højst så længe at åbne WebSocket
static const unsigned long WIFI_RETRY_MS = 5000;   // pause mellem forsøg
// Et forsøg kan blokere i nogle sekunder. Det sker derfor kun, når spilleren ikke har
// sendt noget i så lang tid – ellers hakker det midt i et kast.
static const unsigned long QUIET_BEFORE_RETRY_MS = 8000;

static WiFiSSLClient sslClient;
static WebSocketClient socket(sslClient, SECRET_HOST, SECRET_PORT);

static bool wifiUp = false;           // true = WebSocket til serveren er oppe, og WiFi bruges
static bool firstWifiTry = true;
static bool connectionChanged = false;  // se linkConnected()
static unsigned long lastWifiTryAt = 0;
static unsigned long lastSendAt = 0;

static bool wifiConfigured() {
  return strlen(SECRET_SSID) > 0 && strlen(SECRET_HOST) > 0;
}

static void setWifiUp(bool up) {
  if (up == wifiUp) return;
  wifiUp = up;
  connectionChanged = true;
  linkDebug(up ? "forbindelse: WiFi" : "forbindelse: USB");
}

// Forbinder til WiFi og åbner WebSocket. Blokerer i højst ca. WIFI_JOIN_MS + WS_TIMEOUT_MS.
static bool connectWifiAndSocket() {
  if (WiFi.status() != WL_CONNECTED) {
    WiFi.setTimeout(WIFI_JOIN_MS);
    WiFi.begin(SECRET_SSID, SECRET_PASS);
    if (WiFi.status() != WL_CONNECTED) {
      linkDebug("WiFi: kunne ikke forbinde");
      return false;
    }
  }

  socket.setHttpResponseTimeout(WS_TIMEOUT_MS);
  int status = socket.begin(WS_PATH);  // 0 = WebSocket-håndtrykket lykkedes
  if (status != 0) {
    linkDebug("WebSocket: fejl " + String(status) + " (er certifikatet uploadet?)");
    socket.stop();
    return false;
  }
  return true;
}

void linkSetup() {
  Serial.begin(115200);
  // WiFi forsøges først i linkUpdate(), så skærmen kan vise "Forbinder..." imens.
}

void linkUpdate() {
  if (!wifiConfigured()) return;

  unsigned long now = millis();

  if (wifiUp) {
    if (WiFi.status() != WL_CONNECTED || !socket.connected()) {
      socket.stop();
      setWifiUp(false);        // skift til USB
      lastWifiTryAt = millis();
    }
    return;
  }

  if (!firstWifiTry) {
    if (now - lastWifiTryAt < WIFI_RETRY_MS) return;
    if (lastSendAt != 0 && now - lastSendAt < QUIET_BEFORE_RETRY_MS) return;  // spilleren er i gang
  }
  firstWifiTry = false;

  bool ok = connectWifiAndSocket();
  lastWifiTryAt = millis();
  if (ok) setWifiUp(true);
}

bool linkConnected() {
  if (connectionChanged) {
    connectionChanged = false;
    return false;  // ÉN gang: giver yatzi_controller.ino en "ny forbindelse" og dermed READY
  }
  // WiFi: WebSocket er oppe. USB: et program på PC'en har åbnet porten (native USB).
  return wifiUp || (bool)Serial;
}

bool linkUsesWifi() {
  return wifiUp;
}

void linkSend(const String& line) {
  lastSendAt = millis();
  if (wifiUp) {
    socket.beginMessage(TYPE_TEXT);
    socket.print(line);
    socket.endMessage();
  } else {
    Serial.println(line);
  }
}

void linkDebug(const String& text) {
  Serial.print("# ");
  Serial.println(text);
}

// Én hel besked fra serveren over WebSocket (WiFi).
static bool receiveWifi(String& line) {
  int size = socket.parseMessage();
  if (size <= 0) return false;

  String text = socket.readString();
  text.trim();
  if (text.length() == 0 || text.length() > MAX_LINE) return false;  // for lange linjer kasseres

  line = text;
  return true;
}

// Én hel linje fra USB (browser-broen).
static bool receiveUsb(String& line) {
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

bool linkReceive(String& line) {
  if (wifiUp) {
    // Kun én vej ad gangen: mens WiFi bruges, ignoreres USB-input (men bufferen tømmes).
    while (Serial.available() > 0) Serial.read();
    rxLen = 0;
    rxTooLong = false;
    return receiveWifi(line);
  }
  return receiveUsb(line);
}
