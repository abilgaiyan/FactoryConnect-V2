import assert from "node:assert/strict";
import test from "node:test";

import { ProductionDayPresentationFailure } from "../src/application/production-day-presentation.ts";
import { composeDailyReport } from "../src/presentation/daily-report-composer.ts";
import { ShiftPresentationContractFailure } from "../src/presentation/shift-performance-model.ts";

const day = "2026-09-09";

function source(index, overrides = {}) {
  return {
    machineId: `M${index}`,
    processorId: `P${index}`,
    siteId: "site-a",
    productionLineId: index % 2 === 0 ? "line-b" : "line-a",
    displayName: `Machine ${index}`,
    groupName: index % 2 === 0 ? "Line B" : "Line A",
    displayOrder: index * 10,
    ...overrides,
  };
}

function productionDayItem(configuredSource, metricKey = "availability", overrides = {}) {
  return {
    scope: "production-day",
    processorId: configuredSource.processorId,
    machineId: configuredSource.machineId,
    shift: null,
    productionDay: { siteId: configuredSource.siteId, businessDate: day },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    metricKey,
    definitionVersion: "1.0",
    status: "calculated",
    value: "0.80",
    unit: "Ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: configuredSource.processorId,
      machineId: configuredSource.machineId,
      streamKey: `stream-${configuredSource.processorId}`,
      position: "41",
    },
    ...overrides,
  };
}

function shiftMetric(metricKey, status = "calculated", overrides = {}) {
  return {
    metricKey,
    definitionVersion: "1.0",
    status,
    value: status === "calculated" ? "0.75" : null,
    unit: "Ratio",
    reasonCode: status === "calculated" ? null : "missing-input",
    reasonOperandName: status === "calculated" ? null : "ActualProductionTime",
    ...overrides,
  };
}

