import assert from "node:assert/strict";
import test from "node:test";

import { ReportingInvalidQueryFailure } from "../src/api/reporting/index.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });

  return { promise, resolve, reject };
}

test("obsolete invalid-query failure publishes nothing", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: () => {
      const pending = deferred();
      requests.push(pending);
      return pending.promise;
    },
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  const a = controller.execute();
  const b = controller.execute();
  requests[0].reject(new ReportingInvalidQueryFailure({
    type: "urn:factoryconnect:problem:reporting:invalid-request",
    title: "Invalid reporting query",
    status: 400,
    detail: "obsolete",
    instance: null,
  }));

  await a;
  assert.deepEqual(controller.current(), { kind: "loading" });
  assert.deepEqual(states, ["loading", "loading"]);

  requests[1].resolve({ items: ["B"] });
  await b;
  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["B"] } });
});
