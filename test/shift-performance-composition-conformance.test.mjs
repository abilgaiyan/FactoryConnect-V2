import assert from "node:assert/strict";
import { after, test } from "node:test";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";

import { mountInDom } from "./dom-test-harness.mjs";

const clientRootUrl = new URL("../", import.meta.url);
const clientRoot = fileURLToPath(clientRootUrl);
const vite = await createServer({ root: clientRoot, server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { App } = await vite.ssrLoadModule("/src/App.tsx");
after(async () => vite.close());

const source = {
  machineId: "11111111-1111-1111-1111-111111111111",
  processorId: "operational-metrics",
  siteId: "factory-1",
  productionLineId: "line-1",
  displayName: "Machine 1",
  groupName: "Line 1",
  displayOrder: 10,
};
const day = "2026-09-02";
const opaqueToken = "opaque::token/that+must=remain%unchanged";
const exactRevision = {
  processorId: source.processorId,
  machineId: source.machineId,
  streamKey: "operational-metrics:machine-1",
  position: "18446744073709551615",
};
const expectedMetrics = [
  { metricKey: "availability", version: "1.0" },
  { metricKey: "utilization.elr", version: "1.0" },
  { metricKey: "performance", version: "1.0" },
  { metricKey: "quality", version: "1.0" },
  { metricKey: "oee", version: "1.0" },
];

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

async function settle(action) {
  await act(async () => {
    action();
    await Promise.resolve();
  });
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

function shiftReport({ shiftId, startsAtUtc, endsAtUtc, sourceRevision, metrics }) {
  return {
    processorId: source.processorId,
    machineId: source.machineId,
    productionDay: { siteId: source.siteId, businessDate: day },
    productionLineId: source.productionLineId,
    shift: {
      siteId: source.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId,
      startsAtUtc,
      endsAtUtc,
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision,
    metrics,
  };
}

function assertBaseRequest(call) {
  assert.deepEqual(call.request.sources, [{
    machineId: source.machineId,
    processorId: source.processorId,
    siteId: source.siteId,
    businessDate: day,
  }]);
  assert.deepEqual(call.request.metrics, expectedMetrics);
  assert.deepEqual(call.request.context, {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  });
  assert.equal(call.request.pageSize, 200);
  assert.ok(call.options.signal instanceof AbortSignal);
  assert.equal("order" in call.request, false);
  assert.equal("startsAtOrAfterUtc" in call.request, false);
  assert.equal("startsBeforeUtc" in call.request, false);
}

function importsOf(sourceText) {
  return [...sourceText.matchAll(/(?:import|export)\s+(?:type\s+)?(?:[\s\S]*?\s+from\s+)?["']([^"']+)["'];/g)]
    .map(match => match[1]);
}

async function sourceImports(relativePath) {
  const content = await readFile(new URL(relativePath, clientRootUrl), "utf8");
  return importsOf(content);
}

test("production Shift Performance composition keeps the frozen dependency direction", async () => {
  assert.deepEqual(
    await sourceImports("src/application/use-shift-performance-overview.ts"),
    [
      "react",
      "./application-runtime.ts",
      "./shift-performance-page-state.ts",
      "./use-production-day-shift-reporting.ts",
    ],
  );
  assert.deepEqual(
    await sourceImports("src/application/shift-performance-page-state.ts"),
    [
      "../query/query-state.ts",
      "../presentation/shift-performance-projector.ts",
      "../presentation/shift-performance-model.ts",
      "./production-day-shift-reporting.ts",
      "./runtime-configuration.ts",
    ],
  );
  assert.deepEqual(
    await sourceImports("src/presentation/ShiftPerformanceOverviewView.tsx"),
    ["react", "./date-formatting.ts", "./shift-performance-model.ts", "./shift-performance-view-formatting.ts"],
  );
  assert.deepEqual(
    await sourceImports("src/presentation/ShiftPerformancePageStateView.tsx"),
    ["../application/shift-performance-page-state.ts", "./ShiftPerformanceOverviewView.tsx"],
  );
  assert.deepEqual(
    await sourceImports("src/presentation/ShiftPerformancePage.tsx"),
    ["../application/shift-performance-page-state.ts", "./ShiftPerformancePageStateView.tsx"],
  );
});

test("two-page production route publishes no partial overview and atomically renders authoritative occurrences", async (t) => {
  const pageTwo = deferred();
  const calls = { productionDayShift: [], productionDay: [], shift: [] };
  const shiftA = shiftReport({
    shiftId: "Shift A",
    startsAtUtc: `${day}T00:00:00Z`,
    endsAtUtc: `${day}T08:00:00Z`,
    sourceRevision: null,
    metrics: [],
  });
  const shiftB = shiftReport({
    shiftId: "Shift B",
    startsAtUtc: `${day}T08:00:00Z`,
    endsAtUtc: `${day}T16:00:00Z`,
    sourceRevision: exactRevision,
    metrics: [
      metric("availability", "0.80"),
      metric("utilization.elr", "0.70"),
      metric("performance", "0.50"),
      metric("quality", "0.90"),
      metric("oee", "0.37"),
    ],
  });
  const runtime = {
    configuration: { reportingBasePath: "/", requestTimeoutMilliseconds: 30_000, sources: [source] },
    reportingClient: {
      async queryProductionDayShiftMetrics(request, options) {
        calls.productionDayShift.push({ request, options });
        if (calls.productionDayShift.length === 1) {
          return { items: [shiftA], continuationToken: opaqueToken };
        }
        if (calls.productionDayShift.length === 2) {
          return pageTwo.promise;
        }
        throw new Error("Unexpected automatic reporting retry.");
      },
      async queryProductionDayMetrics(request) {
        calls.productionDay.push(request);
        return { items: [], continuationToken: null };
      },
      async queryShiftMetrics(request) {
        calls.shift.push(request);
        return { items: [], continuationToken: null };
      },
    },
    now: () => new Date("2026-09-03T00:00:00.000Z"),
  };

  const harness = await mountInDom(
    React.createElement(App, { runtime }),
    `http://factory-dashboard/production-days/${day}/shifts`,
  );
  t.after(() => harness.dispose());

  assert.equal(harness.window.location.pathname, `/production-days/${day}/shifts`);
  assert.equal(calls.productionDayShift.length, 2);
  assertBaseRequest(calls.productionDayShift[0]);
  assert.equal(calls.productionDayShift[0].request.continuationToken, null);
  assertBaseRequest(calls.productionDayShift[1]);
  assert.equal(calls.productionDayShift[1].request.continuationToken, opaqueToken);
  assert.equal(calls.productionDayShift[0].options.signal, calls.productionDayShift[1].options.signal);
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);

  const pendingText = harness.document.body.textContent ?? "";
  assert.match(pendingText, /Loading shift performance/i);
  assert.doesNotMatch(pendingText, /Shift A/);
  assert.doesNotMatch(pendingText, /Machine 1/);
  assert.equal([...harness.document.querySelectorAll("table")].length, 0);

  await settle(() => pageTwo.resolve({ items: [shiftB], continuationToken: null }));

  assert.equal(calls.productionDayShift.length, 2);
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
  const finalText = harness.document.body.textContent ?? "";
  assert.match(finalText, /Machine 1/);
  assert.ok(finalText.indexOf("Shift A") < finalText.indexOf("Shift B"));
  assert.match(finalText, /Shift A/);
  assert.match(finalText, /Shift B/);
  assert.match(finalText, /80%/);
  assert.match(finalText, /70%/);
  assert.match(finalText, /50%/);
  assert.match(finalText, /90%/);
  assert.match(finalText, /37%/);
  assert.doesNotMatch(finalText, /36%/);

  const shiftARow = [...harness.document.querySelectorAll("tbody tr")]
    .find(row => row.querySelector("th")?.textContent === "Shift A");
  assert.ok(shiftARow);
  assert.equal(shiftARow.querySelectorAll('td[aria-label$=" missing"]').length, 5);
  assert.equal(calls.productionDayShift.length, 2);

  assert.doesNotMatch(finalText, /current machine|current state|running now|idle now|active now/i);
});
