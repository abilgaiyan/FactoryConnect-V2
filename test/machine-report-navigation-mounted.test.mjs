import assert from "node:assert/strict";
import { after, beforeEach, afterEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";

const vite = await createServer({ root: fileURLToPath(new URL("../", import.meta.url)), server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { App } = await vite.ssrLoadModule("/src/App.tsx");
after(async () => vite.close());
const realFetch = globalThis.fetch;
beforeEach(() => { globalThis.fetch = async () => new Response('', {status: 503}); });
afterEach(() => { globalThis.fetch = realFetch; });
const machineId = "11111111-1111-1111-1111-111111111111";
const basePath = `/machines/${machineId}`;
function runtime() {
  const calls = [];
  return { calls, value: {
    configuration: { reportingBasePath: "/", requestTimeoutMilliseconds: 30000, sources: [
      { machineId, processorId: "operational-metrics", siteId: "CAMPUS-1", productionLineId: "LINE-1", displayName: "M01", groupName: "Sanwariyanath", displayOrder: 0 },
    ] },
    reportingClient: {},
    currentStateClient: { async read(id, options) { calls.push({ id, options }); throw new Error("Offline"); } },
  } };
}
function link(h, label) { return [...h.document.querySelectorAll('nav[aria-label="Machine views"] a')].find(a => a.textContent === label); }
function headings(h) { return [...h.document.querySelectorAll("h2")].map(h => h.textContent); }

test("status links to separate reports and navigation mounts only the selected report", async t => {
  const r = runtime();
  const h = await mountInDom(React.createElement(App, { runtime: r.value }), `http://factory-dashboard${basePath}`);
  t.after(() => h.dispose());
  assert.equal(r.calls.length, 1);
  assert.equal(h.document.querySelector('section[aria-labelledby="activity-title"]'), null);
  assert.equal(h.document.querySelector('section[aria-labelledby="counter-title"]'), null);
  assert.equal(link(h, "Machine status").getAttribute("aria-current"), "page");
  await h.click(link(h, "Activity history"));
  assert.equal(h.window.location.pathname, `${basePath}/activity`);
  assert.deepEqual(headings(h), ["Machine activity history"]);
  assert.equal(h.document.querySelector('section[aria-labelledby="counter-title"]'), null);
  assert.equal(link(h, "Activity history").getAttribute("aria-current"), "page");
  assert.match(h.document.body.textContent, /M01 · CAMPUS-1 \/ LINE-1/);
  await h.click(link(h, "Counter inspection"));
  assert.equal(h.window.location.pathname, `${basePath}/counter`);
  assert.deepEqual(headings(h), ["Raw counter inspection"]);
  assert.equal(link(h, "Counter inspection").getAttribute("aria-current"), "page");
  assert.equal(r.calls.length, 1);
  await h.popstate(`${basePath}/activity`);
  assert.deepEqual(headings(h), ["Machine activity history"]);
  await h.click(link(h, "Machine status"));
  assert.equal(r.calls.length, 2);
  assert.equal(h.document.querySelector('section[aria-labelledby="activity-title"]'), null);
});

test("direct report URLs do not retrieve current state and retain the common site header", async t => {
  for (const suffix of ["activity", "counter"]) {
    const r = runtime();
    const h = await mountInDom(React.createElement(App, { runtime: r.value }), `http://factory-dashboard${basePath}/${suffix}`);
    try {
      assert.equal(r.calls.length, 0);
      assert.equal(headings(h).length, 1);
      assert.equal(h.document.querySelectorAll('input[type="date"]').length, 2);
      assert.match(h.document.querySelector("header").textContent, /Sanwariyanath · CAMPUS-1/);
      assert.equal(link(h, "Machine status").getAttribute("href"), basePath);
    } finally { await h.dispose(); }
  }
});

test("unconfigured machines cannot mount either report or trigger current-state reads", async () => {
  for (const suffix of ["activity", "counter"]) {
    const r = runtime();
    const h = await mountInDom(React.createElement(App, { runtime: r.value }), `http://factory-dashboard/machines/22222222-2222-2222-2222-222222222222/${suffix}`);
    try {
      assert.match(h.document.querySelector('[role="alert"]').textContent, /not configured/);
      assert.equal(h.document.querySelector("form"), null);
      assert.equal(r.calls.length, 0);
    } finally { await h.dispose(); }
  }
});

test("leaving each report cancels its pending read and late failures cannot affect the next page", async t => {
  const originalFetch = globalThis.fetch;
  t.after(() => { globalThis.fetch = originalFetch; });
  for (const suffix of ["activity", "counter"]) {
    let reject;
    let signal;
    globalThis.fetch = async (_input, options) => {
      signal = options.signal;
      return new Promise((_resolve, failed) => { reject = failed; });
    };
    const r = runtime();
    const h = await mountInDom(React.createElement(App, { runtime: r.value }), `http://factory-dashboard${basePath}/${suffix}`);
    try {
      if (suffix === "counter") await h.click([...h.document.querySelectorAll("button")].find(b => b.textContent === "Find signals"));
      else await h.submit(h.document.querySelector("form"));
      assert.ok(signal);
      assert.equal(signal.aborted, false);
      await h.click(link(h, "Machine status"));
      assert.equal(signal.aborted, true);
      await act(async () => { reject(new Error("Late report failure")); });
      assert.equal(h.document.querySelector('section[aria-labelledby="activity-title"]'), null);
      assert.equal(h.document.querySelector('section[aria-labelledby="counter-title"]'), null);
      assert.doesNotMatch(h.document.body.textContent, /Late report failure/);
    } finally { await h.dispose(); }
  }
});

