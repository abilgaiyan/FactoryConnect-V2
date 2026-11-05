import assert from "node:assert/strict";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ProductionDayOverviewMatrix } from "../src/application/ProductionDayOverviewMatrix.ts";
import { deriveProductionDayOverviewViewState } from "../src/application/production-day-overview-state.ts";

const productionDay = "2026-08-31";

function source(machineId, displayName, groupName = "Line 1", displayOrder = 0) {
  return {
    machineId,
    processorId: "operational-metrics",
    displayName,
    groupName,
    displayOrder,
  };
}

function calculatedItem(machineId, metricKey, value, unit = "ratio") {
  return {
    scope: "production-day",
    processorId: "operational-metrics",
    machineId,
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    metricKey,
    definitionVersion: "1.0",
    status: "calculated",
    value,
    unit,
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: "operational-metrics",
      machineId,
      streamKey: `stream:${machineId}`,
      position: "18446744073709551615",
    },
    shift: null,
    productionDay: {
      siteId: "factory-1",
      businessDate: productionDay,
    },
  };
}

function nonCalculatedItem(machineId, metricKey, status, reasonCode, reasonOperandName = null) {
  return {
    ...calculatedItem(machineId, metricKey, null),
    status,
    value: null,
    reasonCode,
    reasonOperandName,
  };
}

function derive(queryState, sources) {
  return deriveProductionDayOverviewViewState(queryState, productionDay, sources);
}

function render(model) {
  return renderToStaticMarkup(React.createElement(ProductionDayOverviewMatrix, { model }));
}

test("lifecycle empty maps configured machines to five missing metric slots", () => {
  const sources = [source("11111111-1111-1111-1111-111111111111", "Machine 1")];
  const state = derive({ kind: "empty", data: { items: [] } }, sources);

  assert.equal(state.kind, "success");
  const machine = state.model.groups[0].machines[0];
  assert.deepEqual(
    Object.values(machine.metrics).map((metric) => metric.kind),
    ["missing", "missing", "missing", "missing", "missing"],
  );
});

test("zero configured machines has a distinct empty-factory state", () => {
  assert.deepEqual(derive({ kind: "empty", data: { items: [] } }, []), { kind: "empty-factory" });
});

test("presentation failures are contained instead of escaping rendering", () => {
  const machineId = "11111111-1111-1111-1111-111111111111";
  const duplicate = calculatedItem(machineId, "oee", "0.37");
  const state = derive(
    { kind: "success", data: { items: [duplicate, duplicate] } },
    [source(machineId, "Machine 1")],
  );

  assert.equal(state.kind, "presentation-failed");
  assert.match(state.message, /violated the production-day overview contract/i);
});

test("unexpected programmer failures are not reclassified as presentation failures", () => {
  assert.throws(
    () => deriveProductionDayOverviewViewState(
      { kind: "success", data: { items: [] } },
      "not-a-production-day",
      [source("11111111-1111-1111-1111-111111111111", "Machine 1")],
    ),
    RangeError,
  );
});

test("group and machine order from the presentation model is rendered without resorting", () => {
  const sources = [
    source("11111111-1111-1111-1111-111111111111", "A1", "Line A", 10),
    source("22222222-2222-2222-2222-222222222222", "B1", "Line B", 20),
    source("33333333-3333-3333-3333-333333333333", "A2", "Line A", 30),
  ];
  const state = derive({ kind: "empty", data: { items: [] } }, sources);
  assert.equal(state.kind, "success");

  const html = render(state.model);
  assert.ok(html.indexOf("Line A") < html.indexOf("Line B"));
  assert.ok(html.indexOf("A1") < html.indexOf("A2"));
});

test("matrix renders all five metric columns with accessible row and column headers", () => {
  const state = derive(
    { kind: "empty", data: { items: [] } },
    [source("11111111-1111-1111-1111-111111111111", "Machine 1")],
  );
  assert.equal(state.kind, "success");

  const html = render(state.model);
  for (const heading of ["Availability", "Utilization", "Performance", "Quality", "OEE"]) {
    assert.match(html, new RegExp(`<th[^>]*scope="col"[^>]*>${heading}</th>`));
  }
  assert.match(html, /<th[^>]*scope="row"[^>]*>Machine 1<\/th>/);
  assert.match(html, /<caption>/);
});

test("calculated numeric strings and zero render as authoritative values", () => {
  const machineId = "11111111-1111-1111-1111-111111111111";
  const state = derive({
    kind: "success",
    data: {
      items: [
        calculatedItem(machineId, "availability", "0.8000000000000000001"),
        calculatedItem(machineId, "utilization.elr", 0),
      ],
    },
  }, [source(machineId, "Machine 1")]);
  assert.equal(state.kind, "success");

  const html = render(state.model);
  assert.match(html, /80\.00000000000000001%/);
  assert.match(html, />0%</);
});

test("missing unavailable and insufficient evidence render distinctly with visible reasons", () => {
  const machineId = "11111111-1111-1111-1111-111111111111";
  const state = derive({
    kind: "success",
    data: {
      items: [
        nonCalculatedItem(machineId, "availability", "unavailable", "no-planned-time", "PlannedOperatingTime"),
        nonCalculatedItem(machineId, "performance", "insufficient-evidence", "missing-reference-time"),
      ],
    },
  }, [source(machineId, "Machine 1")]);
  assert.equal(state.kind, "success");

  const html = render(state.model);
  assert.match(html, /— Unavailable/);
  assert.match(html, /no-planned-time: PlannedOperatingTime/);
  assert.match(html, /— Insufficient evidence/);
  assert.match(html, /missing-reference-time/);
  assert.match(html, /— Missing/);
  assert.doesNotMatch(html, /— Missing[^<]*0%/);
});

test("authoritative OEE is rendered and never recomputed from component metrics", () => {
  const machineId = "11111111-1111-1111-1111-111111111111";
  const state = derive({
    kind: "success",
    data: {
      items: [
        calculatedItem(machineId, "availability", "0.80"),
        calculatedItem(machineId, "performance", "0.50"),
        calculatedItem(machineId, "quality", "0.90"),
        calculatedItem(machineId, "oee", "0.37"),
      ],
    },
  }, [source(machineId, "Machine 1")]);
  assert.equal(state.kind, "success");

  const html = render(state.model);
  const oeeHeading = html.indexOf(">OEE</th>");
  assert.ok(oeeHeading >= 0);
  assert.match(html.slice(oeeHeading), /37%/);
  assert.doesNotMatch(html.slice(oeeHeading), /36%/);
});

test("arbitrary configured populations render without a seven-machine assumption", () => {
  const sources = Array.from({ length: 50 }, (_, index) => source(
    `00000000-0000-0000-0000-${String(index + 1).padStart(12, "0")}`,
    `Machine ${index + 1}`,
    index % 2 === 0 ? "Line A" : "Line B",
    index,
  ));
  const state = derive({ kind: "empty", data: { items: [] } }, sources);
  assert.equal(state.kind, "success");

  const html = render(state.model);
  assert.match(html, /Machine 1</);
  assert.match(html, /Machine 50</);
  assert.equal(state.model.groups.flatMap((group) => group.machines).length, 50);
});
