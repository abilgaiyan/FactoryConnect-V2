import assert from "node:assert/strict";
import test from "node:test";

import { validateProductionDayShiftAuthority } from "../src/presentation/shift-performance-authority-validator.ts";
import { ShiftPresentationContractFailure } from "../src/presentation/shift-performance-model.ts";

const day = "2026-09-02";
const sourceA = source("00000000-0000-0000-0000-000000000001", "processor-a", "site-a", "line-a");
const sourceB = source("00000000-0000-0000-0000-000000000002", "processor-b", "site-a", "line-a");

function source(machineId, processorId, siteId, productionLineId) {
  return { machineId, processorId, siteId, productionLineId, displayName: machineId, groupName: null, displayOrder: 0 };
}

function metric(overrides = {}) {
  return {
    metricKey: "availability",
    definitionVersion: "1.0",
    status: "calculated",
    value: "0.8",
    unit: "Ratio",
    reasonCode: null,
    reasonOperandName: null,
    ...overrides,
  };
}

function report(sourceValue = sourceA, overrides = {}) {
  const base = {
    processorId: sourceValue.processorId,
    machineId: sourceValue.machineId,
    productionDay: { siteId: sourceValue.siteId, businessDate: day },
    productionLineId: sourceValue.productionLineId,
    shift: {
      siteId: sourceValue.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId: "shift-a",
      startsAtUtc: "2026-09-02T00:00:00Z",
      endsAtUtc: "2026-09-02T08:00:00Z",
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision: null,
    metrics: [metric()],
  };
  return { ...base, ...overrides };
}

function expectFailure(reason, sources, items) {
  assert.throws(
    () => validateProductionDayShiftAuthority(day, sources, { items }),
    (error) => error instanceof ShiftPresentationContractFailure && error.reason === reason,
  );
}

test("accepts valid same-source occurrences and preserves original result items", () => {
  const first = report();
  const second = report(sourceA, { shift: { ...first.shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T08:00:00Z", endsAtUtc: "2026-09-02T16:00:00Z" } });
  const items = [first, second];
  const validated = validateProductionDayShiftAuthority(day, [sourceA], { items });
  assert.equal(validated.items, items);
});

test("accepts aggregation revision processor distinct from reporting processor and preserves revision", () => {
  const revision = {
    machineId: sourceA.machineId,
    processorId: "aggregation",
    streamKey: "metric-inputs",
    position: 73,
  };
  const item = report(sourceA, {
    processorId: "projection-shifts",
    sourceRevision: revision,
  });
  const configured = { ...sourceA, processorId: "projection-shifts" };
  const validated = validateProductionDayShiftAuthority(day, [configured], { items: [item] });
  assert.equal(validated.items[0].sourceRevision, revision);
});

test("accepts interleaved sources while validating each source relative order", () => {
  const a1 = report(sourceA);
  const b1 = report(sourceB);
  const a2 = report(sourceA, { shift: { ...a1.shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T08:00:00Z", endsAtUtc: "2026-09-02T16:00:00Z" } });
  assert.doesNotThrow(() => validateProductionDayShiftAuthority(day, [sourceA, sourceB], { items: [a1, b1, a2] }));
});

test("accepts +00:00 ordered occurrences", () => {
  const first = report(sourceA, { shift: { ...report().shift, startsAtUtc: "2026-09-02T00:00:00+00:00", endsAtUtc: "2026-09-02T08:00:00+00:00" } });
  const second = report(sourceA, { shift: { ...report().shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T08:00:00+00:00", endsAtUtc: "2026-09-02T16:00:00+00:00" } });
  assert.doesNotThrow(() => validateProductionDayShiftAuthority(day, [sourceA], { items: [first, second] }));
});

test("treats Z and +00:00 as equivalent instants for ordering", () => {
  const first = report(sourceA, { shift: { ...report().shift, shiftId: "shift-a", startsAtUtc: "2026-09-02T00:00:00Z", endsAtUtc: "2026-09-02T08:00:00Z" } });
  const second = report(sourceA, { shift: { ...report().shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T00:00:00+00:00", endsAtUtc: "2026-09-02T08:00:00+00:00" } });
  assert.doesNotThrow(() => validateProductionDayShiftAuthority(day, [sourceA], { items: [first, second] }));
});

test("rejects non-zero UTC offsets", () => {
  const item = report(sourceA, { shift: { ...report().shift, startsAtUtc: "2026-09-02T00:00:00+05:30" } });
  expectFailure("inconsistent-occurrence-descriptor", [sourceA], [item]);
});

test("rejects RFC 3339 unknown local offset", () => {
  const item = report(sourceA, { shift: { ...report().shift, startsAtUtc: "2026-09-02T00:00:00-00:00" } });
  expectFailure("inconsistent-occurrence-descriptor", [sourceA], [item]);
});

test("rejects year zero outside DateTimeOffset domain", () => {
  const item = report(sourceA, { shift: { ...report().shift, startsAtUtc: "0000-09-02T00:00:00Z" } });
  expectFailure("inconsistent-occurrence-descriptor", [sourceA], [item]);
});

test("rejects fractional precision beyond DateTimeOffset ticks", () => {
  const item = report(sourceA, { shift: { ...report().shift, startsAtUtc: "2026-09-02T00:00:00.12345678Z" } });
  expectFailure("inconsistent-occurrence-descriptor", [sourceA], [item]);
});

test("compares UTC instants exactly across differing fractional precision", () => {
  const first = report(sourceA, { shift: { ...report().shift, startsAtUtc: "2026-09-02T00:00:00.9Z", endsAtUtc: "2026-09-02T01:00:00Z" } });
  const second = report(sourceA, { shift: { ...report().shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T00:00:00.10Z", endsAtUtc: "2026-09-02T02:00:00Z" } });
  expectFailure("out-of-order-occurrence", [sourceA], [first, second]);
});

test("rejects unexpected source", () => expectFailure("unexpected-source", [sourceA], [report(sourceB)]));
test("rejects duplicate configured source", () => expectFailure("unexpected-source", [sourceA, sourceA], []));
test("rejects wrong production day", () => expectFailure("unexpected-production-day", [sourceA], [report(sourceA, { productionDay: { siteId: "site-a", businessDate: "2026-09-01" } })]));
test("rejects wrong site", () => expectFailure("unexpected-site", [sourceA], [report(sourceA, { productionDay: { siteId: "site-b", businessDate: day } })]));
test("rejects wrong production line", () => expectFailure("unexpected-production-line", [sourceA], [report(sourceA, { productionLineId: "line-b" })]));
test("rejects partitioned context", () => expectFailure("unexpected-context", [sourceA], [report(sourceA, { context: { productionOrderId: "order-a", operationId: null, partId: null, operatorId: null } })]));
test("rejects foreign source revision", () => expectFailure("unexpected-source-revision", [sourceA], [report(sourceA, { sourceRevision: { machineId: sourceB.machineId, processorId: "aggregation", streamKey: "stream", position: 1 } })]));

test("rejects duplicate occurrence", () => {
  const item = report();
  expectFailure("duplicate-occurrence", [sourceA], [item, { ...item }]);
});

test("rejects conflicting occurrence descriptor", () => {
  const first = report();
  const conflicting = report(sourceA, { shift: { ...first.shift, startsAtUtc: "2026-09-02T00:00:01Z" } });
  expectFailure("inconsistent-occurrence-descriptor", [sourceA], [first, conflicting]);
});

test("rejects duplicate metric", () => {
  expectFailure("duplicate-metric", [sourceA], [report(sourceA, { metrics: [metric(), metric()] })]);
});

test("rejects wrong metric key or version", () => {
  expectFailure("unexpected-metric", [sourceA], [report(sourceA, { metrics: [metric({ metricKey: "Efficiency" })] })]);
  expectFailure("unexpected-metric", [sourceA], [report(sourceA, { metrics: [metric({ definitionVersion: "2.0" })] })]);
});

test("rejects calculated metric with null value", () => expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ value: null })] })]));
test("rejects calculated metric with reason evidence", () => expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ reasonCode: "missing-input", reasonOperandName: "ActualProductionTime" })] })]));
test("rejects non-calculated metric with non-null value", () => expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ status: "unavailable", value: 0.25, reasonCode: "missing-input" })] })]));
test("rejects non-calculated metric without reason code", () => expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ status: "insufficient-evidence", value: null })] })]));

test("rejects empty unit for every status", () => {
  expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ unit: "" })] })]);
  expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ status: "unavailable", value: null, unit: "", reasonCode: "missing-input" })] })]);
  expectFailure("malformed-metric-state", [sourceA], [report(sourceA, { metrics: [metric({ status: "insufficient-evidence", value: null, unit: "", reasonCode: "missing-input" })] })]);
});

test("rejects out-of-order occurrences without sorting or repair", () => {
  const later = report(sourceA, { shift: { ...report().shift, shiftId: "shift-b", startsAtUtc: "2026-09-02T08:00:00Z", endsAtUtc: "2026-09-02T16:00:00Z" } });
  expectFailure("out-of-order-occurrence", [sourceA], [later, report()]);
});

test("whole-result validation fails when an invalid report follows valid reports", () => {
  const valid = report();
  const invalid = report(sourceB);
  expectFailure("unexpected-source", [sourceA], [valid, invalid]);
});
