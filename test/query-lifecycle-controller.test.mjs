import assert from "node:assert/strict";
import test from "node:test";

import {
  ReportingCancellationFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingNetworkFailure,
  ReportingProtocolFailure,
  ReportingTimeoutFailure,
} from "../src/api/reporting/index.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

const problemDetails = {
  type: "urn:factoryconnect:problem:reporting:invalid-request",
  title: "Invalid reporting query",
  status: 400,
  detail: "The query is invalid.",
  instance: null,
};

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });

  return { promise, resolve, reject };
}

test("controller starts idle and publishes loading then success", async () => {
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [1] }),
    isEmpty: (data) => data.items.length === 0,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  assert.deepEqual(controller.current(), { kind: "idle" });
  const result = await controller.execute();

  assert.deepEqual(states, ["loading", "success"]);
  assert.equal(result.kind, "success");
  assert.deepEqual(controller.current(), result);
});

test("successful zero-item result becomes empty and retains authoritative data", async () => {
  const data = { items: [] };
  const controller = createQueryLifecycleController({
    query: async () => data,
    isEmpty: (result) => result.items.length === 0,
  });

  assert.deepEqual(await controller.execute(), { kind: "empty", data });
});

test("calculated zero remains successful data", async () => {
  const data = { items: [{ status: "calculated", value: 0 }] };
  const controller = createQueryLifecycleController({
    query: async () => data,
    isEmpty: (page) => page.items.length === 0,
  });

  assert.deepEqual(await controller.execute(), { kind: "success", data });
});

test("invalid reporting query becomes invalidRequest with original Problem Details", async () => {
  const failure = new ReportingInvalidQueryFailure(problemDetails);
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => false,
  });

  const result = await controller.execute();
  assert.deepEqual(result, { kind: "invalidRequest", details: problemDetails });
  assert.equal(result.details, failure.problemDetails);
});

test("all non-invalid reporting failures remain failed", async () => {
  const failures = [
    new ReportingCancellationFailure(),
    new ReportingTimeoutFailure(30_000),
    new ReportingNetworkFailure(new Error("offline")),
    new ReportingMalformedContinuationTokenFailure({ ...problemDetails, type: "urn:factoryconnect:problem:reporting:malformed-continuation-token" }),
    new ReportingIncompatibleContinuationTokenFailure({ ...problemDetails, type: "urn:factoryconnect:problem:reporting:incompatible-continuation-token" }),
    new ReportingHttpFailure(502),
    new ReportingProtocolFailure(200, "Malformed response."),
  ];

  for (const failure of failures) {
    const controller = createQueryLifecycleController({
      query: async () => Promise.reject(failure),
      isEmpty: () => false,
    });

    const result = await controller.execute();
    assert.equal(result.kind, "failed");
    assert.equal(result.failure, failure);
  }
});

test("failure never becomes empty", async () => {
  const failure = new ReportingNetworkFailure(new Error("offline"));
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => true,
  });

  const result = await controller.execute();
  assert.equal(result.kind, "failed");
  assert.equal(result.failure, failure);
});

test("unexpected cancellation of the current execution is observable as failed", async () => {
  const failure = new ReportingCancellationFailure("unexpected");
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => false,
  });

  const result = await controller.execute();
  assert.deepEqual(result, { kind: "failed", failure });
});

test("sequential re-execution retains completed result while refreshing", async () => {
  const responses = [
    { items: [1] },
    { items: [] },
    { items: [2] },
  ];
  let index = 0;
  const controller = createQueryLifecycleController({
    query: async () => responses[index++],
    isEmpty: (data) => data.items.length === 0,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  assert.equal((await controller.execute()).kind, "success");
  assert.equal((await controller.execute()).kind, "empty");
  assert.equal((await controller.execute()).kind, "success");

  assert.deepEqual(states, [
    "loading",
    "success",
    "refreshing",
    "empty",
    "refreshing",
    "success",
  ]);
  assert.deepEqual(controller.current(), {
    kind: "success",
    data: { items: [2] },
  });
});

test("subscription cleanup stops later notifications", async () => {
  let value = 1;
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [value++] }),
    isEmpty: () => false,
  });
  const states = [];
  const dispose = controller.subscribe((state) => states.push(state.kind));

  await controller.execute();
  dispose();
  await controller.execute();

  assert.deepEqual(states, ["loading", "success"]);
});

