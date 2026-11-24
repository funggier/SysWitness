"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { snapshotQuery, pageStep, boundedTree, isGuid, requestError, writeText } = require("../../dashboard/model.js");

test("query snapshots preserve filters, omit unsupported fields, and encode opaque cursors", () => {
  const form = { channel: " System ", provider: "A & B", category: "process", processGuid: "hidden", limit: "25" };
  const query = snapshotQuery(form, ["channel", "provider", "limit"]);
  form.channel = "Application";
  assert.deepEqual(query, { channel: "System", provider: "A & B", limit: "25" });
  assert.ok(Object.isFrozen(query));
  const next = pageStep({ query, cursor: null, previous: [], page: 1 }, "next", "a+/=");
  assert.equal(next.query, query);
  assert.equal(new URLSearchParams({ ...next.query, cursor: next.cursor }).get("cursor"), "a+/=");
  const previous = pageStep(next, "previous");
  assert.equal(previous.cursor, null);
  assert.equal(previous.page, 1);
  assert.equal(next.page, 2);
});
test("validates stable GUIDs without accepting zero, braces, or PID aliases", () => {
  assert.equal(isGuid("11111111-2222-4333-8444-555555555555"), true);
  for (const value of ["00000000-0000-0000-0000-000000000000", "{11111111-2222-4333-8444-555555555555}", "42", ""]) {
    assert.equal(isGuid(value), false);
  }
});
test("tree view bounds cyclic/conflicting/reference data without recursive expansion", () => {
  const cyclic = {
    nodes: [{ processGuid: "b", depth: null, evidenceState: "referenceOnly", creations: [] },
      { processGuid: "a", depth: 0, hasConflictingParents: true, creations: [{ imagePath: "<img onerror=x>" }] }],
    relationships: [{ parentProcessGuid: "a", childProcessGuid: "b" }, { parentProcessGuid: "b", childProcessGuid: "a" }],
    unresolved: [{ reason: "self_reference" }]
  };
  const result = boundedTree(cyclic);
  assert.equal(result.nodes[0].processGuid, "a");
  assert.equal(result.nodes[1].depth, null);
  assert.equal(result.relationships.length, 2);
  assert.equal(result.nodes[0].hasConflictingParents, true);
  assert.equal(result.clientClipped, false);
  const clipped = boundedTree({ nodes: Array(300).fill({}), relationships: Array(1002).fill({}), unresolved: [] });
  assert.equal(clipped.nodes.length, 256);
  assert.equal(clipped.relationships.length, 1000);
  assert.equal(clipped.clientClipped, true);
});
test("untrusted source strings use a text-only sink, including markup", () => {
  let actual;
  const target = { set textContent(value) { actual = value; }, set innerHTML(_) { throw new Error("unsafe HTML sink"); } };
  const payload = '<img src=x onerror="alert(1)"><script>throw 1</script>';
  writeText(target, payload);
  assert.equal(actual, payload);
  writeText(target, null);
  assert.equal(actual, "—");
  writeText(target, false);
  assert.equal(actual, "false");
});
test("HTTP states distinguish authorization, rate limit, invalid input, and server failure", () => {
  assert.match(requestError(401), /401/);
  assert.match(requestError(403), /403/);
  assert.match(requestError(429), /429/);
  assert.match(requestError(400), /400/);
  assert.match(requestError(404), /404/);
  assert.match(requestError(503), /503/);
});
