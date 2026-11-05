import assert from "node:assert/strict";
import test from "node:test";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { deriveShiftPerformancePageState } from "../src/application/shift-performance-page-state.ts";

const day = "2026-09-03";

function source(overrides = {}) {
  return {
    machineId: "M1",
    processorId: "P1",
    siteId: "site-a",
    productionLineId: "line-a",
    displayName: "Machine 1",
    groupName: "Line A",
    displayOrder: 0,
    ...overrides,
  };
}

function report(sourceValue, metrics = []) {
  return {
    processorId: sourceValue.processorId,
    machineId: sourceValue.machineId,
    productionDay: { siteId: sourceValue.siteId, businessDate: day },
    productionLineId: sourceValue.productionLineId,
    shift: {
      siteId: sourceValue.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId: "Shift A",
      startsAtUtc: "2026-09-03T00:00:00Z",
      endsAtUtc: "2026-09-03T08:00:00Z",
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision: null,
    metrics,
  };
}

function metric(metricKey, value) {
  return {
    metricKey,
    definitionVersion: "1.0",
    status: "calculated",
    value,
    unit: "Ratio",
    reasonCode: null,
    reasonOperandName: null,
  };
}

function malformedResult(configured) {
  const malformed = report(configured);
  malformed.productionLineId = "wrong-line";
  return { items: [malformed] };
}

test("idle and loading derive loading without manufacturing reporting data", () => {
  assert.deepEqual(deriveShiftPerformancePageState({ kind: "idle" }, day, []), { kind: "loading", productionDay: day });
  assert.deepEqual(deriveShiftPerformancePageState({ kind: "loading" }, day, []), { kind: "loading", productionDay: day });
});

test("success and authoritative empty both map through the presentation boundary", () => {
  const configured = source();
  const success = deriveShiftPerformancePageState({ kind: "success", data: { items: [report(configured)] } }, day, [configured]);
  assert.equal(success.kind, "success");
  assert.equal(success.isRefreshing, false);
  assert.equal(success.overview.groups[0].machines[0].shifts.length, 1);

  const empty = deriveShiftPerformancePageState({ kind: "empty", data: { items: [] } }, day, [configured]);
  assert.equal(empty.kind, "success");
  assert.equal(empty.isRefreshing, false);
  assert.equal(empty.overview.groups[0].machines[0].shifts.length, 0);
});

test("refreshing maps only the exact previous complete authoritative result", () => {
  const configured = source();
  const previous = { items: [report(configured)] };
  const state = deriveShiftPerformancePageState({ kind: "refreshing", previous }, day, [configured]);
  assert.equal(state.kind, "success");
  assert.equal(state.isRefreshing, true);
  assert.equal(state.overview.groups[0].machines[0].shifts[0].shift.shiftId, "Shift A");
});

test("configured empty factory and covered machine with zero occurrences remain distinct valid overviews", () => {
  const factory = deriveShiftPerformancePageState({ kind: "empty", data: { items: [] } }, day, []);
  assert.equal(factory.kind, "success");
  assert.deepEqual(factory.overview.groups, []);

  const configured = source();
  const covered = deriveShiftPerformancePageState({ kind: "empty", data: { items: [] } }, day, [configured]);
  assert.equal(covered.kind, "success");
  assert.equal(covered.overview.groups.length, 1);
  assert.equal(covered.overview.groups[0].machines[0].shifts.length, 0);
});

test("authoritative OEE value remains 0.37 and is never recomputed from operands", () => {
  const configured = source();
  const result = {
    items: [report(configured, [
      metric("availability", "0.80"),
      metric("performance", "0.50"),
      metric("quality", "0.90"),
      metric("oee", "0.37"),
    ])],
  };
  const state = deriveShiftPerformancePageState({ kind: "success", data: result }, day, [configured]);
  assert.equal(state.kind, "success");
  assert.equal(state.overview.groups[0].machines[0].shifts[0].oee.value, "0.37");
});

test("invalid request and coverage identity are classified without reconstructing identity", () => {
  const invalid = deriveShiftPerformancePageState({
    kind: "invalidRequest",
    details: { type: "invalid", title: "Invalid", status: 400, detail: "bad day", instance: null },
  }, day, []);
  assert.deepEqual(invalid, { kind: "invalid-request", productionDay: day, message: "bad day" });

  const coverage = deriveShiftPerformancePageState({
    kind: "coverageRequired",
    details: { machineId: "M-raw", siteId: "SITE-raw", businessDate: "2026-09-03" },
  }, day, []);
  assert.deepEqual(coverage, {
    kind: "roster-coverage-required",
    productionDay: day,
    machineId: "M-raw",
    siteId: "SITE-raw",
    businessDate: "2026-09-03",
  });
});

test("reporting failure becomes controlled transport failure without retaining stale presentation", () => {
  const failure = new ReportingNetworkFailure(new Error("offline"));
  const state = deriveShiftPerformancePageState({ kind: "failed", failure }, day, []);
  assert.deepEqual(state, {
    kind: "transport-failure",
    productionDay: day,
    message: "Shift performance reporting is unavailable. Please try again.",
  });
  assert.equal(Object.hasOwn(state, "overview"), false);
});

test("known presentation contract violations are contained as controlled page failures", () => {
  const configured = source();
  const state = deriveShiftPerformancePageState(
    { kind: "success", data: malformedResult(configured) },
    day,
    [configured],
  );
  assert.equal(state.kind, "presentation-contract-failure");
  assert.equal(state.productionDay, day);
  assert.equal(state.isRefreshing, false);
  assert.equal(Object.hasOwn(state, "overview"), false);
});

test("refreshing a presentation contract violation preserves only controlled failure plus lifecycle activity", () => {
  const configured = source();
  const previous = malformedResult(configured);
  const state = deriveShiftPerformancePageState(
    { kind: "refreshing", previous },
    day,
    [configured],
  );

  assert.equal(state.kind, "presentation-contract-failure");
  assert.equal(state.productionDay, day);
  assert.equal(state.isRefreshing, true);
  assert.equal(Object.hasOwn(state, "overview"), false);
  assert.equal(Object.hasOwn(state, "previous"), false);
  assert.equal(Object.hasOwn(state, "data"), false);
});

test("presentation failure refresh terminates according to the new lifecycle outcome", () => {
  const configured = source();
  const malformed = malformedResult(configured);
  const refreshing = deriveShiftPerformancePageState({ kind: "refreshing", previous: malformed }, day, [configured]);
  assert.equal(refreshing.kind, "presentation-contract-failure");
  assert.equal(refreshing.isRefreshing, true);

  const valid = deriveShiftPerformancePageState(
    { kind: "success", data: { items: [report(configured)] } },
    day,
    [configured],
  );
  assert.equal(valid.kind, "success");
  assert.equal(valid.isRefreshing, false);

  const invalidAgain = deriveShiftPerformancePageState({ kind: "success", data: malformed }, day, [configured]);
  assert.equal(invalidAgain.kind, "presentation-contract-failure");
  assert.equal(invalidAgain.isRefreshing, false);

  const failure = new ReportingNetworkFailure(new Error("offline"));
  const failed = deriveShiftPerformancePageState({ kind: "failed", failure }, day, [configured]);
  assert.equal(failed.kind, "transport-failure");
  assert.equal(Object.hasOwn(failed, "overview"), false);
});

test("unexpected mapper defects propagate unchanged", () => {
  const defect = new Error("programming defect");
  const result = new Proxy(
    { items: [] },
    {
      get() {
        throw defect;
      },
    },
  );

  assert.throws(
    () => deriveShiftPerformancePageState(
      { kind: "success", data: result },
      day,
      [source()],
    ),
    error => error === defect,
  );
});