function shiftReport(configuredSource, shiftId, startsAtUtc, metrics = [], overrides = {}) {
  return {
    processorId: configuredSource.processorId,
    machineId: configuredSource.machineId,
    productionDay: { siteId: configuredSource.siteId, businessDate: day },
    productionLineId: configuredSource.productionLineId,
    shift: {
      siteId: configuredSource.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId,
      startsAtUtc,
      endsAtUtc: startsAtUtc === "2026-09-09T00:00:00Z"
        ? "2026-09-09T08:00:00Z"
        : "2026-09-09T16:00:00Z",
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision: null,
    metrics,
    ...overrides,
  };
}

function compose(sources, productionDayItems = [], shiftItems = []) {
  return composeDailyReport({
    productionDay: day,
    sources,
    productionDayResult: { items: productionDayItems },
    shiftResult: { items: shiftItems },
  });
}

function flattenMachines(model) {
  return model.groups.flatMap(group => group.machines);
}

test("empty configured factory produces an empty deterministic report", () => {
  assert.deepEqual(compose([]), { productionDay: day, groups: [] });
});

test("configured ordering is retained and absent production-day evaluations manufacture exactly five missing cells", () => {
  const sources = [
    source(1, { groupName: "Line A" }),
    source(2, { groupName: "Line B" }),
    source(3, { groupName: "Line A" }),
  ];

  const model = compose(sources);
  assert.deepEqual(model.groups.map(group => group.groupName), ["Line A", "Line B"]);
  assert.deepEqual(flattenMachines(model).map(machine => machine.machineId), ["M1", "M3", "M2"]);

  for (const machine of flattenMachines(model)) {
    assert.deepEqual(machine.productionDayCells.map(cell => cell.metricKey), [
      "availability", "utilization.elr", "performance", "quality", "oee",
    ]);
    assert.deepEqual(machine.productionDayCells.map(cell => cell.state), [
      "missing", "missing", "missing", "missing", "missing",
    ]);
  }
});

test("calculated, unavailable, insufficient-evidence, and missing production-day states are preserved without arithmetic", () => {
  const configured = source(1);
  const model = compose([configured], [
    productionDayItem(configured, "availability", { value: 0 }),
    productionDayItem(configured, "performance", {
      status: "unavailable",
      value: null,
      reasonCode: "missing-reference-time",
      reasonOperandName: "ReferenceTime",
    }),
    productionDayItem(configured, "quality", {
      status: "insufficient-evidence",
      value: null,
      reasonCode: "missing-counts",
      reasonOperandName: null,
    }),
    productionDayItem(configured, "oee", { value: "0.37" }),
  ]);

  const cells = flattenMachines(model)[0].productionDayCells;
  assert.deepEqual(cells.map(cell => cell.state), [
    "calculated", "missing", "unavailable", "insufficient-evidence", "calculated",
  ]);
  assert.equal(cells[0].value, 0);
  assert.equal(cells[4].value, "0.37");
});

test("retains supplied production-day and roster-owned shift lineage", () => {
  const configured = source(1);
  const productionDay = productionDayItem(configured, "availability");
  const shiftRevision = {
    processorId: configured.processorId,
    machineId: configured.machineId,
    streamKey: "shift-stream",
    position: 73,
  };
  const occurrence = shiftReport(
    configured,
    "Shift A",
    "2026-09-09T00:00:00Z",
    [],
    { sourceRevision: shiftRevision },
  );

  const model = compose([configured], [productionDay], [occurrence]);
  const machine = model.groups[0].machines[0];

  assert.equal(machine.productionDayCells[0].sourceRevision, productionDay.sourceRevision);
  assert.equal(machine.shifts[0].sourceRevision, shiftRevision);
});

test("covered zero occurrences remains distinct from a roster-owned occurrence with five missing shift cells", () => {
  const first = source(1, { groupName: null });
  const second = source(2, { groupName: null });
  const occurrence = shiftReport(second, "Shift A", "2026-09-09T00:00:00Z");

  const model = compose([first, second], [], [occurrence]);
  const [firstMachine, secondMachine] = model.groups[0].machines;

  assert.deepEqual(firstMachine.shifts, []);
  assert.equal(secondMachine.shifts.length, 1);
  assert.equal(secondMachine.shifts[0].shift, occurrence.shift);
  assert.deepEqual(secondMachine.shifts[0].cells.map(cell => cell.state), [
    "missing", "missing", "missing", "missing", "missing",
  ]);
});

test("partial shift metric authority preserves all four cell states and authoritative OEE", () => {
  const configured = source(1);
  const occurrence = shiftReport(configured, "Shift A", "2026-09-09T00:00:00Z", [
    shiftMetric("availability", "calculated", { value: "0.80" }),
    shiftMetric("performance", "unavailable", {
      reasonCode: "missing-reference-time",
      reasonOperandName: "ReferenceTime",
    }),
    shiftMetric("quality", "insufficient-evidence", {
      reasonCode: "missing-counts",
      reasonOperandName: null,
    }),
    shiftMetric("oee", "calculated", { value: "0.31" }),
  ]);

  const cells = compose([configured], [], [occurrence]).groups[0].machines[0].shifts[0].cells;
  assert.deepEqual(cells.map(cell => cell.state), [
    "calculated", "missing", "unavailable", "insufficient-evidence", "calculated",
  ]);
  assert.equal(cells[4].value, "0.31");
});

test("production-day authority violations fail closed rather than filtering or repairing input", () => {
  const configured = source(1);
  const unexpected = source(2);

  assert.throws(
    () => compose([configured], [productionDayItem(unexpected)], []),
    error => error instanceof ProductionDayPresentationFailure && error.reason === "unexpected-source",
  );
});

test("duplicate production-day identities fail closed", () => {
  const configured = source(1);
  const duplicate = productionDayItem(configured);

  assert.throws(
    () => compose([configured], [duplicate, { ...duplicate }], []),
    error => error instanceof ProductionDayPresentationFailure && error.reason === "duplicate-result",
  );
});

test("shift authority ordering violations fail closed rather than sorting occurrences", () => {
  const configured = source(1);
  const later = shiftReport(configured, "Shift B", "2026-09-09T08:00:00Z");
  const earlier = shiftReport(configured, "Shift A", "2026-09-09T00:00:00Z");

  assert.throws(
    () => compose([configured], [], [later, earlier]),
    error => error instanceof ShiftPresentationContractFailure && error.reason === "out-of-order-occurrence",
  );
});
