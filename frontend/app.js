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
//   turnCompleted:  false,
//   gameId:         "serverens GUID",
//   turnNumber:     1,
//   gameCompleted:  false,
//   winnerIndices:  []
// }
//
// Lobbyen fra /api/lobby (se docs/lobby.md):
// { players: [{ id, name, connected }], started: false }
// Join tilføjer joinedPlayerId. Frontenden viser serverens resultat og fastholder
// spil-/turkonteksten ved klik. bridge.js videresender Oplà-protokollen uafhængigt.

const MAX_ROLLS = 3;
const DICE_COUNT = 5;
const MAX_PLAYERS = 6;
const POLL_MS = 1000;

async function request(path, options) {
  let res;
  try {
    res = await fetch(path, options);
  } catch (err) {
    if (err.name === "AbortError") throw err;
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
  getGame(signal) {
    return request("/api/game", { signal });
  },

  newGame(name) {
    return postJson("/api/game/new", { name });
  },

  roll(context) {
    return postJson("/api/game/roll", context);
  },

  // API'et tæller terninger 1-5
  toggleHold(index, context) {
    return postJson(`/api/game/hold/${index + 1}`, context);
  },

  score(category, context) {
    return postJson(`/api/game/score/${category}`, context);
  },

  strike(category, context) {
    return postJson(`/api/game/strike/${category}`, context);
  },

  getLobby(signal) {
    return request("/api/lobby", { signal });
  },

  join(name, code) {
    return postJson("/api/lobby/join", { name, code });
  },

  startLobby() {
    return request("/api/lobby/start", { method: "POST" });
  },

  restart(context) {
    return postJson("/api/lobby/restart", context);
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
  lobbyScreen: document.getElementById("start-screen"),
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
  play: document.getElementById("play"),
  hint: document.getElementById("hint"),
  gameOver: document.getElementById("game-over"),
  winner: document.getElementById("winner"),
  ranking: document.getElementById("ranking"),
  endNewGameBtn: document.getElementById("end-new-game-btn"),
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
let lastLobby = null;
let lastState = null;
let busy = false;
let actions = 0;
let actionError = "";
let pollError = "";
let reading = null;

function setMyId(id) {
  myId = id;
  store.set("playerId", id);
}

// Kaldes først, når en hel læsning er accepteret. Et gammelt poll må ikke rydde
// det id, et nyere join netop har gemt, eller ændre browserens spilform.
function applyLobby(lobby) {
  lastLobby = lobby;
  const i = lobby.players.findIndex((p) => p.id === myId);
  if (myId && i < 0) setMyId(null);
  lobbyGame = lobby.started;
  myIndex = lobbyGame && i >= 0 ? i : null;
  if (lobbyGame) store.set("solo", null);
}

// I et lobbyspil må kun spilleren med tur kaste og vælge felt
function canAct(state) {
  return !lobbyGame || myIndex === state.currentPlayer;
}

function actionContext(state) {
  return Object.freeze({ gameId: state.gameId, turnNumber: state.turnNumber, playerId: lobbyGame ? myId : null });
}

function allConnected() {
  return lastLobby?.players.length > 0 && lastLobby.players.every((p) => p.connected);
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

// Tegner kun accepterede øjebliksbilleder. GUID'er er identiteter, ikke en tidsrækkefølge.
function render(state) {
  if (lastState?.gameId === state.gameId && state.turnNumber < lastState.turnNumber) return;
  const sameTurn = lastState?.gameId === state.gameId && lastState.turnNumber === state.turnNumber;
  const rolled = sameTurn && state.rollsUsed > lastState.rollsUsed;
  stopRollAnimation();
  lastState = state;

  el.lobbyScreen.hidden = true;
  el.gameScreen.hidden = false;
  el.newGameBtn.hidden = lobbyGame;

  const over = state.gameCompleted;
  const myTurn = !over && canAct(state);
  renderTurn(state, myTurn, over);

  el.play.hidden = over;
  el.gameOver.hidden = !over;
  if (over) renderGameOver(state);

  updateControls();
  if (rolled && !over) rollAnimation(state);
  updateMessage();
}

function renderTurn(state, myTurn, over) {
  const name = state.players[state.currentPlayer].name;
  let text;
  if (over) text = "Spillet er slut";
  else if (!lobbyGame) text = `${possessive(name)} tur`;
  else if (myTurn) text = "Din tur";
  else if (myIndex !== null) text = `${possessive(name)} tur. Vent på din tur.`;
  else text = `${possessive(name)} tur (du ser med)`;

  el.turn.textContent = text;
  el.turn.classList.toggle("mine", lobbyGame && myTurn);
  document.title = lobbyGame && myTurn ? "Din tur · H3 Yatzi" : "H3 Yatzi";
}

function namesText(names) {
  return names.length > 1 ? `${names.slice(0, -1).join(", ")} og ${names[names.length - 1]}` : names[0];
}

function renderGameOver(state) {
  const winners = state.winnerIndices.map((index) => state.players[index]);
  const best = winners[0].scorecard.total;
  const iWon = lobbyGame && state.winnerIndices.includes(myIndex);

  if (state.players.length === 1) {
    el.winner.textContent = `${state.players[0].name} fik ${best} point`;
  } else if (winners.length > 1) {
    el.winner.textContent = `Uafgjort mellem ${namesText(winners.map((p) => p.name))} med ${best} point`;
  } else if (iWon) {
    el.winner.textContent = `Du vandt med ${best} point!`;
  } else {
    el.winner.textContent = `${winners[0].name} vandt med ${best} point!`;
  }

  el.ranking.replaceChildren();
  state.players.map((player, index) => ({ player, index }))
    .sort((a, b) => b.player.scorecard.total - a.player.scorecard.total || a.index - b.index)
    .forEach(({ player: p, index }) => {
      const li = cell("li", "", state.winnerIndices.includes(index) ? "best" : "");
      li.append(cell("span", p.name), cell("span", `${p.scorecard.total} point`));
      el.ranking.append(li);
    });
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
  state.players.forEach((p, i) => {
    const me = lobbyGame && i === myIndex;
    head.append(cell("th", me ? `${p.name} (dig)` : p.name, colClass(i)));
  });

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
        td.append(scoreButton(id, label, state.possibleScores[id], actionContext(state)));
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

function scoreButton(category, label, points, context) {
  const btn = cell("button", points, points === 0 ? "score-option zero" : "score-option");
  btn.type = "button";
  btn.setAttribute("aria-label", `${label}: ${points} point`);
  btn.addEventListener("click", () => onChooseField(category, label, points, context));
  return btn;
}

function renderLobby(lobby) {
  stopRollAnimation();
  lastState = null;
  document.title = "H3 Yatzi";
  el.gameScreen.hidden = true;
  el.lobbyScreen.hidden = false;

  const me = lobby.players.find((p) => p.id === myId);

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

  updateControls();
  updateMessage();
}

// Dansk ejefald: "Frederiks", men "Andreas'" (navne der ender på s, x eller z).
function possessive(name) {
  return /[sxz]$/i.test(name) ? `${name}'` : `${name}s`;
}

function showMessage(text, isError = false) {
  el.message.textContent = text;
  el.message.classList.toggle("error", isError);
}

// Visuel lås og adgang følger de samme regler som klikhandlerne. Terningerne
// forbliver synlige for ventende spillere og tilskuere, men kan ikke betjenes.
function updateControls() {
  el.joinForm.querySelector("button").disabled = busy;
  el.nameInput.disabled = busy;
  el.codeInput.disabled = busy;
  el.soloBtn.disabled = busy;
  el.startBtn.disabled = busy || !lastLobby || lastLobby.started || !allConnected();
  el.newGameBtn.disabled = busy;
  if (!lastState || el.gameScreen.hidden) return;
  const state = lastState;
  const myTurn = !state.gameCompleted && canAct(state);
  const canHold = !busy && myTurn && !state.turnCompleted && state.rollsUsed > 0 && state.rollsUsed < MAX_ROLLS;
  [...el.dice.children].forEach((die, i) => renderDie(die, i, state.dice[i], state.held[i], canHold));
  el.rollCount.textContent = `Slag ${state.rollsUsed}/${MAX_ROLLS}`;
  el.rollBtn.hidden = lobbyGame && myIndex === null;
  el.rollBtn.disabled = busy || !myTurn || state.turnCompleted || state.rollsUsed >= MAX_ROLLS;
  el.hint.hidden = !myTurn;
  el.endNewGameBtn.textContent = lobbyGame ? "Omkamp" : "Nyt spil";
  el.endNewGameBtn.hidden = lobbyGame && myIndex === null;
  el.endNewGameBtn.disabled = busy || (lobbyGame && !allConnected());
  renderScoreboard(state, myTurn && !busy);
}

// Handlingsfejl lever videre gennem polling. Almindelige råd og forbindelsesfejl
// må ikke erstatte en afvist omkamps konkrete danske forklaring.
function updateMessage() {
  if (actionError || pollError) return showMessage(actionError || pollError, true);
  if (lastState?.gameCompleted && lobbyGame && myIndex !== null && !allConnected())
    return showMessage("Alle deltagere skal være forbundet før omkamp.");
  if (lastState && !lastState.gameCompleted && canAct(lastState) && lastState.rollsUsed >= MAX_ROLLS)
    return showMessage("Alle slag er brugt. Vælg et felt på pointtavlen.");
  showMessage("");
}

async function run(action) {
  if (busy) return;
  busy = true;
  actions++;
  reading?.controller.abort();
  actionError = "";
  updateControls();
  updateMessage();
  try {
    const state = await action();
    if (state) render(state);
  } catch (err) {
    actionError = err.message;
    if (err.status === 409 || err.status === 404) await refresh(true);
  } finally {
    busy = false;
    updateControls();
    updateMessage();
  }
}

function onToggleHold(index) {
  if (!lastState || !canAct(lastState) || lastState.gameCompleted || lastState.rollsUsed === 0 || lastState.rollsUsed >= MAX_ROLLS) return;
  const context = actionContext(lastState);
  return run(() => api.toggleHold(index, context));
}

function onChooseField(category, label, points, context) {
  if (!lastState || !canAct(lastState) || lastState.gameCompleted || lastState.rollsUsed === 0) return;
  // Busy sættes før dialogen. Konteksten kommer fra feltknappens viste tur,
  // også hvis et andet input afslutter turen, mens bekræftelsen er åben.
  return run(() => {
    if (points === 0) {
      if (!confirm(`${label} giver 0 point. Vil du stryge feltet?`)) return;
      return api.strike(category, context);
    }
    return api.score(category, context);
  });
}

el.rollBtn.addEventListener("click", () => {
  if (!lastState || !canAct(lastState) || lastState.gameCompleted || lastState.rollsUsed >= MAX_ROLLS) return;
  const context = actionContext(lastState);
  return run(() => api.roll(context));
});

el.joinForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const name = el.nameInput.value.trim();
  const code = el.codeInput.value.trim();
  if (!name) return;

  return run(async () => {
    const lobby = await api.join(name, code);
    setMyId(lobby.joinedPlayerId);
    applyLobby(lobby);
    if (!lobby.started) renderLobby(lobby);
    await refresh(true);
  });
});

el.startBtn.addEventListener("click", () => {
  return run(async () => {
    await api.startLobby();
    await refresh(true);
  });
});

el.soloBtn.addEventListener("click", () => {
  if (!el.nameInput.reportValidity()) return;
  const name = el.nameInput.value.trim();
  if (!name) return;

  return run(async () => {
    const state = await api.newGame(name);
    lobbyGame = false;
    myIndex = null;
    store.set("solo", "1");
    return state;
  });
});

function backToLobby() {
  return run(async () => {
    store.set("solo", null);
    await refresh(true);
  });
}

el.newGameBtn.addEventListener("click", backToLobby);
el.endNewGameBtn.addEventListener("click", () => {
  if (!lobbyGame) return backToLobby();
  if (!lastState?.gameCompleted || myIndex === null || !allConnected()) return;
  const context = Object.freeze({ gameId: lastState.gameId, playerId: myId });
  return run(() => api.restart(context));
});

// Koden udfyldes, når Oplaen er forbundet med USB (bridge.js)
window.addEventListener("opla:pair", (e) => {
  el.codeInput.value = e.detail.code;
});

// Terninger der ikke holdes, ryster og viser tilfældige øjne, før de lander på serverens værdi.
let rollTimer = null;

function stopRollAnimation() {
  clearInterval(rollTimer);
  rollTimer = null;
  [...el.dice.children].forEach((die) => die.classList.remove("rolling"));
}

function rollAnimation(state) {
  const dice = [...el.dice.children].filter((die, i) => !state.held[i]);
  dice.forEach((die) => {
    die.classList.remove("rolling");
    void die.offsetWidth; // genstart animationen
    die.classList.add("rolling");
  });

  let ticks = 0;
  rollTimer = setInterval(() => {
    // Selv en callback, som allerede var klar, må ikke tegne en gammel tur.
    if (lastState !== state || el.gameScreen.hidden) return;
    ticks++;
    dice.forEach((die) => showPips(die, ticks < 6 ? 1 + Math.floor(Math.random() * 6) : state.dice[die.dataset.index]));
    if (ticks === 6) stopRollAnimation();
  }, 80);
}

// Fælles læsning for opstart, polling og konflikter. En hel læsning accepteres
// først efter begge svar. Generationen beskytter også mod fetch, som ignorerer abort.
function refresh(force = false) {
  if (!force && (busy || document.hidden)) return Promise.resolve();
  if (reading?.generation === actions) return reading.promise;
  reading?.controller.abort();
  const operation = { generation: actions, controller: new AbortController() };
  reading = operation;
  const current = () => reading === operation && operation.generation === actions;
  operation.promise = (async () => {
    try {
      const signal = operation.controller.signal;
      let lobby = await api.getLobby(signal);
      let state = null;
      let missingGame = false;
      if (lobby.started || store.get("solo")) {
        try { state = await api.getGame(signal); }
        catch (err) {
          if (err.status !== 404) throw err;
          // Serveren kan være genstartet mellem lobby- og spilopslaget.
          lobby = await api.getLobby(signal);
          if (lobby.started) throw err;
          missingGame = true;
        }
      }
      if (!current()) return;
      if (state && lastState?.gameId === state.gameId && state.turnNumber < lastState.turnNumber) return;
      if (missingGame) store.set("solo", null);
      applyLobby(lobby);
      pollError = "";
      if (state) render(state); else renderLobby(lobby);
    } catch (err) {
      if (!current() || err.name === "AbortError") return;
      pollError = err.message;
      updateMessage();
    } finally {
      if (reading === operation) reading = null;
    }
  })();
  return operation.promise;
}

createDice();
updateControls();
refresh();
setInterval(() => refresh(), POLL_MS);
