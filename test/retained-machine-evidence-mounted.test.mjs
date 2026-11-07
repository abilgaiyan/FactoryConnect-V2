import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";

const vite = await createServer({ root: fileURLToPath(new URL("../", import.meta.url)), server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { RetainedMachineEvidence } = await vite.ssrLoadModule("/src/presentation/RetainedMachineEvidence.tsx");
after(() => vite.close());
const machineId = "11111111-1111-1111-1111-111111111111";
const page = {
  machineId, siteId: "22222222-2222-2222-2222-222222222222",
  streamKey: "mtconnect:uuid:exact", evidenceKind: "historical-retained-evidence", currentConnectivity: null,
  rows: [{ sequence: "18446744073709551615", instanceId: "42", mappingId: "mapping",
    address: "Counter", rawValue: " 00042 ", quality: "source-reported",
    sourceTimestamp: "2026-11-07T12:00:00+05:30", observedAt: "2026-11-07T06:30:00Z",
    receivedAt: "2026-11-07T06:30:01Z", batchId: "batch", acknowledgementId: "ack", documentHash: "abc" }],
};
test("mounted evidence shows literal saved value and decimal sequence without claiming connectivity", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  globalThis.fetch = async (_url, request) => {
    assert.equal(request.headers.Authorization, "Bearer fixture-session");
    return Response.json(page);
  };
  const h = await mountInDom(React.createElement(RetainedMachineEvidence, { machineId, baseAddress: "https://localhost/", sessionBearer: "fixture-session" }), "https://localhost/");
  t.after(() => h.dispose());
  assert.match(h.document.body.textContent, /18446744073709551615/);
  assert.equal(h.document.querySelector("tbody code").textContent, " 00042 ");
  assert.match(h.document.body.textContent, /does not establish current connectivity/);
  const detail = h.document.querySelector("details");
  assert.ok(detail, "evidence provenance must be inspectable");
  assert.match(detail.textContent, /Mapping ID/);
  assert.match(detail.textContent, /mapping/);
  assert.match(detail.textContent, /Runtime acknowledgement ID/);
  assert.match(detail.textContent, /ack/);
  assert.match(detail.textContent, /Source document SHA-256/);
  assert.match(detail.textContent, /abc/);
  assert.match(detail.textContent, /Receipt time/);
  assert.match(detail.textContent, /2026-11-07T06:30:01Z/);
});
test("no session is denied without issuing a request", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  globalThis.fetch = () => { throw new Error("must not fetch"); };
  const h = await mountInDom(React.createElement(RetainedMachineEvidence, { machineId, baseAddress: "https://localhost/", sessionBearer: null }), "https://localhost/");
  t.after(() => h.dispose());
  assert.match(h.document.querySelector("[role=alert]").textContent, /not authorized/);
});
test("authority failure does not display cached values", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  globalThis.fetch = async () => new Response("", { status: 503 });
  const h = await mountInDom(React.createElement(RetainedMachineEvidence, { machineId, baseAddress: "https://localhost/", sessionBearer: "fixture-session" }), "https://localhost/");
  t.after(() => h.dispose());
  assert.match(h.document.querySelector("[role=alert]").textContent, /unavailable/);
  assert.equal(h.document.querySelector("tbody"), null);
});

test("switching machines hides prior evidence before the new request resolves", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  const nextMachine = "33333333-3333-3333-3333-333333333333";
  let resolveSecond;
  globalThis.fetch = async url => {
    if (String(url).includes(nextMachine))
      return new Promise(resolve => { resolveSecond = resolve; });
    return Response.json(page);
  };
  const props = id => React.createElement(RetainedMachineEvidence,
    { machineId: id, baseAddress: "https://localhost/", sessionBearer: "fixture-session" });
  const h = await mountInDom(props(machineId), "https://localhost/");
  t.after(() => h.dispose());
  assert.match(h.document.body.textContent, /00042/);
  await h.rerender(props(nextMachine));
  assert.doesNotMatch(h.document.body.textContent, /00042/);
  assert.match(h.document.body.textContent, /Loading accepted evidence/);
  await act(async () => resolveSecond(Response.json({ ...page, machineId: nextMachine, rows: [] })));
  assert.match(h.document.body.textContent, /No retained observations/);
});

test("ending a session removes previously rendered evidence and makes no new request", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  let calls = 0;
  globalThis.fetch = async () => { calls++; return Response.json(page); };
  const props = bearer => React.createElement(RetainedMachineEvidence,
    { machineId, baseAddress: "https://localhost/", sessionBearer: bearer });
  const h = await mountInDom(props("fixture-session"), "https://localhost/");
  t.after(() => h.dispose());
  assert.match(h.document.body.textContent, /00042/);
  assert.equal(calls, 1);
  await h.rerender(props(null));
  assert.doesNotMatch(h.document.body.textContent, /00042/);
  assert.match(h.document.body.textContent, /not authorized/);
  assert.equal(calls, 1);
});
