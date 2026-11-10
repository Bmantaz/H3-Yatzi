// Adfærdstests af det uændrede browser-entrypoint med styrede svar og timere.
const test = require("node:test");
const assert = require("node:assert/strict");
const { fixture, deferred, state, lobby, ids, gameId, nextGameId } = require("./fixture.cjs");

async function ready(options) { const h = fixture(options); await h.idle(); return h; }
const member = (id = ids[0]) => ({ playerId: id });
const solo = { solo: "1" };

for (const mode of ["solo", "lobby"]) {
  for (const action of ["roll", "hold", "score", "strike"]) {
    test(`${mode}: ${action} sender eksplicit kontekst og viser accepteret tur`, async () => {
      const count = mode === "solo" ? 1 : 2;
      const before = state({ count, turnNumber: 7, rollsUsed: action === "roll" ? 0 : 1 });
      const after = state({ count, turnNumber: action === "score" || action === "strike" ? 8 : 7,
        rollsUsed: action === "roll" || action === "hold" ? 1 : 0, currentPlayer: mode === "lobby" && action.includes("s") ? 1 : 0 });
      const h = await ready({ game: before, initialLobby: mode === "solo" ? lobby(0, { started: false }) : lobby(2),
        storage: mode === "solo" ? solo : member(), fetchAction: req => req.method === "POST" ? { data: after } : undefined });
      if (action === "roll") await h.get("roll-btn").click();
      else if (action === "hold") await h.get("dice").children[4].click();
      else await h.field(action === "score" ? "Chance" : "1'ere").click();
      const request = assertSingle(h.posts());
      const expectedPath = { roll: "roll", hold: "hold/5", score: "score/chance", strike: "strike/ones" }[action];
      assert.equal(request.url, `/api/game/${expectedPath}`);
      assert.equal(request.options.headers["Content-Type"], "application/json");
      assert.deepEqual(request.body, { gameId, turnNumber: 7, playerId: mode === "solo" ? null : ids[0] });
      assert.deepEqual(h.read("lastState"), after);
      if (action === "score" || action === "strike") assert.equal(h.get("roll-count").textContent, "Slag 0/3");
    });
  }
}

test("0-point-dialog fastholder gammel knapkontekst og sender kun bekræftet strygning", async () => {
  const h = await ready({ game: state({ rollsUsed: 1 }), storage: solo,
    confirmAction: () => { assert.equal(h.get("roll-btn").disabled, true); return true; },
    fetchAction: req => req.method === "POST" ? { data: state({ turnNumber: 4 }) } : undefined });
  const oldButton = h.field("1'ere");
  h.server.game = state({ turnNumber: 3, rollsUsed: 1 });
  await h.tick();
  await oldButton.dispatch("click"); // Et allerede leveret klik må ikke få den nye turs nøgle.
  assert.deepEqual(assertSingle(h.posts()).body, { gameId, turnNumber: 1, playerId: null });
  assert.equal(h.posts()[0].url, "/api/game/strike/ones");
});

test("annulleret bekræftelse sender ingen POST", async () => {
  const h = await ready({ game: state({ rollsUsed: 1 }), storage: solo, confirmAction: () => false });
  await h.field("1'ere").click();
  assert.equal(h.posts().length, 0);
  assert.equal(h.get("roll-btn").disabled, false);
});

test("dobbeltklik låser knapper og bruger højst én POST mens svaret venter", async () => {
  const reply = deferred();
  const h = await ready({ game: state({ rollsUsed: 1 }), storage: solo,
    fetchAction: req => req.method === "POST" ? reply.promise : undefined });
  const first = h.get("roll-btn").click();
  assert.equal(h.get("roll-btn").disabled, true);
  assert.equal(h.get("new-game-btn").disabled, true);
  assert.ok(h.get("dice").children.every(die => die.disabled));
  assert.equal(h.get("scoreboard").querySelectorAll("button").length, 0);
  await h.get("roll-btn").dispatch("click");
  assert.equal(h.posts().length, 1);
  reply.resolve({ data: state({ rollsUsed: 2 }) });
  await first;
  assert.equal(h.get("roll-btn").disabled, false);
});

