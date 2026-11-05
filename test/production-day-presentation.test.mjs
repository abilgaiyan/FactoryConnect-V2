import assert from "node:assert/strict";
import test from "node:test";

import {
  mapProductionDayOverview,
  ProductionDayPresentationFailure,
} from "../src/application/production-day-presentation.ts";

const day = "2026-08-31";

function source(index, overrides = {}) {
  return {
    machineId: `00000000-0000-0000-0000-${String(index).padStart(12, "0")}`,
    processorId: `processor-${index}`,
    displayName: `Machine ${index}`,
    groupName: index % 2 === 0 ? "Line B" : "Line A",
    displayOrder: index * 10,
    ...overrides,
  };
}

function item(configuredSource, metricKey = "availability", overrides = {}) {
  const revision = {
    processorId: configuredSource.processorId,
    machineId: configuredSource.machineId,
    streamKey: `stream-${configuredSource.processorId}`,
    position: "18446744073709551615",
  };

  return {
    scope: "production-day",
    processorId: configuredSource.processorId,
    machineId: configuredSource.machineId,
    shift: null,
    productionDay: { siteId: "site-1", businessDate: day },
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    metricKey,
    definitionVersion: "1.0",
    status: "calculated",
    value: "0.80",
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: revision,
    ...overrides,
  };
}

function map(sources, items, productionDay = day) {
  return mapProductionDayOverview({
    productionDay,
    sources,
    result: { items },
  });
}

function machine(model, groupIndex = 0, machineIndex = 0) {
  return model.groups[groupIndex].machines[machineIndex];
}

function expectPresentationFailure(reason, action) {
  assert.throws(action, (error) => {
    assert.ok(error instanceof ProductionDayPresentationFailure);
    assert.equal(error.reason, reason);
    return true;
  });
}

test("zero configured sources produces an empty overview", () => {
  assert.deepEqual(map([], []), { productionDay: day, groups: [] });
});

test("one, seven, and arbitrary machine populations remain fully represented without reporting results", () => {
  for (const count of [1, 7, 50]) {
    const sources = Array.from({ length: count }, (_, index) =>
      source(index + 1, { groupName: null }),
    );
    const model = map(sources, []);

    assert.equal(model.groups.length, 1);
    assert.equal(model.groups[0].machines.length, count);
    assert.deepEqual(
      model.groups[0].machines.map(({ machineId }) => machineId),
      sources.map(({ machineId }) => machineId),
    );
    for (const overview of model.groups[0].machines) {
      assert.deepEqual(
        Object.values(overview.metrics).map(({ kind }) => kind),
        ["missing", "missing", "missing", "missing", "missing"],
      );
    }
  }
});

test("group order follows first configured occurrence and machine order remains relative configured order", () => {
  const a1 = source(1, { displayName: "A1", groupName: "A" });
  const b1 = source(2, { displayName: "B1", groupName: "B" });
  const a2 = source(3, { displayName: "A2", groupName: "A" });
  const ungrouped = source(4, { displayName: "Ungrouped", groupName: null });

  const model = map([a1, b1, a2, ungrouped], []);

  assert.deepEqual(model.groups.map(({ groupName }) => groupName), ["A", "B", null]);
  assert.deepEqual(model.groups[0].machines.map(({ displayName }) => displayName), ["A1", "A2"]);
  assert.deepEqual(model.groups[1].machines.map(({ displayName }) => displayName), ["B1"]);
  assert.deepEqual(model.groups[2].machines.map(({ displayName }) => displayName), ["Ungrouped"]);
});

test("reporting arrival order does not affect configured output order", () => {
  const first = source(1, { groupName: null });
  const second = source(2, { groupName: null });
  const results = [item(second), item(first)];

  const model = map([first, second], results);

  assert.deepEqual(model.groups[0].machines.map(({ machineId }) => machineId), [
    first.machineId,
    second.machineId,
  ]);
  assert.equal(model.groups[0].machines[0].metrics.availability.kind, "calculated");
  assert.equal(model.groups[0].machines[1].metrics.availability.kind, "calculated");
});

test("correlation requires MachineId plus ProcessorId rather than MachineId alone", () => {
  const configured = source(1);
  const wrongProcessor = item(configured, "availability", { processorId: "other-processor" });

  expectPresentationFailure("unexpected-source", () => map([configured], [wrongProcessor]));
});

test("calculated numeric strings, zero, unit, and source revision are preserved verbatim", () => {
  const configured = source(1);
  const availabilityRevision = item(configured).sourceRevision;
  const availability = item(configured, "availability", {
    value: "0.8000000000000000000000000001",
    unit: "ratio-exact",
    sourceRevision: availabilityRevision,
  });
  const utilization = item(configured, "utilization.elr", { value: 0 });

  const model = map([configured], [availability, utilization]);
  const overview = machine(model);

  assert.equal(overview.metrics.availability.kind, "calculated");
  assert.equal(overview.metrics.availability.value, availability.value);
  assert.equal(overview.metrics.availability.unit, availability.unit);
  assert.equal(overview.metrics.availability.sourceRevision, availabilityRevision);
  assert.equal(overview.metrics.utilization.kind, "calculated");
  assert.equal(overview.metrics.utilization.value, 0);
});

