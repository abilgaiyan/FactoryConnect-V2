import assert from "node:assert/strict";
import test from "node:test";

import { ReportingProtocolFailure } from "../src/api/reporting/index.ts";
import {
  ProductionDayShiftReportingTraversalFailure,
  queryAuthoritativeProductionDayShifts,
} from "../src/application/production-day-shift-reporting.ts";

const productionDay = "2026-09-02";
const configuredSources = [{
  machineId: "11111111-1111-1111-1111-111111111111",
  processorId: "operational-metrics",
  siteId: "site-1",
  displayName: "Machine 1",
  groupName: "Line 1",
  displayOrder: 0,
}];

function item(shiftId) {
  return {
    processorId: "operational-metrics",
    machineId: configuredSources[0].machineId,
    productionDay: { siteId: "site-1", businessDate: productionDay },
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

test("traverses every page and publishes one complete authoritative result", async () => {
  const tokens = ["opaque+/=one", "opaque.two.do-not-decode", null];
  const requests = [];
  let pageIndex = 0;
  const client = {
    async queryProductionDayShiftMetrics(request) {
      requests.push(request);
      const current = pageIndex++;
      return {
        items: [item(`shift-${current + 1}`)],
        continuationToken: tokens[current],
      };
    },
  };

  const result = await queryAuthoritativeProductionDayShifts(
    productionDay,
    configuredSources,
    client,
  );

  assert.deepEqual(result.items.map(({ shift }) => shift.shiftId), ["shift-1", "shift-2", "shift-3"]);
  assert.deepEqual(requests.map(({ continuationToken }) => continuationToken), [
    null,
    "opaque+/=one",
    "opaque.two.do-not-decode",
  ]);
});

test("passes the exact same caller AbortSignal to every page", async () => {
  const controller = new AbortController();
  const observedSignals = [];
  let page = 0;
  const client = {
    async queryProductionDayShiftMetrics(_request, options) {
      observedSignals.push(options.signal);
      page += 1;
      return {
        items: [],
        continuationToken: page === 1 ? "next" : null,
      };
    },
  };

  await queryAuthoritativeProductionDayShifts(
    productionDay,
    configuredSources,
    client,
    { signal: controller.signal },
  );

  assert.deepEqual(observedSignals, [controller.signal, controller.signal]);
});

test("zero configured sources is authoritative empty and performs no reporting call", async () => {
  let calls = 0;
  const result = await queryAuthoritativeProductionDayShifts(productionDay, [], {
    async queryProductionDayShiftMetrics() {
      calls += 1;
      throw new Error("must not be called");
    },
  });

  assert.deepEqual(result, { items: [] });
  assert.equal(calls, 0);
});

test("rejects repeated continuation tokens as a reporting protocol failure", async () => {
  let calls = 0;
  const client = {
    async queryProductionDayShiftMetrics() {
      calls += 1;
      return { items: [], continuationToken: "same-token" };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDayShifts(productionDay, configuredSources, client),
    (failure) => failure instanceof ReportingProtocolFailure
      && failure.cause instanceof ProductionDayShiftReportingTraversalFailure
      && failure.cause.reason === "continuation-cycle",
  );
  assert.equal(calls, 2);
});

test("enforces the 100-page traversal limit before issuing page 101", async () => {
  let calls = 0;
  const client = {
    async queryProductionDayShiftMetrics() {
      calls += 1;
      return { items: [], continuationToken: `token-${calls}` };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDayShifts(productionDay, configuredSources, client),
    (failure) => failure instanceof ReportingProtocolFailure
      && failure.cause instanceof ProductionDayShiftReportingTraversalFailure
      && failure.cause.reason === "page-limit-exceeded",
  );
  assert.equal(calls, 100);
});

test("a later-page failure rejects the traversal instead of returning accumulated partial data", async () => {
  const expected = new Error("page two failed");
  let calls = 0;
  const client = {
    async queryProductionDayShiftMetrics() {
      calls += 1;
      if (calls === 1) {
        return { items: [item("shift-1")], continuationToken: "next" };
      }
      throw expected;
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDayShifts(productionDay, configuredSources, client),
    (failure) => failure === expected,
  );
  assert.equal(calls, 2);
});

test("cancellation during a page prevents a subsequent page request", async () => {
  const controller = new AbortController();
  const reason = new Error("selection superseded");
  let calls = 0;
  const client = {
    async queryProductionDayShiftMetrics(_request, options) {
      calls += 1;
      controller.abort(reason);
      options.signal.throwIfAborted();
      return { items: [], continuationToken: "must-not-be-followed" };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDayShifts(
      productionDay,
      configuredSources,
      client,
      { signal: controller.signal },
    ),
    (failure) => failure === reason,
  );
  assert.equal(calls, 1);
});