for (const started of [false, true]) {
  test(`join med ens navne bruger joinedPlayerId, også når started=${started}`, async () => {
    const reply = deferred();
    const h = await ready({ fetchAction: req => req.url === "/api/lobby/join" ? reply.promise : undefined });
    h.get("player-name").value = "Alex"; h.get("opla-code").value = "0000";
    const join = h.get("join-form").dispatch("submit");
    h.server.lobby = lobby(2, { started }); h.server.game = state({ count: 2 });
    reply.resolve({ data: { ...h.server.lobby, joinedPlayerId: ids[0] } });
    await join;
    assert.equal(h.stored.get("playerId"), ids[0]);
    assert.equal(h.get(started ? "game-screen" : "start-screen").hidden, false);
    assert.equal(h.get("join-form").hidden, !started);
    if (started) { assert.equal(h.read("myIndex"), 0); assert.equal(h.get("turn").textContent, "Din tur"); }
  });
}

for (const [role, id, current, disconnected] of [
  ["aktiv", ids[0], 0, false], ["reserve uden Oplà", ids[0], 0, true],
  ["ventende", ids[0], 1, false], ["tilskuer", null, 0, false],
]) {
  test(`${role} får korrekt adgang og turvisning efter genindlæsning`, async () => {
    const members = lobby(2); if (disconnected) members.players[0].connected = false;
    const h = await ready({ game: state({ count: 2, currentPlayer: current, rollsUsed: 1 }), initialLobby: members,
      storage: id ? { ...member(id), solo: "1" } : {} });
    const canAct = current === 0 && id !== null;
    assert.equal(h.get("roll-btn").disabled, !canAct);
    assert.ok(h.get("dice").children.every(die => die.disabled === !canAct));
    assert.equal(h.get("scoreboard").querySelectorAll("button").length, canAct ? 15 : 0);
    assert.equal(h.stored.has("solo"), false);
    assert.equal(h.get("turn").textContent, canAct ? "Din tur" : id ? "Alex' tur. Vent på din tur." : "Alex' tur (du ser med)");
  });
}

test("bonus, serverens vinderindeks og rangliste bevarer spilleridentitet og rækkefølge", async () => {
  const final = state({ count: 2, gameCompleted: true, currentPlayer: 1, winnerIndices: [1] });
  Object.assign(final.players[0].scorecard, { total: 80 });
  Object.assign(final.players[1].scorecard, { upperSum: 63, bonus: 50, total: 113 });
  const h = await ready({ game: final, initialLobby: lobby(2), storage: member(ids[1]) });
  assert.equal(h.get("play").hidden, true);
  assert.equal(h.get("game-over").hidden, false);
  assert.equal(h.get("winner").textContent, "Du vandt med 113 point!");
  assert.deepEqual(h.get("ranking").children.map(row => row.textContent), ["Alex113 point", "Alex80 point"]);
  assert.equal(h.get("ranking").children[0].className, "best");
  assert.deepEqual(h.read("lastState.players.map(p => p.scorecard.total)"), [80, 113]);
  assert.equal(h.get("end-new-game-btn").textContent, "Omkamp");
});

test("uafgjort med ens navne markerer alle serverens vinderindeks i deltagerrækkefølge", async () => {
  const final = state({ count: 3, gameCompleted: true, winnerIndices: [1, 2] });
  final.players[1].scorecard.total = 10; final.players[2].scorecard.total = 10;
  const h = await ready({ game: final, initialLobby: lobby(3), storage: member() });
  assert.equal(h.get("winner").textContent, "Uafgjort mellem Alex og Alex med 10 point");
  assert.equal(h.get("ranking").querySelectorAll(".best").length, 2);
  assert.deepEqual(h.read("lastState.winnerIndices"), [1, 2]);
  assert.deepEqual(h.read("lastState.players.map(p => p.scorecard.total)"), [0, 10, 10]);
});

test("serverens gameCompleted og winnerIndices er autoritative", async () => {
  // En kontraktprobe med afvigende tavleindhold afslører en genindført lokal slutberegning.
  const final = state({ gameCompleted: true }); final.players[0].scorecard.scores.ones = null;
  const h = await ready({ game: final, storage: solo });
  assert.equal(h.get("game-over").hidden, false);
  assert.equal(h.get("winner").textContent, "Alex fik 0 point");
});

