"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { boot, response, settle } = require("./harness.cjs");
const coverage = { status: "collecting", channels: [], configuredCollectors: [], enabledCollectors: [], completeness: "partial" };
test("does not read until connect; token stays in header; permission denial stops overview polling", async () => {
  const app = boot(call => response(call.url.endsWith("/health") ? 200 : 403, call.url.endsWith("/health") ? { status: "ok" } : {}));
  assert.equal(app.calls.length, 0);
  app.connect(); await settle();
  assert.equal(app.ids.get("token").value, "");
  assert.equal(app.calls.length, 2);
  assert.ok(app.calls.every(x => x.options.method === "GET" && x.options.headers["X-SysWitness-Token"] === "test-dashboard-token"));
  assert.ok(app.calls.every(x => !x.url.includes("test-dashboard-token")));
  assert.equal([...app.timers.values()].filter(x => x.delay >= 15000).length, 0);
  assert.match(app.ids.get("overview-state").textContent, /403/);
});
test("pagination keeps applied filter snapshot despite unsubmitted edits; source markup is text", async () => {
  let eventPage = 0;
  const payload = '<img src=x onerror="throw 1">';
  const app = boot(call => {
    const url = new URL(call.url);
    if (url.pathname.endsWith("/health")) return response(200, { status: "ok" });
    if (url.pathname.endsWith("/coverage")) return response(200, coverage);
    return response(200, { items: [{ eventId: "fixture-id", occurredAtUtc: "2026-11-24T00:00:00Z",
      channel: payload, provider: "fixture", sourceEventId: 1 }], limit: 50, returned: 1,
      nextCursor: ++eventPage === 1 ? "opaque+/=" : null });
  });
  app.connect(); await settle();
  app.control("events", "channel").value = "System";
  app.findForm("events").dispatch("submit"); await settle();
  app.control("events", "channel").value = "Application";
  const next = app.ids.get("events-client").all().find(x => x.tagName === "BUTTON" && x.textContent === "หน้าถัดไป →");
  next.dispatch("click"); await settle();
  const url = new URL(app.calls.at(-1).url);
  assert.equal(url.searchParams.get("channel"), "System");
  assert.equal(url.searchParams.get("cursor"), "opaque+/=");
  assert.equal(app.control("events", "channel").value, "Application");
  assert.match(app.ids.get("events-client").textContent, /<img src=x/);
  assert.ok(!app.ids.get("events-client").all().some(x => x.tagName === "IMG"));
});
test("disconnect aborts pending reads and ignores late replies while retaining filter inputs", async () => {
  let resolveEvent;
  const app = boot(call => {
    if (call.url.includes("/health")) return response(200, { status: "ok" });
    if (call.url.includes("/coverage")) return response(200, coverage);
    return new Promise(resolve => { resolveEvent = resolve; });
  });
  app.connect(); await settle();
  app.control("events", "channel").value = "System";
  app.findForm("events").dispatch("submit"); await settle();
  const pending = app.calls.at(-1);
  app.ids.get("disconnect-button").dispatch("click");
  assert.equal(pending.options.signal.aborted, true);
  resolveEvent(response(200, { items: [{ eventId: "late-sensitive-evidence" }], returned: 1, limit: 50 }));
  await settle();
  assert.doesNotMatch(app.ids.get("events-client").textContent, /late-sensitive-evidence/);
  assert.equal(app.control("events", "channel").value, "System");
  assert.equal(app.ids.get("connect-button").disabled, false);
  assert.equal(app.timers.size, 0);
});
test("401 clears connection state; 429 uses bounded Retry-After and only overview polls", async () => {
  const denied = boot(call => response(call.url.includes("/health") ? 200 : 401, { status: "ok" }));
  denied.connect(); await settle();
  assert.equal(denied.ids.get("disconnect-button").disabled, true);
  assert.match(denied.ids.get("connection-state").textContent, /401/);
  const limited = boot(call => response(call.url.includes("/health") ? 200 : 429, { status: "ok" }, { "Retry-After": "60" }));
  limited.connect(); await settle();
  assert.equal([...limited.timers.values()].find(x => x.delay === 60000)?.delay, 60000);
  limited.nav.find(x => x.dataset.panel === "events").dispatch("click");
  assert.equal(limited.timers.size, 0);
});
test("Tree sends only exact identity and graph bounds; activity sends typed family without category", async () => {
  const app = boot(call => {
    if (call.url.includes("/health")) return response(200, { status: "ok" });
    if (call.url.includes("/coverage")) return response(200, coverage);
    if (call.url.includes("/tree")) return response(200, {
      nodes: [{ processGuid: "11111111-2222-4333-8444-555555555555", depth: 0, evidenceState: "referenceOnly", creations: [] }],
      relationships: [], unresolved: [], sourceEventLimit: 1000, sourceEventsUsed: 0, hasCycle: true
    });
    return response(404, {});
  });
  app.connect(); await settle();
  app.control("processes", "mode").value = "tree";
  app.control("processes", "processGuid").value = "11111111-2222-4333-8444-555555555555";
  app.control("processes", "computer").value = "Case-Sensitive-Host";
  app.control("processes", "channel").value = "ShouldNotReachTree";
  app.findForm("processes").dispatch("submit"); await settle();
  const tree = new URL(app.calls.at(-1).url);
  assert.deepEqual([...tree.searchParams.keys()].sort(), ["computer", "depth", "limit"]);
  assert.equal(tree.searchParams.get("computer"), "Case-Sensitive-Host");
  assert.match(app.ids.get("processes-client").textContent, /referenceOnly/);
  assert.match(app.ids.get("processes-client").textContent, /Self-reference/);
  app.control("activity", "kind").value = "files";
  app.findForm("activity").dispatch("submit"); await settle();
  assert.equal(new URL(app.calls.at(-1).url).pathname, "/api/v1/activity/files");
  assert.match(app.ids.get("activity-client").textContent, /404/);
});
