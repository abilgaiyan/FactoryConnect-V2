import assert from "node:assert/strict";
import test from "node:test";

import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingInvalidQueryFailure,
  ReportingNetworkFailure,
} from "../src/api/reporting/index.ts";
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

function problemDetails() {
  return {
    type: "urn:factoryconnect:problem:reporting:invalid-request",
    title: "Invalid reporting query",
    status: 400,
    detail: "The query is invalid.",
    instance: null,
  };
}

test("success refresh retains the last completed result until replacement succeeds", async () => {
  const refresh = deferred();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? { items: ["A"] } : refresh.promise,
    isEmpty: (data) => data.items.length === 0,
  });

  await controller.execute();
  const execution = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: { items: ["A"] } });

  refresh.resolve({ items: ["B"] });
  assert.deepEqual(await execution, { kind: "success", data: { items: ["B"] } });
});

test("authoritative empty result is retained while refreshing", async () => {
  const refresh = deferred();
  let call = 0;
  const empty = { items: [] };
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? empty : refresh.promise,
    isEmpty: (data) => data.items.length === 0,
  });

  assert.deepEqual(await controller.execute(), { kind: "empty", data: empty });
  const execution = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: empty });
  refresh.resolve({ items: ["B"] });
  await execution;
});

test("success undefined is retained by state presence while refreshing", async () => {
  const refresh = deferred();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? undefined : refresh.promise,
    isEmpty: () => false,
  });

  assert.deepEqual(await controller.execute(), { kind: "success", data: undefined });
  const execution = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: undefined });
  refresh.resolve("replacement");
  await execution;
});

test("empty undefined is retained by state presence while refreshing", async () => {
  const refresh = deferred();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? undefined : refresh.promise,
    isEmpty: (data) => data === undefined,
  });

  assert.deepEqual(await controller.execute(), { kind: "empty", data: undefined });
  const execution = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: undefined });
  refresh.resolve("replacement");
  await execution;
});

test("repeated refresh cancels the current generation and retains the original completed result", async () => {
  const requests = [];
  let call = 0;
  const controller = createQueryLifecycleController({
    query: (signal) => {
      if (++call === 1) {
        return Promise.resolve({ items: ["A"] });
      }
      const pending = deferred();
      requests.push({ signal, pending });
      return pending.promise;
    },
    isEmpty: () => false,
  });

  await controller.execute();
  const b = controller.execute();
  const c = controller.execute();
  assert.equal(requests[0].signal.aborted, true);
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: { items: ["A"] } });

  requests[1].pending.resolve({ items: ["C"] });
  await c;
  requests[0].pending.resolve({ items: ["B"] });
  await b;
  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["C"] } });
});

test("refresh failure terminates previous-result retention", async () => {
  const failure = new ReportingNetworkFailure(new Error("offline"));
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? { items: ["A"] } : Promise.reject(failure),
    isEmpty: () => false,
  });

  await controller.execute();
  const result = await controller.execute();
  assert.deepEqual(result, { kind: "failed", failure });
  assert.equal(Object.hasOwn(result, "previous"), false);
});

test("refresh invalid request terminates previous-result retention", async () => {
  const details = problemDetails();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? { items: ["A"] } : Promise.reject(new ReportingInvalidQueryFailure(details)),
    isEmpty: () => false,
  });

  await controller.execute();
  const result = await controller.execute();
  assert.deepEqual(result, { kind: "invalidRequest", details });
});

test("refresh coverage failure terminates previous-result retention", async () => {
  let call = 0;
  const details = { machineId: "machine-1", siteId: "site-1", businessDate: "2026-09-03" };
  const controller = createQueryLifecycleController({
    query: async () => {
      if (++call === 1) {
        return { items: ["A"] };
      }
      throw new ProductionDayShiftRosterCoverageRequiredFailure(
        {
          type: "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required",
          title: "Roster coverage required",
          status: 409,
          detail: "Coverage is required.",
          instance: null,
        },
        details,
      );
    },
    isEmpty: () => false,
  });

  await controller.execute();
  const result = await controller.execute();
  assert.deepEqual(result, { kind: "coverageRequired", details });
});