test("omkamp fra sidste deltager bruger afsluttet gameId og viser frisk tur uden lobby", async () => {
  const final = state({ count: 2, gameCompleted: true });
  const fresh = state({ count: 2, gameId: nextGameId });
  const h = await ready({ game: final, initialLobby: lobby(2), storage: member(ids[1]),
    fetchAction: req => req.url === "/api/lobby/restart" ? { data: fresh } : undefined });
  await h.get("end-new-game-btn").click();
  assert.deepEqual(assertSingle(h.posts()).body, { gameId, playerId: ids[1] });
  assert.equal(h.get("start-screen").hidden, true);
  assert.equal(h.get("game-over").hidden, true);
  assert.equal(h.get("roll-count").textContent, "Slag 0/3");
  assert.deepEqual(h.read("lastState"), fresh);
  assert.equal(h.stored.get("playerId"), ids[1]);
});

test("slutskærmen poller forbindelser, deaktiverer omkamp og aktiverer efter genforbindelse", async () => {
  const h = await ready({ game: state({ count: 2, gameCompleted: true }), initialLobby: lobby(2), storage: member() });
  assert.equal(h.get("end-new-game-btn").disabled, false);
  h.server.lobby.players[1].connected = false;
  await h.tick();
  assert.equal(h.get("end-new-game-btn").disabled, true);
  assert.equal(h.get("message").textContent, "Alle deltagere skal være forbundet før omkamp.");
  await h.get("end-new-game-btn").click(); assert.equal(h.posts().length, 0);
  h.server.lobby.players[1].connected = true;
  await h.tick();
  assert.equal(h.get("end-new-game-btn").disabled, false);
  assert.equal(h.get("message").textContent, "");
});

test("tilskuer har ingen omkampsknap, men følger accepteret omkamp gennem polling", async () => {
  const h = await ready({ game: state({ count: 2, gameCompleted: true }), initialLobby: lobby(2) });
  assert.equal(h.get("end-new-game-btn").hidden, true);
  h.server.game = state({ count: 2, gameId: nextGameId });
  await h.tick();
  assert.equal(h.get("game-over").hidden, true);
  assert.equal(h.get("roll-btn").hidden, true);
  assert.equal(h.read("lastState.gameId"), nextGameId);
});

for (const rejected of ["samtidig omkamp", "frakobling efter klik"]) {
  test(`${rejected}: 409 vises gennem efterfølgende polling uden ny POST`, async () => {
    const message = rejected === "samtidig omkamp" ? "Spillet er ikke længere aktuelt. Hent spiltilstanden igen." : "Alle tilmeldte spillere skal være forbundet før omkamp.";
    const h = await ready({ game: state({ count: 2, gameCompleted: true }), initialLobby: lobby(2), storage: member(),
      fetchAction: (req, server) => {
        if (req.url !== "/api/lobby/restart") return;
        if (rejected === "samtidig omkamp") server.game = state({ count: 2, gameId: nextGameId });
        else server.lobby.players[1].connected = false;
        return { status: 409, data: { message } };
      } });
    await h.get("end-new-game-btn").click();
    assert.equal(h.posts().length, 1);
    assert.equal(h.get("message").textContent, message);
    assert.equal(h.get("message").className, "error");
    await h.tick();
    assert.equal(h.get("message").textContent, message);
    assert.equal(h.posts().length, 1);
    assert.equal(h.read("lastState.gameCompleted"), rejected !== "samtidig omkamp");
  });
}

test("enkeltspillerens Nyt spil går til lobby og opretter næste spil gennem /new", async () => {
  const h = await ready({ game: state({ gameCompleted: true }), storage: solo,
    fetchAction: req => req.url === "/api/game/new" ? { data: state({ gameId: nextGameId }) } : undefined });
  await h.get("end-new-game-btn").click();
  assert.equal(h.get("start-screen").hidden, false);
  assert.equal(h.stored.has("solo"), false);
  h.get("player-name").value = "Alex";
  await h.get("solo-btn").click();
  assert.equal(assertSingle(h.posts()).url, "/api/game/new");
  assert.deepEqual(h.posts()[0].body, { name: "Alex" });
  assert.equal(h.read("lastState.gameId"), nextGameId);
  assert.equal(h.stored.get("solo"), "1");
});

