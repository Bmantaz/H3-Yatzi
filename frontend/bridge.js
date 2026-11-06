// Browser-bro (S2.11): Oplà i USB ⇄ Web Serial ⇄ WebSocket /ws/opla ⇄ server.
//
// Broen kender ikke spillet. Den sender linjer uændret begge veje og dropper debug-linjer (#).
// Resten af siden får besked via hændelser på window:
//   opla:pair    { code }      – serveren har givet Oplaen en kode → udfyld lobbyens kodefelt
//   opla:status  { text, ok }  – tekst til statuslinjen
//
// Test mod spike-serveren indtil /ws/opla er klar: åbn siden med ?ws=/ws/spike

(() => {
  const BAUD_RATE = 115200;
  const RECONNECT_MS = 2000;
  const WS_PATH = new URLSearchParams(location.search).get("ws") || "/ws/opla";

  let port = null;
  let writer = null;
  let reader = null;
  let ws = null;
  let pendingReady = null;   // seneste READY fra Oplaen, mens serveren ikke er forbundet
  let socketOpenedBefore = false;
  let running = false;

  const emit = (name, detail) => window.dispatchEvent(new CustomEvent(name, { detail }));
  const status = (text, ok = true) => emit("opla:status", { text, ok });
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  // --- Serveren ---

  function openSocket() {
    const url = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}${WS_PATH}`;
    ws = new WebSocket(url);

    ws.onopen = () => {
      status("Oplà forbundet til serveren");
      if (pendingReady) {
        ws.send(pendingReady);
        pendingReady = null;
      }
      // Ny forbindelse efter et udfald: serveren kender os ikke længere, så Oplaen skal sende
      // READY (med sin kode) igen. Første gang sender Oplaen selv READY, når porten åbnes.
      if (socketOpenedBefore) restartOpla();
      socketOpenedBefore = true;
    };

    ws.onmessage = (event) => {
      const line = String(event.data).trim();
      if (!line || line.startsWith("#")) return;
      if (line.startsWith("PAIR ")) emit("opla:pair", { code: line.slice(5) });
      writeToOpla(line);
    };

    ws.onclose = () => {
      if (!running) return;
      status("Mistede forbindelsen til serveren – prøver igen …", false);
      setTimeout(() => running && openSocket(), RECONNECT_MS);
    };
  }

  function sendToServer(line) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(line);
    } else if (line.startsWith("READY")) {
      pendingReady = line;  // sendes når forbindelsen er oppe
    }
    // ROLL/HOLD mens serveren er væk smides væk – de må ikke komme frem senere
  }

  // --- Oplaen ---

  async function writeToOpla(line) {
    if (!writer) return;
    try {
      await writer.write(new TextEncoder().encode(line + "\n"));
    } catch (err) {
      console.warn("Kunne ikke skrive til Oplaen:", err);
    }
  }

  // Slukker og tænder "porten er åben"-signalet (DTR). Oplaen opfatter det som en ny
  // forbindelse og sender READY igen (S2.2).
  async function restartOpla() {
    if (!port) return;
    try {
      await port.setSignals({ dataTerminalReady: false });
      await sleep(300);
      await port.setSignals({ dataTerminalReady: true });
    } catch (err) {
      console.warn("Kunne ikke genstarte forbindelsen til Oplaen:", err);
    }
  }

  async function readLines() {
    reader = port.readable.pipeThrough(new TextDecoderStream()).getReader();
    let buffer = "";
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += value;

      let i;
      while ((i = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, i).trim();
        buffer = buffer.slice(i + 1);
        if (!line) continue;
        if (line.startsWith("#")) {
          console.debug("Oplà:", line);
        } else {
          sendToServer(line);
        }
      }
    }
  }

  // --- Offentlig ---

  async function connect() {
    if (!("serial" in navigator)) {
      status("Din browser kan ikke forbinde til Oplaen. Brug Chrome eller Edge.", false);
      return;
    }
    if (running) return;

    try {
      port = await navigator.serial.requestPort();  // kræver at spilleren har klikket
      await port.open({ baudRate: BAUD_RATE });
    } catch (err) {
      port = null;
      if (err.name !== "NotFoundError") {  // NotFoundError = spilleren lukkede vinduet
        status(`Kunne ikke åbne Oplaen: ${err.message}. Er Serial Monitor lukket?`, false);
      }
      return;
    }

    running = true;
    socketOpenedBefore = false;
    writer = port.writable.getWriter();
    status("Oplà fundet – forbinder til serveren …");
    openSocket();

    readLines()
      .catch((err) => console.warn("Læsefejl fra Oplaen:", err))
      .finally(() => {
        if (running) {
          status("Oplaen blev frakoblet. Sæt den i igen og tryk Forbind.", false);
          disconnect();
        }
      });
  }

  async function disconnect() {
    running = false;
    if (ws) ws.close();
    ws = null;
    try {
      if (reader) await reader.cancel();
      if (writer) writer.releaseLock();
      if (port) await port.close();
    } catch (err) {
      console.warn(err);
    }
    reader = writer = port = null;
  }

  window.OplaBridge = {
    supported: "serial" in navigator,
    connect,
    disconnect,
    get connected() { return running; },
  };

  // Knap og statuslinje, hvis siden har dem
  const button = document.getElementById("opla-connect-btn");
  const statusLine = document.getElementById("opla-status");
  if (button) button.addEventListener("click", connect);
  if (statusLine) {
    window.addEventListener("opla:status", (e) => {
      statusLine.textContent = e.detail.text;
      statusLine.classList.toggle("error", !e.detail.ok);
    });
    window.addEventListener("opla:pair", (e) => {
      statusLine.textContent = `Oplà forbundet – din kode er ${e.detail.code}`;
      statusLine.classList.remove("error");
    });
    if (!window.OplaBridge.supported) {
      statusLine.textContent = "Din browser kan ikke forbinde til Oplaen. Brug Chrome eller Edge.";
      statusLine.classList.add("error");
      if (button) button.disabled = true;
    }
  }
})();