test("current programming exceptions restore idle and remain rejected", async () => {
  const defect = new TypeError("bug");
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(defect),
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  await assert.rejects(controller.execute(), (error) => error === defect);
  assert.deepEqual(states, ["loading", "idle"]);
  assert.deepEqual(controller.current(), { kind: "idle" });
});

test("starting B aborts A and B owns publication", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: (signal) => {
      const pending = deferred();
      requests.push({ signal, pending });
      return pending.promise;
    },
    isEmpty: (data) => data.items.length === 0,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  const a = controller.execute();
  const b = controller.execute();

  assert.equal(requests.length, 2);
  assert.equal(requests[0].signal.aborted, true);
  assert.equal(requests[1].signal.aborted, false);
  assert.equal(typeof requests[0].signal.reason, "symbol");

  requests[1].pending.resolve({ items: ["B"] });
  assert.deepEqual(await b, { kind: "success", data: { items: ["B"] } });

  requests[0].pending.resolve({ items: ["A"] });
  await a;

  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["B"] } });
  assert.deepEqual(states, ["loading", "loading", "success"]);
});

test("A cannot publish after supersession even when it resolves before B", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: (signal) => {
      const pending = deferred();
      requests.push({ signal, pending });
      return pending.promise;
    },
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  const a = controller.execute();
  const b = controller.execute();
  requests[0].pending.resolve({ items: ["A"] });
  await a;

  assert.deepEqual(controller.current(), { kind: "loading" });
  assert.deepEqual(states, ["loading", "loading"]);

  requests[1].pending.resolve({ items: ["B"] });
  await b;
  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["B"] } });
});

test("obsolete reporting rejection publishes nothing", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: (signal) => {
      const pending = deferred();
      requests.push({ signal, pending });
      return pending.promise;
    },
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  const a = controller.execute();
  const b = controller.execute();
  requests[0].pending.reject(new ReportingCancellationFailure(requests[0].signal.reason));
  await a;

  assert.deepEqual(controller.current(), { kind: "loading" });
  assert.deepEqual(states, ["loading", "loading"]);

  requests[1].pending.resolve({ items: ["B"] });
  await b;
  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["B"] } });
});

test("obsolete programming defect cannot publish but its execution promise still rejects", async () => {
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

  const defect = new TypeError("obsolete bug");
  const a = controller.execute();
  const b = controller.execute();

  requests[1].resolve({ items: ["B"] });
  await b;
  requests[0].reject(defect);

  await assert.rejects(a, (error) => error === defect);
  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["B"] } });
  assert.deepEqual(states, ["loading", "loading", "success"]);
});

test("disposal aborts active execution and prevents all later publication", async () => {
  const request = deferred();
  let signal;
  const controller = createQueryLifecycleController({
    query: (executionSignal) => {
      signal = executionSignal;
      return request.promise;
    },
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  const execution = controller.execute();
  controller.dispose();

  assert.equal(signal.aborted, true);
  assert.equal(typeof signal.reason, "symbol");
  request.resolve({ items: ["late"] });
  await execution;

  assert.deepEqual(controller.current(), { kind: "loading" });
  assert.deepEqual(states, ["loading"]);
  await assert.rejects(controller.execute(), /disposed/);
});

test("superseded and disposed cancellation reasons are private identity values", async () => {
  const firstRequests = [];
  const first = createQueryLifecycleController({
    query: (signal) => {
      const pending = deferred();
      firstRequests.push({ signal, pending });
      return pending.promise;
    },
    isEmpty: () => false,
  });

  const firstExecution = first.execute();
  const secondExecution = first.execute();
  const supersededReason = firstRequests[0].signal.reason;

  const disposedRequest = deferred();
  let disposedSignal;
  const second = createQueryLifecycleController({
    query: (signal) => {
      disposedSignal = signal;
      return disposedRequest.promise;
    },
    isEmpty: () => false,
  });
  const disposedExecution = second.execute();
  second.dispose();
  const disposedReason = disposedSignal.reason;

  assert.equal(typeof supersededReason, "symbol");
  assert.equal(typeof disposedReason, "symbol");
  assert.notEqual(supersededReason, disposedReason);

  firstRequests[1].pending.resolve({ items: ["current"] });
  await secondExecution;
  firstRequests[0].pending.resolve({ items: ["obsolete"] });
  await firstExecution;
  disposedRequest.resolve({ items: ["disposed"] });
  await disposedExecution;
});
