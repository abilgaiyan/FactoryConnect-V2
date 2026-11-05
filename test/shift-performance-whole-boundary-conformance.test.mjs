import assert from "node:assert/strict";
import test from "node:test";

import { mapShiftPerformanceOverview } from "../src/presentation/shift-performance-projector.ts";
import { ShiftPresentationContractFailure } from "../src/presentation/shift-performance-model.ts";

const day = "2026-09-02";

function source(machineId, processorId, groupName, displayOrder, productionLineId) {
  return { machineId, processorId, siteId: "site-a", productionLineId, displayName: `Machine ${machineId}`, groupName, displayOrder };
}
function metric(metricKey, status = "calculated", overrides = {}) {
  const calculated = status === "calculated";
  return { metricKey, definitionVersion: "1.0", status, value: calculated ? "0.80" : null, unit: "Ratio", reasonCode: calculated ? null : "missing-input", reasonOperandName: calculated ? null : "ActualProductionTime", ...overrides };
}
function report(sourceValue, shiftId, startsAtUtc, endsAtUtc, metrics = [], sourceRevision = null, overrides = {}) {
  return { processorId: sourceValue.processorId, machineId: sourceValue.machineId, productionDay: { siteId: sourceValue.siteId, businessDate: day }, productionLineId: sourceValue.productionLineId, shift: { siteId: sourceValue.siteId, shiftScheduleAssignmentId: "assignment-a", shiftId, startsAtUtc, endsAtUtc }, context: { productionOrderId: null, operationId: null, partId: null, operatorId: null }, sourceRevision, metrics, ...overrides };
}
function expectFailure(reason, productionDay, sources, items) {
  assert.throws(() => mapShiftPerformanceOverview(productionDay, sources, { items }), error => error instanceof ShiftPresentationContractFailure && error.reason === reason);
}
function metricStates(shift) { return [shift.availability.state, shift.utilization.state, shift.performance.state, shift.quality.state, shift.oee.state]; }
function projectedMachines(overview) { return overview.groups.flatMap(group => group.machines); }

test("whole boundary preserves configured population, ordering, and all lineage states", () => {
  const m1 = source("M1", "projection-m1", "Line A", 10, "line-a");
  const m2 = source("M2", "projection-m2", "Line A", 20, "line-a");
  const m3 = source("M3", "projection-m3", "Line B", 30, "line-b");
  const revision = { machineId: "M3", processorId: "aggregation", streamKey: "metric-inputs", position: 73 };
  const m1Shift1 = report(m1, "shift-a", "2026-09-02T00:00:00Z", "2026-09-02T08:00:00Z");
  const m1Shift2 = report(m1, "shift-b", "2026-09-02T08:00:00Z", "2026-09-02T16:00:00Z", [metric("availability", "calculated", { value: "0.80" }), metric("performance", "calculated", { value: "0.50" }), metric("quality", "calculated", { value: "0.90" }), metric("oee", "calculated", { value: "0.37" })]);
  const m3Shift = report(m3, "shift-a", "2026-09-02T00:00:00+00:00", "2026-09-02T08:00:00+00:00", [], revision);
  const overview = mapShiftPerformanceOverview(day, [m1, m2, m3], { items: [m1Shift1, m3Shift, m1Shift2] });
  assert.equal(overview.productionDay, day);
  assert.deepEqual(overview.groups.map(group => group.groupName), ["Line A", "Line B"]);
  assert.deepEqual(overview.groups[0].machines.map(machine => machine.machineId), ["M1", "M2"]);
  assert.deepEqual(overview.groups[1].machines.map(machine => machine.machineId), ["M3"]);
  const projectedM1 = overview.groups[0].machines[0]; const projectedM2 = overview.groups[0].machines[1]; const projectedM3 = overview.groups[1].machines[0];
  assert.deepEqual(projectedM1.shifts.map(shift => shift.shift.shiftId), ["shift-a", "shift-b"]); assert.deepEqual(projectedM2.shifts, []);
  assert.equal(projectedM1.shifts[0].sourceRevision, null); assert.deepEqual(metricStates(projectedM1.shifts[0]), ["missing", "missing", "missing", "missing", "missing"]);
  assert.equal(projectedM3.shifts[0].sourceRevision, revision); assert.equal(projectedM3.shifts[0].shift, m3Shift.shift); assert.equal(projectedM3.shifts[0].productionLineId, "line-b"); assert.deepEqual(metricStates(projectedM3.shifts[0]), ["missing", "missing", "missing", "missing", "missing"]);
  assert.equal(projectedM1.shifts[1].availability.value, "0.80"); assert.equal(projectedM1.shifts[1].performance.value, "0.50"); assert.equal(projectedM1.shifts[1].quality.value, "0.90"); assert.equal(projectedM1.shifts[1].oee.value, "0.37"); assert.equal(projectedM1.shifts[1].utilization.state, "missing");
});

