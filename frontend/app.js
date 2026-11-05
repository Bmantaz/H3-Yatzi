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
};

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
    el.dice.appendChild(die);
  }
}

function renderDie(die, index, value, held, canHold) {
  const pips = die.querySelectorAll(".pip");
  const visible = PIPS[value] || [];
  pips.forEach((pip, p) => pip.classList.toggle("on", visible.includes(p)));

  die.classList.toggle("held", held);
  die.classList.toggle("unrolled", !value);
  die.disabled = !canHold;
  die.setAttribute("aria-pressed", String(held));
  die.setAttribute("aria-label", `Terning ${index + 1}: ${value || "ikke slået"}${held ? ", holdt" : ""}`);
  die.querySelector(".die-label").textContent = held ? "HOLDT" : String(index + 1);
}

// Tegner hele skærmen ud fra tilstanden
function render(state) {
  el.startScreen.hidden = true;
  el.gameScreen.hidden = false;

  const player = state.players[state.currentPlayer];
  el.turn.textContent = `${possessive(player.name)} tur`;

  const canHold = !state.turnCompleted && state.rollsUsed > 0 && state.rollsUsed < MAX_ROLLS;
  [...el.dice.children].forEach((die, i) => renderDie(die, i, state.dice[i], state.held[i], canHold));

  el.rollCount.textContent = `Slag ${state.rollsUsed}/${MAX_ROLLS}`;
  el.rollBtn.disabled = state.turnCompleted || state.rollsUsed >= MAX_ROLLS;

  if (state.turnCompleted) {
    showMessage("Turen er afsluttet.");
  } else if (state.rollsUsed >= MAX_ROLLS) {
    showMessage("Alle slag er brugt. Vælg et felt på pointtavlen.");
  }
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

async function run(action) {
  if (busy) return;
  busy = true;
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

el.rollBtn.addEventListener("click", () => {
  run(async () => {
    const state = await api.roll();
    rollAnimation();
    return state;
  });
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

// Kort ryste-effekt på de terninger der ikke holdes. Rigtig animation kommer i S2.7.
function rollAnimation() {
  el.dice.querySelectorAll(".die:not(.held)").forEach((die) => {
    die.classList.remove("rolling");
    void die.offsetWidth; // genstart animationen
    die.classList.add("rolling");
  });
}

createDice();

// 404 = intet spil endnu, så bliver startskærmen stående
api.getGame()
  .then(render)
  .catch((err) => {
    if (err.status !== 404) showMessage(err.message, true);
  });
