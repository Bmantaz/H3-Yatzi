// Kopiér denne fil til arduino_secrets.h og udfyld den.
// arduino_secrets.h står i .gitignore og må ALDRIG i git (WiFi-koden er hemmelig).
// Mangler arduino_secrets.h, kører Oplaen kun over USB.

#define SECRET_SSID "navn-på-dit-wifi"
#define SECRET_PASS "wifi-koden"

// Tunnel-adressen UDEN https:// og uden skråstreg, fx "ord-ord-ord.ngrok-free.dev".
// Root-certifikatet for adressen skal uploades til Oplaen (se docs/protocol.md).
#define SECRET_HOST "din-adresse.ngrok-free.dev"
#define SECRET_PORT 443
