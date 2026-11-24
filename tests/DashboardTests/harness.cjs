"use strict";
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
class Element {
  constructor(tag = "div") {
    this.tagName = tag.toUpperCase(); this.children = []; this.listeners = {}; this.dataset = {};
    this.attributes = {}; this._text = ""; this._value = undefined; this.hidden = false;
    this.disabled = false; this.className = ""; this.classList = { add: value => { this.className += " " + value; } };
  }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(x => x.textContent).join(""); }
  set innerHTML(_) { throw new Error("Unsafe HTML assignment"); }
  set value(value) { this._value = String(value); }
  get value() { return this._value ?? (this.tagName === "SELECT" ? this.children[0]?.value || "" : ""); }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this._text = ""; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, handler) { (this.listeners[name] ||= []).push(handler); }
  dispatch(name) { for (const fn of this.listeners[name] || []) fn({ preventDefault() {}, target: this }); }
  all() { return this.children.flatMap(x => [x, ...x.all()]); }
  querySelector(selector) { return this.all().find(x => x.tagName.toLowerCase() === selector); }
}
function boot(reply) {
  const root = path.resolve(__dirname, "../../dashboard");
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const ids = new Map();
  for (const match of html.matchAll(/<([a-z]+)\b[^>]*\bid="([^"]+)"[^>]*>/g)) {
    const element = new Element(match[1]); element.id = match[2]; ids.set(match[2], element);
  }
  ids.get("gaps-form").append(new Element("button"));
  ids.get("auto-refresh").checked = true;
  const nav = ["overview", "events", "processes", "network", "activity", "storage"].map(name => {
    const button = new Element("button"); button.dataset.panel = name; return button;
  });
  const timers = new Map(); let timerId = 0;
  const document = {
    hidden: false, listeners: {}, getElementById: id => ids.get(id),
    createElement: tag => new Element(tag),
    addEventListener(name, handler) { (this.listeners[name] ||= []).push(handler); },
    querySelectorAll(selector) {
      if (selector === "nav [data-panel]") return nav;
      if (selector === ".panel") return nav.map(x => ids.get(x.dataset.panel));
      if (selector === "[data-needs-connection]") return ["overview-refresh", "storage-refresh"].map(id => ids.get(id));
      return [];
    }
  };
  const calls = [];
  class FormData {
    constructor(form) { this.values = form.all().filter(x => x.name).map(x => [x.name, x.value]); }
    [Symbol.iterator]() { return this.values[Symbol.iterator](); }
  }
  const context = vm.createContext({
    document, Node: Element, URL, URLSearchParams, AbortController, FormData,
    location: { origin: "http://127.0.0.1:8769" },
    window: { addEventListener() {} },
    fetch: async (url, options) => {
      const call = { url: String(url), options }; calls.push(call);
      return reply(call, calls.length);
    },
    setTimeout(fn, delay) { const id = ++timerId; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); }
  });
  vm.runInContext(fs.readFileSync(path.join(root, "model.js"), "utf8"), context);
  vm.runInContext(fs.readFileSync(path.join(root, "app.js"), "utf8"), context);
  const findForm = name => ids.get(name + "-client").all().find(x => x.tagName === "FORM");
  const control = (name, field) => findForm(name).all().find(x => x.name === field);
  function connect(value = "test-dashboard-token") { ids.get("token").value = value; ids.get("connect-form").dispatch("submit"); }
  return { ids, nav, timers, document, calls, connect, findForm, control };
}
function response(status, data, headers = {}) {
  return { status, ok: status >= 200 && status < 300, json: async () => data,
    headers: { get: key => headers[key] ?? null } };
}
async function settle() { for (let n = 0; n < 8; n++) await new Promise(resolve => setImmediate(resolve)); }
module.exports = { boot, response, settle };
