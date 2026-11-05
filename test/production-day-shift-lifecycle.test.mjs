import assert from "node:assert/strict";
import test from "node:test";

import React, { act } from "react";
import { createRoot } from "react-dom/client";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { useProductionDayShiftReporting } from "../src/application/use-production-day-shift-reporting.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

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

function createRuntime(queryProductionDayShiftMetrics, sources = [{
  machineId,
  processorId: "operational-metrics",
  siteId: "site-1",
  displayName: "Machine 1",
  groupName: "Line 1",
  displayOrder: 10,
}]) {
  return {
    configuration: { reportingBasePath: "/", requestTimeoutMilliseconds: 30_000, sources },
    reportingClient: {
      queryProductionDayShiftMetrics,
      queryProductionDayMetrics() { throw new Error("Production-day query is outside this proof."); },
      queryShiftMetrics() { throw new Error("UTC shift query is outside this proof."); },
    },
    now: () => new Date("2026-09-02T08:00:00Z"),
  };
}

function shiftItem(shiftId) {
  return {
    processorId: "operational-metrics",
    machineId,
    productionDay: { siteId: "site-1", businessDate: "2026-09-02" },
    productionLineId: "line-1",
    shift: {
      siteId: "site-1",
      shiftScheduleAssignmentId: `assignment-${shiftId}`,
      shiftId,
      startsAtUtc: "2026-09-01T20:00:00Z",
      endsAtUtc: "2026-09-02T04:00:00Z",
    },
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    sourceRevision: null,
    metrics: [],
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

test("mounted shift reporting publishes authoritative empty state without HTTP for zero sources", async () => {
  let calls = 0;
  let binding;
  const runtime = createRuntime(async () => {
    calls += 1;
    throw new Error("must not be called");
  }, []);

  function Harness() {
    binding = useProductionDayShiftReporting("2026-09-02", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "empty");
    assert.equal(calls, 0);
  });
});

test("mounted lifecycle contains pagination and publishes only the complete non-empty result", async () => {
  const first = deferred();
  const second = deferred();
  const calls = [];
  const observedKinds = [];
  let binding;
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return calls.length === 1 ? first.promise : second.promise;
  });

  function Harness() {
    binding = useProductionDayShiftReporting("2026-09-02", runtime);
    observedKinds.push(binding.state.kind);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");
    assert.equal(calls.length, 1);

    first.resolve({ items: [shiftItem("shift-a")], continuationToken: "opaque-next" });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });

    assert.equal(calls.length, 2);
    assert.equal(calls[1].request.continuationToken, "opaque-next");
    assert.equal(calls[1].signal, calls[0].signal);
    assert.equal(binding.state.kind, "loading");
    assert.equal(observedKinds.includes("success"), false);
    assert.equal(observedKinds.includes("empty"), false);

    second.resolve({ items: [shiftItem("shift-b")], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });

    assert.equal(binding.state.kind, "success");
    assert.deepEqual(
      binding.state.data.items.map(({ shift }) => shift.shiftId),
      ["shift-a", "shift-b"],
    );
  });
});

test("refresh supersedes the active complete traversal and stale completion cannot publish", async () => {
  const first = deferred();
  const second = deferred();
  const calls = [];
  let binding;
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return calls.length === 1 ? first.promise : second.promise;
  });

  function Harness() {
    binding = useProductionDayShiftReporting("2026-09-02", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");

    await act(async () => { void binding.execute(); await Promise.resolve(); });
    assert.equal(calls[0].signal.aborted, true);
    assert.equal(calls[1].signal.aborted, false);
    assert.equal(binding.state.kind, "loading");

    first.resolve({ items: [], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");

    second.resolve({ items: [], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "empty");
  });
});

test("keyed production-day change aborts and isolates the previous shift lifecycle", async () => {
  const first = deferred();
  const second = deferred();
  const calls = [];
  let binding;
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return calls.length === 1 ? first.promise : second.promise;
  });

  function Day({ productionDay }) {
    binding = useProductionDayShiftReporting(productionDay, runtime);
    return null;
  }
  function Harness({ productionDay }) {
    return React.createElement(Day, { key: productionDay, productionDay });
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness, { productionDay: "2026-09-02" })); await Promise.resolve(); });
    await act(async () => { root.render(React.createElement(Harness, { productionDay: "2026-09-03" })); await Promise.resolve(); });

    assert.equal(calls[0].signal.aborted, true);
    assert.equal(calls[1].request.sources[0].businessDate, "2026-09-03");
    assert.equal(binding.state.kind, "loading");

    first.resolve({ items: [], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");

    second.resolve({ items: [], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "empty");
  });
});

test("unmount aborts active shift traversal and prevents later publication", async () => {
  const pending = deferred();
  const calls = [];
  const runtime = createRuntime((request, options) => {
    calls.push({ request, signal: options.signal });
    return pending.promise;
  });
  let binding;

  function Harness() {
    binding = useProductionDayShiftReporting("2026-09-02", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    assert.equal(calls[0].signal.aborted, false);

    await act(async () => { root.unmount(); });
    assert.equal(calls[0].signal.aborted, true);

    pending.resolve({ items: [], continuationToken: null });
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    assert.equal(binding.state.kind, "loading");
  });
});

test("visible reporting failure is classified by the shared lifecycle controller", async () => {
  const pending = deferred();
  let binding;
  const runtime = createRuntime(() => pending.promise);

  function Harness() {
    binding = useProductionDayShiftReporting("2026-09-02", runtime);
    return null;
  }

  await withMountedHarness(async (root) => {
    await act(async () => { root.render(React.createElement(Harness)); await Promise.resolve(); });
    const failure = new ReportingNetworkFailure(new Error("offline"));
    pending.reject(failure);
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });

    assert.equal(binding.state.kind, "failed");
    assert.equal(binding.state.failure, failure);
  });
});
