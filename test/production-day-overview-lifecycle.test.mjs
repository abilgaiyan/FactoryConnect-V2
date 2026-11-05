import assert from "node:assert/strict";
import test from "node:test";

import React, { act } from "react";
import { createRoot } from "react-dom/client";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { productionDayPath } from "../src/application/production-day-navigation.ts";
import { useProductionDayOverview } from "../src/application/use-production-day-overview.ts";

const machineId = "11111111-1111-1111-1111-111111111111";
const processorId = "operational-metrics";

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function createReactHost() {
  const listeners = new Map();
  const documentElement = { namespaceURI: "http://www.w3.org/1999/xhtml" };
  const document = {
    nodeType: 9,
    documentElement,
    activeElement: null,
    defaultView: globalThis,
    addEventListener(type, listener) { listeners.set(type, listener); },
    removeEventListener(type, listener) { if (listeners.get(type) === listener) listeners.delete(type); },
  };
  const container = {
    nodeType: 1,
    tagName: "DIV",
    namespaceURI: "http://www.w3.org/1999/xhtml",
    ownerDocument: document,
    addEventListener(type, listener) { listeners.set(type, listener); },
    removeEventListener(type, listener) { if (listeners.get(type) === listener) listeners.delete(type); },
  };
  return { container, document };
}

function createRuntime(queryProductionDayMetrics, now = () => new Date("2026-09-01T08:00:00Z")) {
  return {
    configuration: {
      reportingBasePath: "/",
      requestTimeoutMilliseconds: 30_000,
      sources: [{ machineId, processorId, displayName: "Machine 1", groupName: "Line 1", displayOrder: 10 }],
    },
    reportingClient: {
      queryProductionDayMetrics,
      queryShiftMetrics() { throw new Error("Shift query is outside this proof."); },
    },
    now,
  };
}

function emptyPage() {
  return { items: [], continuationToken: null };
}

function unexpectedSourcePage(day) {
  return {
    items: [{
      scope: "production-day",
      processorId,
      machineId: "22222222-2222-2222-2222-222222222222",
      metricKey: "availability",
      definitionVersion: "1.0",
      status: "calculated",
      value: "0.8",
      unit: "ratio",
      reasonCode: null,
      reasonOperandName: null,
      context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
      shift: null,
      productionDay: { siteId: "factory", businessDate: day },
      sourceRevision: {
        processorId,
        machineId: "22222222-2222-2222-2222-222222222222",
        streamKey: "stream",
        position: "1",
      },
    }],
    continuationToken: null,
  };
}

async function withMountedHarness(run) {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalHtmlIFrameElement = globalThis.HTMLIFrameElement;
  const originalActEnvironment = globalThis.IS_REACT_ACT_ENVIRONMENT;
  const host = createReactHost();
  globalThis.document = host.document;
  globalThis.window = globalThis;
  globalThis.HTMLIFrameElement = class HTMLIFrameElement {};
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  const root = createRoot(host.container);

  try {
    await run(root);
  } finally {
    await act(async () => { root.unmount(); });
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    globalThis.HTMLIFrameElement = originalHtmlIFrameElement;
    globalThis.IS_REACT_ACT_ENVIRONMENT = originalActEnvironment;
  }
}

test("overview date route remains date-only", () => {
  assert.equal(productionDayPath("2026-09-01"), "/production-days/2026-09-01");
  assert.equal(productionDayPath("2026-09-01").includes("T"), false);
});

test("mounted overview records one injected timestamp, refreshes same day, and preserves it on failure", async () => {
  const first = deferred();
  const refresh = deferred();
  const failure = deferred();
  const calls = [];
  const clockValues = [new Date("2026-09-01T08:00:00Z"), new Date("2026-09-01T08:05:00Z")];
  let clockCalls = 0;
  let binding;
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return calls.length === 1 ? first.promise : calls.length === 2 ? refresh.promise : failure.promise;
  }, () => clockValues[clockCalls++]);

  function Harness() {
    binding = useProductionDayOverview("2026-09-01", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");
    assert.equal(clockCalls, 0);

    first.resolve(emptyPage());
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "success");
    assert.equal(clockCalls, 1);
    assert.equal(binding.lastSuccessfulRetrieval.retrievedAt.toISOString(), "2026-09-01T08:00:00.000Z");

    await act(async () => { void binding.refresh(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "refreshing");
    assert.equal(calls[1].request.fromInclusive, "2026-09-01");
    assert.equal(binding.lastSuccessfulRetrieval.retrievedAt.toISOString(), "2026-09-01T08:00:00.000Z");

    refresh.resolve(emptyPage());
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "success");
    assert.equal(clockCalls, 2);
    assert.equal(binding.lastSuccessfulRetrieval.retrievedAt.toISOString(), "2026-09-01T08:05:00.000Z");

    await act(async () => { void binding.refresh(); await Promise.resolve(); });
    failure.reject(new ReportingNetworkFailure(new Error("offline")));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "reporting-failed");
    assert.equal(clockCalls, 2);
    assert.equal(binding.lastSuccessfulRetrieval.retrievedAt.toISOString(), "2026-09-01T08:05:00.000Z");
  });
});

test("mounted presentation failure preserves the previous successful timestamp", async () => {
  const first = deferred();
  const second = deferred();
  let callCount = 0;
  let binding;
  const runtime = createRuntime(() => ++callCount === 1 ? first.promise : second.promise);

  function Harness() {
    binding = useProductionDayOverview("2026-09-01", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    first.resolve(emptyPage());
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    const timestamp = binding.lastSuccessfulRetrieval.retrievedAt;

    await act(async () => { void binding.refresh(); await Promise.resolve(); });
    second.resolve(unexpectedSourcePage("2026-09-01"));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });

    assert.equal(binding.state.kind, "presentation-failed");
    assert.equal(binding.lastSuccessfulRetrieval.retrievedAt, timestamp);
  });
});

test("keyed mounted day change aborts and isolates the previous lifecycle", async () => {
  const first = deferred();
  const second = deferred();
  const calls = [];
  const renders = [];
  let binding;
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return calls.length === 1 ? first.promise : second.promise;
  });

  function Day({ productionDay }) {
    binding = useProductionDayOverview(productionDay, runtime);
    renders.push(`${productionDay}:${binding.state.kind}`);
    return null;
  }
  function Harness({ productionDay }) {
    return React.createElement(Day, { key: productionDay, productionDay });
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness, { productionDay: "2026-09-01" })); await Promise.resolve(); });
    assert.equal(calls[0].signal.aborted, false);

    await act(async () => { root.render(React.createElement(Harness, { productionDay: "2026-09-02" })); await Promise.resolve(); });
    assert.equal(calls[0].signal.aborted, true);
    assert.equal(calls[1].request.fromInclusive, "2026-09-02");
    assert.equal(binding.state.kind, "loading");

    first.resolve(emptyPage());
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");
    assert.equal(renders.includes("2026-09-02:presentation-failed"), false);
    assert.equal(renders.includes("2026-09-02:success"), false);

    second.resolve(emptyPage());
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "success");
  });
});