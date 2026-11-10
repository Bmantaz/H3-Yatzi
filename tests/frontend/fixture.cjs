// Lille DOM-/netværksfixture til app.js. Ingen browser- eller produktionsafhængigheder.
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const categories = ["ones", "twos", "threes", "fours", "fives", "sixes", "onePair", "twoPairs", "threeOfAKind",
  "fourOfAKind", "smallStraight", "largeStraight", "fullHouse", "chance", "yatzy"];
const ids = Array.from({ length: 6 }, (_, i) => {
  const digit = String(i + 1);
  return `${digit.repeat(8)}-${digit.repeat(4)}-4${digit.repeat(3)}-8${digit.repeat(3)}-${digit.repeat(12)}`;
});
const gameId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const nextGameId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

function state(options = {}) {
  const { count = 1, ...overrides } = options;
  const { rollsUsed = 0, gameCompleted = false } = overrides;
  return {
    players: Array.from({ length: count }, () => ({ name: "Alex", scorecard: {
      scores: Object.fromEntries(categories.map(id => [id, gameCompleted ? 0 : null])), upperSum: 0, bonus: 0, total: 0,
    } })),
    currentPlayer: 0, dice: Array(5).fill(rollsUsed ? 1 : 0), held: Array(5).fill(false), rollsUsed,
    possibleScores: rollsUsed && !gameCompleted ? Object.fromEntries(categories.map(id => [id, id === "chance" ? 5 : 0])) : {},
    turnCompleted: gameCompleted, gameId, turnNumber: gameCompleted ? 15 * count : 1, gameCompleted,
    winnerIndices: gameCompleted ? Array.from({ length: count }, (_, i) => i) : [], ...overrides,
  };
}

function lobby(count = 1, options = {}) {
  return { players: Array.from({ length: count }, (_, i) => ({ id: ids[i], name: "Alex", connected: true })), started: true, ...options };
}

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

class Element {
  constructor(tag = "div") {
    this.tagName = tag; this.children = []; this.dataset = {}; this.attributes = {}; this.listeners = new Map();
    this.className = ""; this.hidden = false; this.disabled = false; this.value = ""; this._text = "";
    this.classList = {
      contains: name => this.className.split(/\s+/).includes(name),
      toggle: (name, enabled) => {
        const names = new Set(this.className.split(/\s+/).filter(Boolean));
        if (enabled ?? !names.has(name)) names.add(name); else names.delete(name);
        this.className = [...names].join(" ");
      },
      add: name => this.classList.toggle(name, true),
      remove: name => this.classList.toggle(name, false),
    };
  }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(""); }
  append(...children) { this.children.push(...children); }
  appendChild(child) { this.append(child); return child; }
  replaceChildren(...children) { this._text = ""; this.children = [...children]; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(event, handler) {
    if (!this.listeners.has(event)) this.listeners.set(event, []);
    this.listeners.get(event).push(handler);
  }
  async dispatch(event, extra = {}) {
    const results = (this.listeners.get(event) || []).map(handler => handler({ preventDefault() {}, ...extra }));
    await Promise.all(results);
  }
  async click() { if (!this.disabled && !this.hidden) await this.dispatch("click"); }
  reportValidity() { return this.value.trim().length > 0; }
  querySelectorAll(selector) {
    const matches = child => selector.startsWith(".") ? child.classList.contains(selector.slice(1)) : child.tagName === selector;
    return this.children.flatMap(child => [...(matches(child) ? [child] : []), ...child.querySelectorAll(selector)]);
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  insertRow() { return this.appendChild(new Element("tr")); }
  createTHead() { return this.appendChild(new Element("thead")); }
  createTBody() { return this.appendChild(new Element("tbody")); }
  createTFoot() { return this.appendChild(new Element("tfoot")); }
}

function fixture({ game = null, initialLobby = lobby(0, { started: false }), storage = {}, fetchAction, confirmAction } = {}) {
  const server = { game, lobby: initialLobby };
  const requests = [], elements = new Map(), timers = new Map(), stored = new Map(Object.entries(storage));
  const html = fs.readFileSync(path.join(__dirname, "../../frontend/index.html"), "utf8");
  for (const match of html.matchAll(/<([a-z]+)[^>]*\bid="([^"]+)"([^>]*)>/g)) {
    const element = new Element(match[1]); element.hidden = /\bhidden\b/.test(match[3]); elements.set(match[2], element);
  }
  elements.get("join-form").appendChild(new Element("button"));
  let timerId = 0;
  const document = { hidden: false, title: "H3 Yatzi", createElement: tag => new Element(tag), getElementById: id => elements.get(id) };
  const window = new Element();
  const context = vm.createContext({
    document, window, AbortController, console,
    sessionStorage: { getItem: key => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, String(value)), removeItem: key => stored.delete(key) },
    confirm: message => confirmAction ? confirmAction(message) : true,
    setInterval: (callback, ms) => { const id = ++timerId; timers.set(id, { callback, ms }); return id; },
    clearInterval: id => timers.delete(id),
    fetch: async (url, options = {}) => {
      const req = { url, method: options.method || "GET", body: options.body ? JSON.parse(options.body) : null, options };
      requests.push(req);
      let response = fetchAction ? await fetchAction(req, server) : undefined;
      if (!response) {
        if (url === "/api/lobby" && req.method === "GET") response = { data: server.lobby };
        else if (url === "/api/game" && req.method === "GET") response = server.game ? { data: server.game } : { status: 404, data: { message: "Intet spil." } };
        else throw new Error(`Uventet request: ${req.method} ${url}`);
      }
      const status = response.status || 200;
      const data = structuredClone(response.data);
      return { ok: status >= 200 && status < 300, status, json: async () => data };
    },
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../frontend/app.js"), "utf8"), context, { filename: "app.js" });
  const evaluate = code => vm.runInContext(code, context);
  return {
    server, requests, elements, timers, stored, document, window, evaluate,
    get: id => elements.get(id),
    posts: () => requests.filter(req => req.method === "POST"),
    read: code => JSON.parse(JSON.stringify(evaluate(code))),
    idle: () => new Promise(resolve => setImmediate(resolve)),
    refresh: () => evaluate("refresh()"),
    async tick(ms = 1000) { for (const timer of [...timers.values()]) if (timer.ms === ms) timer.callback(); await new Promise(resolve => setImmediate(resolve)); },
    field(label) { return elements.get("scoreboard").querySelectorAll("button").find(button => button.attributes["aria-label"].startsWith(label + ":")); },
  };
}

module.exports = { fixture, deferred, state, lobby, ids, gameId, nextGameId, categories };
