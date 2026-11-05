import assert from "node:assert/strict";
import test from "node:test";

import { mapShiftPerformanceOverview } from "../src/presentation/shift-performance-projector.ts";
import { ShiftPresentationContractFailure } from "../src/presentation/shift-performance-model.ts";

const day = "2026-09-02";

function source(machineId, processorId, groupName, displayOrder, productionLineId = "line-a") {
  return {
    machineId,
    processorId,
    siteId: "site-a",
    productionLineId,
    displayName: `Machine ${machineId}`,
    groupName,
    displayOrder,
  };
}

function metric(metricKey, status = "calculated", overrides = {}) {
  const calculated = status === "calculated";
  return {
    metricKey,
    definitionVersion: "1.0",
    status,
    value: calculated ? "0.8" : null,
    unit: "Ratio",
    reasonCode: calculated ? null : "missing-input",
    reasonOperandName: calculated ? null : "ActualProductionTime",
    ...overrides,
  };
}

function report(sourceValue, shiftId, startsAtUtc, metrics = [], sourceRevision = null, businessDate = day) {
  return {
    processorId: sourceValue.processorId,
    machineId: sourceValue.machineId,
    productionDay: { siteId: sourceValue.siteId, businessDate },
    productionLineId: sourceValue.productionLineId,
    shift: {
      siteId: sourceValue.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId,
      startsAtUtc,
      endsAtUtc: startsAtUtc.replace(/T(00|08|16):/, (_, hour) => `T${String(Number(hour) + 8).padStart(2, "0")}:`),
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision,
    metrics,
  };
}

function expectFailure(reason, productionDay, sources, items) {
  assert.throws(
    () => mapShiftPerformanceOverview(productionDay, sources, { items }),
    error => error instanceof ShiftPresentationContractFailure && error.reason === reason,
  );
}

test("maps configured groups and machines in configured first-occurrence order", () => {
  const m1 = source("M1", "P1", "Line A", 10, "line-a");
  const m2 = source("M2", "P2", "Line A", 20, "line-a");
  const m3 = source("M3", "P3", "Line B", 30, "line-b");
  const result = {
    items: [
      report(m1, "Shift A", "2026-09-02T00:00:00Z"),
      report(m3, "Shift A", "2026-09-02T00:00:00Z"),
      report(m1, "Shift B", "2026-09-02T08:00:00Z"),
    ],
  };

  const overview = mapShiftPerformanceOverview(day, [m1, m2, m3], result);

  assert.equal(overview.productionDay, day);
  assert.deepEqual(overview.groups.map(group => group.groupName), ["Line A", "Line B"]);
  assert.deepEqual(overview.groups[0].machines.map(machine => machine.machineId), ["M1", "M2"]);
  assert.deepEqual(overview.groups[1].machines.map(machine => machine.machineId), ["M3"]);
  assert.deepEqual(overview.groups[0].machines[0].shifts.map(shift => shift.shift.shiftId), ["Shift A", "Shift B"]);
  assert.deepEqual(overview.groups[0].machines[1].shifts, []);
});

test("public mapper rejects out-of-order authority instead of sorting or repairing it", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const later = report(m1, "Shift B", "2026-09-02T08:00:00Z");
  const earlier = report(m1, "Shift A", "2026-09-02T00:00:00Z");
  expectFailure("out-of-order-occurrence", day, [m1], [later, earlier]);
});

test("unexpected reports cannot be silently discarded by replacement configuration", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const m2 = source("M2", "P2", "Line A", 1);
  expectFailure("unexpected-source", day, [m1], [report(m1, "Shift A", "2026-09-02T00:00:00Z"), report(m2, "Shift A", "2026-09-02T00:00:00Z")]);
});

test("validation uses the same production day rendered by the overview", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  expectFailure("unexpected-production-day", "2026-09-03", [m1], [report(m1, "Shift A", "2026-09-02T00:00:00Z")]);
});

test("validation uses the same configured population projected into groups", () => {
  const validatedSource = source("M1", "P1", "Line A", 0);
  const replacementSource = source("M1", "P1", "Replacement Group", 0, "line-b");
  expectFailure("unexpected-production-line", day, [replacementSource], [report(validatedSource, "Shift A", "2026-09-02T00:00:00Z")]);
});

test("later invalid report prevents any overview from being returned", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const m2 = source("M2", "P2", "Line A", 1);
  const valid = report(m1, "Shift A", "2026-09-02T00:00:00Z");
  const invalid = report(m2, "Shift A", "2026-09-02T00:00:00Z");
  expectFailure("unexpected-source", day, [m1], [valid, invalid]);
});

test("preserves exact shift descriptor production line and non-null source revision", () => {
  const m1 = source("M1", "projection-shifts", "Line A", 0);
  const revision = { machineId: "M1", processorId: "aggregation", streamKey: "metric-inputs", position: 73 };
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [], revision);
  const overview = mapShiftPerformanceOverview(day, [m1], { items: [item] });
  const projected = overview.groups[0].machines[0].shifts[0];

  assert.equal(projected.shift, item.shift);
  assert.equal(projected.productionLineId, item.productionLineId);
  assert.equal(projected.sourceRevision, revision);
});

test("preserves null revision distinctly while manufacturing five missing slots", () => {
  const m1 = source("M1", "P1", null, 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z");
  const projected = mapShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];

  assert.equal(projected.sourceRevision, null);
  for (const [property, metricKey] of [["availability", "availability"], ["utilization", "utilization.elr"], ["performance", "performance"], ["quality", "quality"], ["oee", "oee"]]) {
    assert.deepEqual(projected[property], { metricKey, version: "1.0", state: "missing" });
  }
});

test("maps partial metric evidence and manufactures only absent requested slots", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [
    metric("availability", "calculated", { value: "0.80" }),
    metric("performance", "unavailable", { reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" }),
    metric("quality", "insufficient-evidence", { reasonCode: "missing-counts", reasonOperandName: null }),
  ]);
  const projected = mapShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];

  assert.deepEqual(projected.availability, { metricKey: "availability", version: "1.0", state: "calculated", value: "0.80", unit: "Ratio" });
  assert.deepEqual(projected.performance, { metricKey: "performance", version: "1.0", state: "unavailable", reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" });
  assert.deepEqual(projected.quality, { metricKey: "quality", version: "1.0", state: "insufficient-evidence", reasonCode: "missing-counts", reasonOperandName: null });
  assert.deepEqual(projected.utilization, { metricKey: "utilization.elr", version: "1.0", state: "missing" });
  assert.deepEqual(projected.oee, { metricKey: "oee", version: "1.0", state: "missing" });
});

test("preserves authoritative OEE instead of recalculating it", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [
    metric("availability", "calculated", { value: "0.80" }),
    metric("performance", "calculated", { value: "0.50" }),
    metric("quality", "calculated", { value: "0.90" }),
    metric("oee", "calculated", { value: "0.37" }),
  ]);
  const projected = mapShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];
  assert.equal(projected.oee.value, "0.37");
});

test("supports an empty configured factory population", () => {
  assert.deepEqual(mapShiftPerformanceOverview(day, [], { items: [] }), { productionDay: day, groups: [] });
});