test("første start bruger uændret bodyløs rute og opdaterer identiteten før spilvisning", async () => {
  const h = await ready({ initialLobby: lobby(2, { started: false }), storage: member(ids[1]),
    fetchAction: (req, server) => {
      if (req.url !== "/api/lobby/start") return;
      server.lobby.started = true; server.game = state({ count: 2 }); return { data: server.game };
    } });
  await h.get("start-btn").click();
  const post = assertSingle(h.posts()); assert.equal(post.body, null);
  assert.equal(h.read("myIndex"), 1);
  assert.equal(h.get("roll-btn").disabled, true);
  assert.equal(h.get("game-screen").hidden, false);
});

test("en gammel indledende læsning kan ikke overskrive en ny brugerhandling", async () => {
  const old = deferred(); let reads = 0;
  const h = fixture({ fetchAction: (req, server) => {
    if (req.url === "/api/lobby" && ++reads === 1) return old.promise;
    if (req.url === "/api/game/new") { server.game = state({ gameId: nextGameId }); return { data: server.game }; }
  } });
  h.get("player-name").value = "Alex";
  await h.get("solo-btn").click();
  old.resolve({ data: lobby(0, { started: false }) });
  await h.idle();
  assert.equal(h.read("lastState.gameId"), nextGameId);
  assert.equal(h.get("game-screen").hidden, false);
  assert.equal(h.stored.get("solo"), "1");
});

test("et gammelt poll kan ikke genåbne det afsluttede spil efter omkamp", async () => {
  const old = deferred(); let block = false;
  const final = state({ count: 2, gameCompleted: true });
  const h = await ready({ game: final, initialLobby: lobby(2), storage: member(), fetchAction: req => {
    if (req.url === "/api/game" && block) return old.promise;
    if (req.url === "/api/lobby/restart") return { data: state({ count: 2, gameId: nextGameId }) };
  } });
  block = true; const polling = h.refresh(); await h.idle();
  await h.get("end-new-game-btn").click();
  old.resolve({ data: final }); await polling;
  assert.equal(h.read("lastState.gameId"), nextGameId);
  assert.equal(h.get("game-over").hidden, true);
});

test("lobby og spil fra samme læsning anvendes samlet, og polling overlapper ikke", async () => {
  const pending = deferred(); let block = false;
  const h = await ready({ game: state({ count: 2, gameCompleted: true }), initialLobby: lobby(2), storage: member(),
    fetchAction: req => req.url === "/api/game" && block ? pending.promise : undefined });
  h.server.lobby.players[1].connected = false; block = true;
  const before = h.requests.length;
  const a = h.refresh(); const b = h.refresh(); assert.equal(a, b);
  await h.idle();
  assert.equal(h.requests.length - before, 2);
  assert.equal(h.get("end-new-game-btn").disabled, false);
  pending.resolve({ data: h.server.game }); await a;
  assert.equal(h.get("end-new-game-btn").disabled, true);
  h.document.hidden = true;
  const requests = h.requests.length; await h.tick(); assert.equal(h.requests.length, requests);
});

test("lavere turnummer i samme spil afvises, og nyt gameId starter på tur 1", async () => {
  const h = await ready({ game: state({ turnNumber: 5 }), storage: solo });
  h.server.game = state({ turnNumber: 3 }); await h.tick();
  assert.equal(h.read("lastState.turnNumber"), 5);
  h.server.game = state({ gameId: nextGameId }); await h.tick();
  assert.equal(h.read("lastState.turnNumber"), 1);
  assert.equal(h.read("lastState.gameId"), nextGameId);
});