test("whole boundary supports configured populations of 0, 1, 7, and 50 without manufacturing truth", () => {
  for (const count of [0, 1, 7, 50]) {
    const sources = Array.from({ length: count }, (_, index) => source(`M${index + 1}`, `P${index + 1}`, `Line ${index % 3}`, index, `line-${index % 3}`));
    const overview = mapShiftPerformanceOverview(day, sources, { items: [] });
    const machines = projectedMachines(overview);
    assert.equal(machines.length, count, `population ${count}`);
    assert.equal(new Set(machines.map(machine => `${machine.machineId}\u0000${machine.processorId}`)).size, count, `unique population ${count}`);
    assert.deepEqual(overview.groups.map(group => group.groupName), [...new Set(sources.map(item => item.groupName))], `first-occurrence group order ${count}`);
    for (const group of overview.groups) {
      assert.deepEqual(group.machines.map(machine => machine.machineId), sources.filter(item => item.groupName === group.groupName).map(item => item.machineId), `relative machine order in ${group.groupName} population ${count}`);
    }
    assert.ok(machines.every(machine => machine.shifts.length === 0), `no manufactured occurrences ${count}`);
    assert.equal(overview.groups.reduce((total, group) => total + group.machines.length, 0), count, `deterministic population count ${count}`);
  }
});

test("whole boundary groups by first configured occurrence while preserving relative machine order", () => {
  const m1 = source("M1", "P1", "Line A", 10, "line-a");
  const m2 = source("M2", "P2", "Line B", 20, "line-b");
  const m3 = source("M3", "P3", "Line A", 30, "line-a");
  const overview = mapShiftPerformanceOverview(day, [m1, m2, m3], { items: [] });
  assert.deepEqual(overview.groups.map(group => group.groupName), ["Line A", "Line B"]);
  assert.deepEqual(overview.groups[0].machines.map(machine => machine.machineId), ["M1", "M3"]);
  assert.deepEqual(overview.groups[1].machines.map(machine => machine.machineId), ["M2"]);
  assert.equal(new Set(projectedMachines(overview).map(machine => machine.machineId)).size, 3);
});

test("whole boundary preserves authoritative evaluation states without normalization", () => {
  const m1 = source("M1", "projection-m1", "Line A", 0, "line-a");
  const item = report(m1, "shift-a", "2026-09-02T00:00:00Z", "2026-09-02T08:00:00Z", [metric("availability", "calculated", { value: "0.8000" }), metric("utilization.elr", "unavailable", { reasonCode: "missing-power-on", reasonOperandName: "PowerOn" }), metric("performance", "insufficient-evidence", { reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" })]);
  const shift = mapShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];
  assert.deepEqual(shift.availability, { metricKey: "availability", version: "1.0", state: "calculated", value: "0.8000", unit: "Ratio" });
  assert.deepEqual(shift.utilization, { metricKey: "utilization.elr", version: "1.0", state: "unavailable", reasonCode: "missing-power-on", reasonOperandName: "PowerOn" });
  assert.deepEqual(shift.performance, { metricKey: "performance", version: "1.0", state: "insufficient-evidence", reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" });
  assert.equal(shift.quality.state, "missing"); assert.equal(shift.oee.state, "missing");
});

test("whole boundary rejects malformed authority and publishes no partial model", () => {
  const m1 = source("M1", "projection-m1", "Line A", 0, "line-a");
  const valid = report(m1, "shift-a", "2026-09-02T00:00:00Z", "2026-09-02T08:00:00Z");
  const invalid = report(m1, "shift-b", "2026-09-02T08:00:00Z", "2026-09-02T16:00:00Z", [metric("oee", "calculated", { value: null })]);
  expectFailure("malformed-metric-state", day, [m1], [valid, invalid]);
});

test("whole boundary rejects out-of-order authority rather than reconstructing shift order", () => {
  const m1 = source("M1", "projection-m1", "Line A", 0, "line-a");
  const later = report(m1, "shift-b", "2026-09-02T08:00:00Z", "2026-09-02T16:00:00Z"); const earlier = report(m1, "shift-a", "2026-09-02T00:00:00Z", "2026-09-02T08:00:00Z");
  expectFailure("out-of-order-occurrence", day, [m1], [later, earlier]);
});

test("whole boundary rejects authority from outside the configured population", () => {
  const configured = source("M1", "projection-m1", "Line A", 0, "line-a"); const foreign = source("M2", "projection-m2", "Line B", 1, "line-b");
  expectFailure("unexpected-source", day, [configured], [report(foreign, "shift-a", "2026-09-02T00:00:00Z", "2026-09-02T08:00:00Z")]);
});

test("whole boundary contains no current-state inference for configured machines without reports", () => {
  const m1 = source("M1", "projection-m1", "Line A", 0, "line-a"); const machine = mapShiftPerformanceOverview(day, [m1], { items: [] }).groups[0].machines[0];
  assert.deepEqual(Object.keys(machine).sort(), ["displayName", "machineId", "processorId", "productionLineId", "shifts", "siteId"]); assert.deepEqual(machine.shifts, []);
});
