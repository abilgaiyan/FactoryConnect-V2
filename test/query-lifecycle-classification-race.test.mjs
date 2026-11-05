import assert from "node:assert/strict";
import test from "node:test";

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

test("current classifier programming defect restores idle and preserves rejection", async () => {
  const defect = new TypeError("classifier bug");
  const states = [];
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [1] }),
    isEmpty: () => {
      throw defect;
    },
  });
  controller.subscribe((state) => states.push(state.kind));

  await assert.rejects(controller.execute(), (error) => error === defect);

  assert.deepEqual(states, ["loading", "idle"]);
  assert.deepEqual(controller.current(), { kind: "idle" });
});

test("classifier re-entrancy cannot let A overwrite B", async () => {
  const bRequest = deferred();
  let call = 0;
  let bExecution;
  const states = [];

  const controller = createQueryLifecycleController({
    query: async () => {
      call += 1;
      if (call === 1) {
        return { items: ["A"] };
      }

      return bRequest.promise;
    },
    isEmpty: (data) => {
      if (data.items[0] === "A") {
        bExecution = controller.execute();
      }

      return false;
    },
  });
  controller.subscribe((state) => states.push(state.kind));

  const aExecution = controller.execute();
  await aExecution;

  assert.deepEqual(controller.current(), { kind: "loading" });
  assert.deepEqual(states, ["loading", "loading"]);

  bRequest.resolve({ items: ["B"] });
  await bExecution;

  assert.deepEqual(controller.current(), {
    kind: "success",
    data: { items: ["B"] },
  });
  assert.deepEqual(states, ["loading", "loading", "success"]);
});
