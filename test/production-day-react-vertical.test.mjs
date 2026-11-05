import assert from "node:assert/strict";
import test from "node:test";

import React, { act } from "react";
import { createRoot } from "react-dom/client";

import { useProductionDayReporting } from "../src/application/use-production-day-reporting.ts";

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
    addEventListener(type, listener) {
      listeners.set(type, listener);
    },
    removeEventListener(type, listener) {
      if (listeners.get(type) === listener) listeners.delete(type);
    },
  };
  const container = {
    nodeType: 1,
    tagName: "DIV",
    namespaceURI: "http://www.w3.org/1999/xhtml",
    ownerDocument: document,
    addEventListener(type, listener) {
      listeners.set(type, listener);
    },
    removeEventListener(type, listener) {
      if (listeners.get(type) === listener) listeners.delete(type);
    },
  };
  return { container, document };
}

function page(metricKey) {
  return {
    items: [{ metricKey }],
    continuationToken: null,
  };
}

test("production-day route change aborts prior query and late obsolete response cannot publish", async () => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalHtmlIFrameElement = globalThis.HTMLIFrameElement;
  const originalActEnvironment = globalThis.IS_REACT_ACT_ENVIRONMENT;
  const host = createReactHost();
  globalThis.document = host.document;
  globalThis.window = globalThis;
  globalThis.HTMLIFrameElement = class HTMLIFrameElement {};
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;

  const first = deferred();
  const second = deferred();
  const calls = [];
  const renders = [];
  let binding;

  const runtime = {
    configuration: {
      reportingBasePath: "/",
      requestTimeoutMilliseconds: 30_000,
      sources: [{
        machineId: "11111111-1111-1111-1111-111111111111",
        processorId: "operational-metrics",
        displayName: "Machine 1",
      }],
    },
    reportingClient: {
      queryProductionDayMetrics(request, options) {
        calls.push({ request, signal: options.signal });
        return calls.length === 1 ? first.promise : second.promise;
      },
      queryShiftMetrics() {
        throw new Error("Shift query is outside this proof.");
      },
    },
  };

  function Harness({ productionDay }) {
    binding = useProductionDayReporting(productionDay, runtime);
    renders.push(`${productionDay}:${binding.state.kind}`);
    return null;
  }

  const root = createRoot(host.container);

  try {
    await act(async () => {
      root.render(React.createElement(Harness, { productionDay: "2026-08-30" }));
      await Promise.resolve();
    });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].request.fromInclusive, "2026-08-30");
    assert.equal(calls[0].request.toExclusive, "2026-08-31");
    assert.equal(binding.state.kind, "loading");
    assert.equal(calls[0].signal.aborted, false);

    await act(async () => {
      root.render(React.createElement(Harness, { productionDay: "2026-08-31" }));
      await Promise.resolve();
    });

    assert.equal(calls.length, 2);
    assert.equal(calls[0].signal.aborted, true);
    assert.equal(calls[1].signal.aborted, false);
    assert.equal(calls[1].request.fromInclusive, "2026-08-31");
    assert.equal(calls[1].request.toExclusive, "2026-09-01");
    assert.equal(binding.state.kind, "loading");

    first.resolve(page("ObsoleteMetric"));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    assert.equal(binding.state.kind, "loading");
    assert.equal(renders.some((render) => render.endsWith(":success")), false);

    second.resolve(page("availability"));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    assert.equal(binding.state.kind, "success");
    assert.equal(binding.state.data.items[0].metricKey, "availability");

    const third = deferred();
    runtime.reportingClient.queryProductionDayMetrics = (request, options) => {
      calls.push({ request, signal: options.signal });
      return third.promise;
    };

    await act(async () => {
      root.render(React.createElement(Harness, { productionDay: "2026-09-01" }));
      await Promise.resolve();
    });

    const thirdCall = calls.at(-1);
    assert.equal(thirdCall.signal.aborted, false);

    await act(async () => {
      root.unmount();
    });

    assert.equal(thirdCall.signal.aborted, true);
    third.resolve(page("LateMetric"));
  } finally {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    globalThis.HTMLIFrameElement = originalHtmlIFrameElement;
    globalThis.IS_REACT_ACT_ENVIRONMENT = originalActEnvironment;
  }
});
