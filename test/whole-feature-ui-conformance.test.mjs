import assert from "node:assert/strict";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ProductionDayMetricResults } from "../src/application/ProductionDayMetricResults.ts";
import { presentQueryState } from "../src/query/query-state-presentation.ts";

const configuredMachineId = "11111111-1111-1111-1111-111111111111";
const unknownMachineId = "22222222-2222-2222-2222-222222222222";

function metric(overrides) {
  return {
    scope: "production-day",
    processorId: "operational-metrics",
    machineId: configuredMachineId,
    shift: null,
    productionDay: { siteId: "site-1", businessDate: "2026-08-31" },
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    metricKey: "availability",
    definitionVersion: "1.0",
    status: "calculated",
    value: 0,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: "operational-metrics",
      machineId: configuredMachineId,
      streamKey: "stream-a",
      position: 10,
    },
    ...overrides,
  };
}

test("whole-feature UI preserves absence, invalid-request, and failure distinctions", () => {
  assert.deepEqual(presentQueryState({ kind: "empty" }), {
    kind: "empty",
    message: "No matching data.",
  });
  assert.deepEqual(presentQueryState({
    kind: "invalidRequest",
    details: { detail: "The reporting query is invalid." },
  }), {
    kind: "invalidRequest",
    message: "The reporting query is invalid.",
  });
  assert.deepEqual(presentQueryState({
    kind: "failed",
    failure: { message: "Reporting transport failed." },
  }), {
    kind: "failed",
    message: "Reporting transport failed.",
  });
});

test("whole-feature UI preserves zero and authoritative metric-status evidence", () => {
  const page = {
    items: [
      metric({}),
      metric({
        metricKey: "performance",
        status: "unavailable",
        value: null,
        reasonCode: "missing-reference-time",
        reasonOperandName: "ProductionReferenceTime",
      }),
      metric({
        machineId: unknownMachineId,
        metricKey: "quality",
        status: "insufficient-evidence",
        value: null,
        reasonCode: "missing-good-count",
        sourceRevision: {
          processorId: "operational-metrics",
          machineId: unknownMachineId,
          streamKey: "stream-b",
          position: 11,
        },
      }),
    ],
    continuationToken: null,
  };

  const html = renderToStaticMarkup(React.createElement(ProductionDayMetricResults, {
    page,
    sources: [{
      machineId: configuredMachineId,
      processorId: "operational-metrics",
      displayName: "Machine 1",
    }],
  }));

  assert.match(html, />0<\/td>/);
  assert.match(html, /calculated/);
  assert.match(html, /unavailable/);
  assert.match(html, /insufficient-evidence/);
  assert.match(html, /missing-reference-time \(ProductionReferenceTime\)/);
  assert.match(html, /missing-good-count/);
  assert.match(html, /Unconfigured source/);
  assert.match(html, new RegExp(unknownMachineId));
  assert.doesNotMatch(html, /Additional reporting results are available\./);
});