test("authoritative OEE is never reconstructed from Availability, Performance, and Quality", () => {
  const configured = source(1);
  const results = [
    item(configured, "availability", { value: 0.8 }),
    item(configured, "performance", { value: 0.5 }),
    item(configured, "quality", { value: 0.9 }),
    item(configured, "oee", { value: 0.37 }),
  ];

  const overview = machine(map([configured], results));

  assert.equal(overview.metrics.oee.kind, "calculated");
  assert.equal(overview.metrics.oee.value, 0.37);
  assert.equal(overview.metrics.utilization.kind, "missing");
});

test("unavailable and insufficient-evidence preserve reason evidence and source revision", () => {
  const configured = source(1);
  const unavailableRevision = item(configured).sourceRevision;
  const insufficientRevision = { ...unavailableRevision, position: "42" };
  const results = [
    item(configured, "availability", {
      status: "unavailable",
      value: null,
      reasonCode: "planned-time-missing",
      reasonOperandName: "PlannedOperatingTime",
      sourceRevision: unavailableRevision,
    }),
    item(configured, "quality", {
      status: "insufficient-evidence",
      value: null,
      reasonCode: "part-count-missing",
      reasonOperandName: "GoodParts",
      sourceRevision: insufficientRevision,
    }),
  ];

  const overview = machine(map([configured], results));

  assert.deepEqual(overview.metrics.availability, {
    kind: "unavailable",
    metricKey: "availability",
    version: "1.0",
    reasonCode: "planned-time-missing",
    reasonOperandName: "PlannedOperatingTime",
    sourceRevision: unavailableRevision,
  });
  assert.deepEqual(overview.metrics.quality, {
    kind: "insufficient-evidence",
    metricKey: "quality",
    version: "1.0",
    reasonCode: "part-count-missing",
    reasonOperandName: "GoodParts",
    sourceRevision: insufficientRevision,
  });
});

test("missing is presentation-only and contains no fabricated value, unit, reason, or revision", () => {
  const configured = source(1);
  const missing = machine(map([configured], [])).metrics.oee;

  assert.deepEqual(missing, { kind: "missing", metricKey: "oee", version: "1.0" });
  assert.equal("value" in missing, false);
  assert.equal("unit" in missing, false);
  assert.equal("reasonCode" in missing, false);
  assert.equal("sourceRevision" in missing, false);
});

test("duplicate authoritative identity fails rather than selecting a result", () => {
  const configured = source(1);
  const first = item(configured, "availability", { value: 0.8 });
  const second = item(configured, "availability", { value: 0.9 });

  expectPresentationFailure("duplicate-result", () => map([configured], [first, second]));
});

test("every authoritative item is validated before missing slots are manufactured", () => {
  const configured = source(1);
  const unexpected = item(source(2), "availability");

  expectPresentationFailure("unexpected-source", () => map([configured], [unexpected]));
});

test("unexpected scope, period, context, metric identity, and version fail explicitly", () => {
  const configured = source(1);

  expectPresentationFailure("unexpected-scope", () =>
    map([configured], [item(configured, "availability", { scope: "shift" })]),
  );
  expectPresentationFailure("unexpected-period", () =>
    map([configured], [
      item(configured, "availability", {
        productionDay: { siteId: "site-1", businessDate: "2026-08-30" },
      }),
    ]),
  );
  expectPresentationFailure("unexpected-context", () =>
    map([configured], [
      item(configured, "availability", {
        context: {
          productionOrderId: "PO-1",
          operationId: null,
          partId: null,
          operatorId: null,
        },
      }),
    ]),
  );
  expectPresentationFailure("unexpected-metric", () =>
    map([configured], [item(configured, "SomethingElse")]),
  );
  expectPresentationFailure("unexpected-metric", () =>
    map([configured], [item(configured, "availability", { definitionVersion: "2.0" })]),
  );
});

test("unknown status and inconsistent status/value combinations fail rather than being repaired", () => {
  const configured = source(1);

  expectPresentationFailure("invalid-result-shape", () =>
    map([configured], [item(configured, "availability", { status: "future-status" })]),
  );
  expectPresentationFailure("invalid-result-shape", () =>
    map([configured], [item(configured, "availability", { status: "calculated", value: null })]),
  );
  expectPresentationFailure("invalid-result-shape", () =>
    map([configured], [item(configured, "availability", { status: "unavailable", value: 0 })]),
  );
  expectPresentationFailure("invalid-result-shape", () =>
    map([configured], [
      item(configured, "availability", { status: "insufficient-evidence", value: "0.1" }),
    ]),
  );
});

test("configured source uniqueness and selected production day are validated before result reshaping", () => {
  const configured = source(1);

  expectPresentationFailure("unexpected-source", () => map([configured, { ...configured }], []));
  assert.throws(
    () => map([configured], [], "2026-02-29"),
    /valid queryable YYYY-MM-DD calendar date/,
  );
});