for (const transition of ["tur", "spil", "slut", "lobby"]) {
  test(`gammel animation tegner ikke efter skift til ${transition}`, async () => {
    const h = await ready({ game: state({ rollsUsed: 1 }), storage: solo });
    h.server.game = state({ rollsUsed: 2, dice: Array(5).fill(6) }); await h.tick();
    const callback = assertSingle([...h.timers.values()].filter(timer => timer.ms === 80)).callback;
    if (transition === "tur") h.server.game = state({ turnNumber: 2 });
    if (transition === "spil") h.server.game = state({ gameId: nextGameId });
    if (transition === "slut") h.server.game = state({ gameCompleted: true, rollsUsed: 2, dice: Array(5).fill(2) });
    if (transition === "lobby") h.stored.delete("solo");
    await h.tick();
    const faces = h.get("dice").children.map(die => die.querySelectorAll(".pip").filter(pip => pip.classList.contains("on")).length);
    callback(); // En allerede planlagt callback skal også være uskadelig.
    assert.deepEqual(h.get("dice").children.map(die => die.querySelectorAll(".pip").filter(pip => pip.classList.contains("on")).length), faces);
    assert.equal([...h.timers.values()].some(timer => timer.ms === 80), false);
    assert.ok(h.get("dice").children.every(die => !die.classList.contains("rolling")));
  });
}

test("animation lander på serverens terninger, og hold stopper gammel animation", async () => {
  const h = await ready({ game: state({ rollsUsed: 1 }), storage: solo });
  h.server.game = state({ rollsUsed: 2, dice: Array(5).fill(4) }); await h.tick();
  for (let i = 0; i < 6; i++) await h.tick(80);
  assert.ok(h.get("dice").children.every(die => die.querySelectorAll(".pip").filter(p => p.classList.contains("on")).length === 4));
  h.server.game = state({ rollsUsed: 3 }); await h.tick();
  h.server.game = state({ rollsUsed: 3, held: [true, false, false, false, false] }); await h.tick();
  assert.equal([...h.timers.values()].some(timer => timer.ms === 80), false);
  assert.equal(h.get("dice").children[0].attributes["aria-pressed"], "true");
});

test("netværksfejl gentager ingen POST og bevarer kendt tilstand gennem genoprettelse", async () => {
  let offline = false;
  const h = await ready({ game: state(), storage: solo, fetchAction: req => {
    if (offline || req.method === "POST") throw new TypeError("offline");
  } });
  const before = h.read("lastState");
  await h.get("roll-btn").click();
  assert.equal(h.get("message").textContent, "Ingen forbindelse til serveren.");
  offline = true; await h.tick(); assert.deepEqual(h.read("lastState"), before);
  offline = false; h.server.game = state({ rollsUsed: 1 }); await h.tick();
  assert.equal(h.read("lastState.rollsUsed"), 1);
  assert.equal(h.posts().length, 1);
  assert.equal(h.get("message").textContent, "Ingen forbindelse til serveren.");
});

test("servergenstart rydder ugyldigt id og manglende enkeltspillerspil", async () => {
  const h = await ready({ game: state({ count: 2 }), initialLobby: lobby(2), storage: member() });
  h.server.game = null; h.server.lobby = lobby(0, { started: false }); await h.tick();
  assert.equal(h.stored.has("playerId"), false); assert.equal(h.get("start-screen").hidden, false);
  assert.equal(h.read("lastState"), null);
  const missing = await ready({ storage: { ...solo, ...member() } });
  assert.equal(missing.stored.has("solo"), false); assert.equal(missing.stored.has("playerId"), false);
  assert.equal(missing.get("start-screen").hidden, false);
});

test("404 mellem lobby- og spilopslag genlæser lobbyen efter servergenstart", async () => {
  let reset = false;
  const h = await ready({ game: state({ count: 2 }), initialLobby: lobby(2), storage: member(), fetchAction: (req, server) => {
    if (reset && req.url === "/api/game") { server.lobby = lobby(0, { started: false }); return { status: 404, data: { message: "Intet spil." } }; }
  } });
  reset = true; await h.tick();
  assert.equal(h.stored.has("playerId"), false);
  assert.equal(h.get("start-screen").hidden, false);
});

function assertSingle(items) { assert.equal(items.length, 1); return items[0]; }
