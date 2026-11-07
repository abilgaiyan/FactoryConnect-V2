import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";

const vite = await createServer({
  root: fileURLToPath(new URL("../", import.meta.url)),
  server: { middlewareMode: true }, appType: "custom", logLevel: "silent",
});
const { OperatorEvidencePage } = await vite.ssrLoadModule("/src/presentation/OperatorEvidencePage.tsx");
after(() => vite.close());

const machineId = "11111111-1111-1111-1111-111111111111";
const siteId = "22222222-2222-2222-2222-222222222222";
const baseAddress = "https://localhost/";

test("operator evidence screen refuses machine discovery without a human session", async t => {
  const previous = globalThis.fetch;
  t.after(() => { globalThis.fetch = previous; });
  globalThis.fetch = () => { throw Error("unauthenticated discovery attempted"); };
  const h = await mountInDom(React.createElement(OperatorEvidencePage,
    { baseAddress, sessionBearer: null }), baseAddress);
  t.after(() => h.dispose());
  assert.match(h.document.querySelector("[role=alert]").textContent, /Sign in with a trusted identity/);
  assert.equal(h.document.querySelector("select"), null);
});

test("authorized machine selection shows retained SQL-shaped values, then session removal hides all evidence", async t => {
  const previous = globalThis.fetch;
  t.after(() => { globalThis.fetch = previous; });
  const calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push({ url: String(url), authorization: options.headers.Authorization });
    if (String(url).endsWith("/evidence-machines"))
      return Response.json({ machines: [{ siteId, machineId, displayName: "CNC One" }] });
    if (String(url).endsWith(`/machines/${machineId}/evidence`))
      return Response.json({ machineId, siteId, streamKey: "mtconnect:uuid:fixture",
        evidenceKind: "historical-retained-evidence", currentConnectivity: null,
        rows: [{ instanceId: "7", sequence: "18446744073709551615", mappingId: "map-7",
          address: "Counter", rawValue: " 00042 ", quality: "source-reported",
          sourceTimestamp: "2026-11-07T12:00:00+05:30",
          observedAt: "2026-11-07T06:30:00Z", receivedAt: "2026-11-07T06:30:01Z",
          batchId: "batch-7", acknowledgementId: "ack-7", documentHash: "hash-7" }] });
    throw Error("Unexpected request: " + url);
  };
  const view = sessionBearer => React.createElement(OperatorEvidencePage, { baseAddress, sessionBearer });
  const h = await mountInDom(view("fixture-session"), baseAddress);
  t.after(() => h.dispose());
  const selector = h.document.querySelector("select");
  assert.ok(selector);
  assert.equal(selector.options.length, 2);
  assert.match(selector.textContent, /CNC One/);
  assert.equal(calls.length, 1);
  await act(async () => {
    selector.value = machineId;
    selector.dispatchEvent(new h.window.Event("change", { bubbles: true }));
  });
  assert.equal(calls.length, 2);
  assert.ok(calls.every(x => x.authorization === "Bearer fixture-session"));
  assert.equal(h.document.querySelector("tbody code").textContent, " 00042 ");
  assert.match(h.document.body.textContent, /18446744073709551615/);
  assert.match(h.document.querySelector("details").textContent, /ack-7/);
  await h.rerender(view(null));
  assert.equal(h.document.querySelector("tbody"), null);
  assert.equal(h.document.querySelector("select"), null);
  assert.match(h.document.querySelector("[role=alert]").textContent, /Sign in/);
  assert.equal(calls.length, 2);
});
