// H3 Yatzi frontend
//
// Tilstanden fra /api/game (se docs/api.md):
// {
//   players:        [{ name: "Frederik", scorecard: {...} }],
//   currentPlayer:  0,
//   dice:           [0, 0, 0, 0, 0],   // 1-6, 0 = ikke slået endnu
//   held:           [false, false, false, false, false],
//   rollsUsed:      0,
//   possibleScores: {},
//   turnCompleted:  false
// }
//
// Lobbyen fra /api/lobby (se docs/lobby.md):
// { players: [{ id, name, connected }], started: false }

const MAX_ROLLS = 3;
const DICE_COUNT = 5;
const MAX_PLAYERS = 6;
const POLL_MS = 1000;

async function request(path, options) {
  let res;
  try {
    res = await fetch(path, options);
  } catch {
    throw new Error("Ingen forbindelse til serveren.");
  }

  const data = await res.json().catch(() => null);
  if (!res.ok) {
    const err = new Error(data?.message || "Serveren svarede med en fejl.");
    err.status = res.status;
    throw err;
  }
  return data;
}

function postJson(path, body) {
  return request(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

const api = {
  getGame() {
    return request("/api/game");
  },

  newGame(name) {
    return postJson("/api/game/new", { name });
  },

  roll() {
    return request("/api/game/roll", { method: "POST" });
  },

  // API'et tæller terninger 1-5
  toggleHold(index) {
    return request(`/api/game/hold/${index + 1}`, { method: "POST" });
  },

  score(category) {
    return request(`/api/game/score/${category}`, { method: "POST" });
  },

  getLobby() {
    return request("/api/lobby");
  },

  join(name, code) {
    return postJson("/api/lobby/join", { name, code });
  },

  startLobby() {
    return request("/api/lobby/start", { method: "POST" });
  },
};

// Felt-id'erne fra docs/api.md i den rækkefølge de står på pointtavlen
const CATEGORIES = [
  ["ones", "1'ere"],
  ["twos", "2'ere"],
  ["threes", "3'ere"],
  ["fours", "4'ere"],
  ["fives", "5'ere"],
  ["sixes", "6'ere"],
  ["onePair", "1 par"],
  ["twoPairs", "2 par"],
  ["threeOfAKind", "3 ens"],
  ["fourOfAKind", "4 ens"],
  ["smallStraight", "Lille straight"],
  ["largeStraight", "Stor straight"],
  ["fullHouse", "Hus"],
  ["chance", "Chance"],
  ["yatzy", "Yatzy"],
];

const el = {
  lobbyScreen: document.getElementById("lobby-screen"),
  joinForm: document.getElementById("join-form"),
  nameInput: document.getElementById("player-name"),
  codeInput: document.getElementById("opla-code"),
  joined: document.getElementById("joined"),
  lobbyCount: document.getElementById("lobby-count"),
  lobbyPlayers: document.getElementById("lobby-players"),
  startBtn: document.getElementById("start-btn"),
  soloBtn: document.getElementById("solo-btn"),
  gameScreen: document.getElementById("game-screen"),
  turn: document.getElementById("turn"),
  dice: document.getElementById("dice"),
  rollCount: document.getElementById("roll-count"),
  rollBtn: document.getElementById("roll-btn"),
  newGameBtn: document.getElementById("new-game-btn"),
  scoreboard: document.getElementById("scoreboard"),
  message: document.getElementById("message"),
};

// sessionStorage er pr. fane, så to faner kan være to forskellige spillere
const store = {
  get(key) {
    try {
      return sessionStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key, value) {
    try {
      if (value === null) sessionStorage.removeItem(key);
      else sessionStorage.setItem(key, value);
    } catch {}
  },
};

// Hvem er "mig"? Spillets JSON har ingen id'er, men spillerne står i samme rækkefølge som i lobbyen.
let myId = store.get("playerId");
let myIndex = null;
let lobbyGame = false;

function setMyId(id) {
  myId = id;
  store.set("playerId", id);
}

function enterLobbyGame(lobby) {
  lobbyGame = true;
  store.set("solo", null);
  const i = lobby.players.findIndex((p) => p.id === myId);
  myIndex = i >= 0 ? i : null;
}

// I et lobbyspil må kun spilleren med tur kaste og vælge felt
function canAct(state) {
  return !lobbyGame || myIndex === state.currentPlayer;
}

// Hvilke af de 9 pladser (3x3) der har en prik for hver værdi.
const PIPS = {
  1: [4],
  2: [0, 8],
  3: [0, 4, 8],
  4: [0, 2, 6, 8],
  5: [0, 2, 4, 6, 8],
  6: [0, 2, 3, 5, 6, 8],
};

function createDice() {
  for (let i = 0; i < DICE_COUNT; i++) {
    const die = document.createElement("button");
    die.type = "button";
    die.className = "die";
    die.dataset.index = i;

    const face = document.createElement("span");
    face.className = "die-face";
    for (let p = 0; p < 9; p++) {
      const pip = document.createElement("span");
      pip.className = "pip";
      face.appendChild(pip);
    }

    const label = document.createElement("span");
    label.className = "die-label";

    die.append(face, label);
    die.addEventListener("click", () => onToggleHold(i));
    die.addEventListener("animationend", () => die.classList.remove("rolling"));
    el.dice.appendChild(die);
  }
}

function showPips(die, value) {
  const visible = PIPS[value] || [];
  die.querySelectorAll(".pip").forEach((pip, p) => pip.classList.toggle("on", visible.includes(p)));
}

function renderDie(die, index, value, held, canHold) {
  showPips(die, value);

  die.classList.toggle("held", held);
  die.classList.toggle("unrolled", !value);
  die.disabled = !canHold;
  die.setAttribute("aria-pressed", String(held));
  die.setAttribute("aria-label", `Terning ${index + 1}: ${value || "ikke slået"}${held ? ", holdt" : ""}`);
  die.querySelector(".die-label").textContent = held ? "HOLDT" : String(index + 1);
}

// Tegner hele spilskærmen ud fra tilstanden
let lastState = null;

function render(state) {
  const rolled = lastState && state.rollsUsed > lastState.rollsUsed;
  lastState = state;

  el.lobbyScreen.hidden = true;
  el.gameScreen.hidden = false;
  el.newGameBtn.hidden = lobbyGame;

  const player = state.players[state.currentPlayer];
  el.turn.textContent = `${possessive(player.name)} tur`;

  const myTurn = canAct(state);
  const canHold = myTurn && !state.turnCompleted && state.rollsUsed > 0 && state.rollsUsed < MAX_ROLLS;
  [...el.dice.children].forEach((die, i) => renderDie(die, i, state.dice[i], state.held[i], canHold));
  if (rolled) rollAnimation(state);

  el.rollCount.textContent = `Slag ${state.rollsUsed}/${MAX_ROLLS}`;
  el.rollBtn.disabled = !myTurn || state.turnCompleted || state.rollsUsed >= MAX_ROLLS;

  if (state.turnCompleted) {
    showMessage("Turen er afsluttet.");
  } else if (myTurn && state.rollsUsed >= MAX_ROLLS) {
    showMessage("Alle slag er brugt. Vælg et felt på pointtavlen.");
  }

  renderScoreboard(state, myTurn);
}

function cell(tag, text, className) {
  const c = document.createElement(tag);
  c.textContent = text;
  if (className) c.className = className;
  return c;
}

function renderScoreboard(state, myTurn) {
  const table = el.scoreboard;
  table.replaceChildren();

  const current = state.currentPlayer;
  const canScore = myTurn && !state.turnCompleted && state.rollsUsed > 0;
  const colClass = (i) => (i === current ? "current" : "");

  const head = table.createTHead().insertRow();
  head.append(cell("th", ""));
  state.players.forEach((p, i) => head.append(cell("th", p.name, colClass(i))));

  const body = table.createTBody();
  CATEGORIES.forEach(([id, label], row) => {
    const tr = body.insertRow();
    tr.append(cell("th", label));

    state.players.forEach((p, i) => {
      const td = cell("td", "", colClass(i));
      const saved = p.scorecard.scores[id];
      if (saved !== null) {
        td.textContent = saved;
      } else if (i === current && canScore) {
        td.append(scoreButton(id, label, state.possibleScores[id]));
      }
      tr.append(td);
    });

    // Sum og bonus står lige under 6'erne
    if (row === 5) {
      addSumRow(body, "Sum", state, (s) => s.upperSum);
      addSumRow(body, "Bonus", state, (s) => s.bonus);
    }
  });

  addSumRow(table.createTFoot(), "Total", state, (s) => s.total);
}

function addSumRow(section, label, state, value) {
  const tr = section.insertRow();
  tr.className = "sum";
  tr.append(cell("th", label));
  state.players.forEach((p, i) => {
    tr.append(cell("td", value(p.scorecard), i === state.currentPlayer ? "current" : ""));
  });
}

function scoreButton(category, label, points) {
  const btn = cell("button", points, points === 0 ? "score-option zero" : "score-option");
  btn.type = "button";
  btn.setAttribute("aria-label", `${label}: ${points} point`);
  btn.addEventListener("click", () => onChooseField(category, label, points));
  return btn;
}

function renderLobby(lobby) {
  el.gameScreen.hidden = true;
  el.lobbyScreen.hidden = false;

  const me = lobby.players.find((p) => p.id === myId);
  if (myId && !me) setMyId(null); // fx hvis serveren er genstartet

  el.joinForm.hidden = !!me;
  el.soloBtn.hidden = !!me;
  el.joined.hidden = !me;
  if (me) el.joined.textContent = `Du er med som ${me.name}. Spillet går i gang, når nogen trykker Start spil.`;

  el.lobbyCount.textContent = `Spillere (${lobby.players.length}/${MAX_PLAYERS})`;
  el.lobbyPlayers.replaceChildren();
  if (lobby.players.length === 0) {
    el.lobbyPlayers.append(cell("li", "Ingen endnu", "empty"));
  }
  lobby.players.forEach((p) => {
    const li = cell("li", "", p.id === myId ? "me" : "");
    li.append(
      cell("span", p.id === myId ? `${p.name} (dig)` : p.name),
      cell("span", p.connected ? "Forbundet" : "Ikke forbundet", p.connected ? "status" : "status offline"),
    );
    el.lobbyPlayers.append(li);
  });

  el.startBtn.disabled = lobby.players.length === 0 || lobby.players.some((p) => !p.connected);
}

// Dansk ejefald: "Frederiks", men "Andreas'" (navne der ender på s, x eller z).
function possessive(name) {
  return /[sxz]$/i.test(name) ? `${name}'` : `${name}s`;
}

function showMessage(text, isError = false) {
  el.message.textContent = text;
  el.message.classList.toggle("error", isError);
}

let busy = false; // så et dobbeltklik på Kast ikke bruger to slag
let actions = 0;

async function run(action) {
  if (busy) return;
  busy = true;
  actions++;
  try {
    showMessage("");
    const state = await action();
    if (state) render(state);
  } catch (err) {
    showMessage(err.message, true);
  } finally {
    busy = false;
  }
}

function onToggleHold(index) {
  run(() => api.toggleHold(index));
}

function onChooseField(category, label, points) {
  if (points === 0 && !confirm(`${label} giver 0 point. Vil du stryge feltet?`)) return;
  run(() => api.score(category));
}

el.rollBtn.addEventListener("click", () => {
  run(() => api.roll());
});

el.joinForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const name = el.nameInput.value.trim();
  const code = el.codeInput.value.trim();
  if (!name) return;

  run(async () => {
    const lobby = await api.join(name, code);
    // Svaret er lobbyen lige efter vores join, så vi er den sidste med navnet
    const mine = lobby.players.filter((p) => p.name === name).pop();
    setMyId(mine ? mine.id : null);
    renderLobby(lobby);
  });
});

el.startBtn.addEventListener("click", () => {
  run(async () => {
    const state = await api.startLobby();
    enterLobbyGame(await api.getLobby());
    return state;
  });
});

el.soloBtn.addEventListener("click", () => {
  if (!el.nameInput.reportValidity()) return;
  const name = el.nameInput.value.trim();
  if (!name) return;

  run(async () => {
    const state = await api.newGame(name);
    lobbyGame = false;
    myIndex = null;
    store.set("solo", "1");
    return state;
  });
});

el.newGameBtn.addEventListener("click", () => {
  showMessage("");
  store.set("solo", null);
  run(async () => {
    renderLobby(await api.getLobby());
  });
});

// Koden udfyldes, når Oplaen er forbundet med USB (bridge.js)
window.addEventListener("opla:pair", (e) => {
  el.codeInput.value = e.detail.code;
});

// Terninger der ikke holdes, ryster og viser tilfældige øjne, før de lander på serverens værdi.
let rollTimer = null;

function rollAnimation(state) {
  const dice = [...el.dice.children].filter((die, i) => !state.held[i]);
  dice.forEach((die) => {
    die.classList.remove("rolling");
    void die.offsetWidth; // genstart animationen
    die.classList.add("rolling");
  });

  clearInterval(rollTimer);
  let ticks = 0;
  rollTimer = setInterval(() => {
    ticks++;
    dice.forEach((die) => showPips(die, ticks < 6 ? 1 + Math.floor(Math.random() * 6) : state.dice[die.dataset.index]));
    if (ticks === 6) clearInterval(rollTimer);
  }, 80);
}

// Henter tilstanden hvert sekund: spillet når det er i gang, ellers lobbyen.
let polling = false;
let pollFailed = false;

async function poll() {
  if (busy || polling || document.hidden) return;
  polling = true;
  const before = actions;
  try {
    let state = null;
    let lobby = null;
    if (!el.gameScreen.hidden) {
      state = await api.getGame();
    } else {
      lobby = await api.getLobby();
      if (lobby.started) state = await api.getGame();
    }
    if (before !== actions) return; // et klik er sket imens, og dets svar er nyere

    if (pollFailed) {
      pollFailed = false;
      showMessage("");
    }
    if (lobby && lobby.started) enterLobbyGame(lobby);
    if (lobby && !lobby.started) renderLobby(lobby);
    else if (JSON.stringify(state) !== JSON.stringify(lastState) || !el.lobbyScreen.hidden) render(state);
  } catch (err) {
    if (before !== actions) return;
    pollFailed = true;
    showMessage(err.message, true);
  } finally {
    polling = false;
  }
}

async function load() {
  try {
    const lobby = await api.getLobby();
    if (lobby.started) {
      enterLobbyGame(lobby);
      render(await api.getGame());
      return;
    }
    if (store.get("solo")) {
      try {
        render(await api.getGame());
        return;
      } catch (err) {
        if (err.status !== 404) throw err;
      }
    }
    renderLobby(lobby);
  } catch (err) {
    showMessage(err.message, true);
  }
}

createDice();
load();
setInterval(poll, POLL_MS);
