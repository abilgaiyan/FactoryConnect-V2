import assert from "node:assert/strict";
import test from "node:test";

import React, { act } from "react";
import { createRoot } from "react-dom/client";

import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";
import { useQueryLifecycleController } from "../src/query/use-query-lifecycle-controller.ts";

function deferred() {
  let resolve;
  const promise = new Promise((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function createReactHost() {
  const listeners = new Map();
  const documentElement = {
    namespaceURI: "http://www.w3.org/1999/xhtml",
  };
  const document = {
    nodeType: 9,
    documentElement,
    activeElement: null,
    defaultView: globalThis,
    addEventListener(type, listener) {
      listeners.set(type, listener);
    },
    removeEventListener(type, listener) {
      if (listeners.get(type) === listener) {
        listeners.delete(type);
      }
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
      if (listeners.get(type) === listener) {
        listeners.delete(type);
      }
    },
  };

  return { container, document };
}

test("React mount subscribes once, rerender reuses controller, and unmount unsubscribes before disposal", async () => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalHtmlIFrameElement = globalThis.HTMLIFrameElement;
  const originalActEnvironment = globalThis.IS_REACT_ACT_ENVIRONMENT;
  const host = createReactHost();
  globalThis.document = host.document;
  globalThis.window = globalThis;
  globalThis.HTMLIFrameElement = class HTMLIFrameElement {};
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;

  const pending = deferred();
  const lifecycle = [];
  const renders = [];
  let signal;
  let factoryCount = 0;
  let binding;

  const factory = () => {
    factoryCount += 1;
    const controller = createQueryLifecycleController({
      query: (executionSignal) => {
        signal = executionSignal;
        return pending.promise;
      },
      isEmpty: () => false,
    });

    return {
      current: () => controller.current(),
      subscribe(listener) {
        lifecycle.push("subscribe");
        const unsubscribe = controller.subscribe(listener);
        return () => {
          lifecycle.push("unsubscribe");
          unsubscribe();
        };
      },
      execute: () => controller.execute(),
      dispose() {
        lifecycle.push("dispose");
        controller.dispose();
      },
    };
  };

  function Harness({ marker }) {
    binding = useQueryLifecycleController(factory);
    renders.push(`${marker}:${binding.state.kind}`);
    return null;
  }

  const root = createRoot(host.container);

  try {
    await act(async () => {
      root.render(React.createElement(Harness, { marker: "first" }));
    });

    assert.equal(factoryCount, 1);
    assert.deepEqual(lifecycle, ["subscribe"]);
    assert.equal(binding.state.kind, "idle");

    let execution;
    await act(async () => {
      execution = binding.execute();
      await Promise.resolve();
    });

    assert.equal(signal.aborted, false);
    assert.equal(binding.state.kind, "loading");
    assert.ok(renders.includes("first:loading"));

    await act(async () => {
      root.render(React.createElement(Harness, { marker: "rerender" }));
    });

    assert.equal(factoryCount, 1);
    assert.equal(binding.state.kind, "loading");

    await act(async () => {
      root.unmount();
    });

    assert.deepEqual(lifecycle, ["subscribe", "unsubscribe", "dispose"]);
    assert.equal(signal.aborted, true);

    pending.resolve({ items: [1] });
    await execution;
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = originalActEnvironment;
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    globalThis.HTMLIFrameElement = originalHtmlIFrameElement;
  }
});
