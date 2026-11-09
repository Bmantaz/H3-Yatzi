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

const MAX_ROLLS = 3;
const DICE_COUNT = 5;
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

const api = {
  getGame() {
    return request("/api/game");
  },

  newGame(name) {
    return request("/api/game/new", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    });
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
  startScreen: document.getElementById("start-screen"),
  startForm: document.getElementById("start-form"),
  nameInput: document.getElementById("player-name"),
  gameScreen: document.getElementById("game-screen"),
  turn: document.getElementById("turn"),
  dice: document.getElementById("dice"),
  rollCount: document.getElementById("roll-count"),
  rollBtn: document.getElementById("roll-btn"),
  newGameBtn: document.getElementById("new-game-btn"),
  scoreboard: document.getElementById("scoreboard"),
  message: document.getElementById("message"),
};

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

// Tegner hele skærmen ud fra tilstanden
let lastState = null;

function render(state) {
  const rolled = lastState && state.rollsUsed > lastState.rollsUsed;
  lastState = state;

  el.startScreen.hidden = true;
  el.gameScreen.hidden = false;

  const player = state.players[state.currentPlayer];
  el.turn.textContent = `${possessive(player.name)} tur`;

  const canHold = !state.turnCompleted && state.rollsUsed > 0 && state.rollsUsed < MAX_ROLLS;
  [...el.dice.children].forEach((die, i) => renderDie(die, i, state.dice[i], state.held[i], canHold));
  if (rolled) rollAnimation(state);

  el.rollCount.textContent = `Slag ${state.rollsUsed}/${MAX_ROLLS}`;
  el.rollBtn.disabled = state.turnCompleted || state.rollsUsed >= MAX_ROLLS;

  if (state.turnCompleted) {
    showMessage("Turen er afsluttet.");
  } else if (state.rollsUsed >= MAX_ROLLS) {
    showMessage("Alle slag er brugt. Vælg et felt på pointtavlen.");
  }

  renderScoreboard(state);
}

function cell(tag, text, className) {
  const c = document.createElement(tag);
  c.textContent = text;
  if (className) c.className = className;
  return c;
}

function renderScoreboard(state) {
  const table = el.scoreboard;
  table.replaceChildren();

  const current = state.currentPlayer;
  const canScore = !state.turnCompleted && state.rollsUsed > 0;
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
    render(await action());
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

el.startForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const name = el.nameInput.value.trim();
  if (!name) return;

  run(() => api.newGame(name));
});

el.newGameBtn.addEventListener("click", () => {
  showMessage("");
  el.gameScreen.hidden = true;
  el.startScreen.hidden = false;
  el.nameInput.focus();
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

// Henter tilstanden hvert sekund, så kast og hold fra Oplaen og de andre spillere vises.
let polling = false;
let pollFailed = false;

async function poll() {
  if (busy || polling || el.gameScreen.hidden || document.hidden) return;
  polling = true;
  const before = actions;
  try {
    const state = await api.getGame();
    if (before !== actions) return; // et klik er sket imens, og dets svar er nyere

    if (pollFailed) {
      pollFailed = false;
      showMessage("");
    }
    if (JSON.stringify(state) !== JSON.stringify(lastState)) render(state);
  } catch (err) {
    if (before !== actions) return;
    pollFailed = true;
    showMessage(err.message, true);
  } finally {
    polling = false;
  }
}

createDice();

// 404 = intet spil endnu, så bliver startskærmen stående
api.getGame()
  .then(render)
  .catch((err) => {
    if (err.status !== 404) showMessage(err.message, true);
  });

setInterval(poll, POLL_MS);
