import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";
const vite = await createServer({ root: fileURLToPath(new URL("../", import.meta.url)), server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { CommissioningPage } = await vite.ssrLoadModule("/src/presentation/CommissioningPage.tsx");
after(() => vite.close());
const snapshot = {
  readStartedAt: "2026-10-28T00:00:00Z",
  configuration: { status: "captured-file-only", authority: "capture", fileModifiedAt: null, sha256: "captured-hash", fields: [] },
  durable: { status: "read", authority: "heads", readCompletedAt: "2026-10-28T00:00:01Z", processors: [{ rowId: 2, machineId: "moxa-id", processorId: "activity", streamKey: "moxa-stream", position: null, admissionBound: true, productionStartsAt: null }] },
  runtimeActivation: "unknown/unverified", transportHealth: "unknown", checkpointFreshness: "unknown",
};
test("refresh removes previous evidence immediately; failed read never retains current-looking data", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  let resolve;
  let calls = 0;
  globalThis.fetch = async () => ++calls === 1 ? Response.json(snapshot) : new Promise(done => { resolve = done; });
  const h = await mountInDom(React.createElement(CommissioningPage));
  t.after(() => h.dispose());
  assert.match(h.document.body.textContent, /moxa-stream/);
  assert.match(h.document.body.textContent, /Not established/);
  assert.match(h.document.body.textContent, /unknown\/unverified/);
  await h.click(h.document.querySelector("button"));
  assert.doesNotMatch(h.document.body.textContent, /moxa-stream|captured-hash/);
  assert.equal(h.document.querySelector("button").disabled, true);
  await act(async () => resolve(new Response("", { status: 503 })));
  assert.match(h.document.querySelector("[role=alert]").textContent, /No previous snapshot/);
  assert.equal(h.document.querySelector("table"), null);
});
test("unmount cancels pending read", async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  let signal;
  globalThis.fetch = async (_, options) => { signal = options.signal; return new Promise(() => {}); };
  const h = await mountInDom(React.createElement(CommissioningPage));
  await h.dispose();
  assert.equal(signal.aborted, true);
});
