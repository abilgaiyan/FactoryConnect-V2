import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ReportingNetworkFailure, ReportingProtocolFailure } from "../src/api/reporting/index.ts";
import { deriveProductionDayOverviewViewState } from "../src/application/production-day-overview-state.ts";
import { ProductionDayOverviewSurface } from "../src/application/ProductionDayOverviewSurface.ts";
import {
  buildProductionDayQueryRequest,
  queryAuthoritativeProductionDay,
} from "../src/application/production-day-reporting.ts";

const day = "2026-08-31";
const processorId = "operational-metrics";
const expectedMetrics = [
  { metricKey: "availability", version: "1.0" },
  { metricKey: "utilization.elr", version: "1.0" },
  { metricKey: "performance", version: "1.0" },
  { metricKey: "quality", version: "1.0" },
  { metricKey: "oee", version: "1.0" },
];
const expectedContext = {
  productionOrderId: null,
  operationId: null,
  partId: null,
  operatorId: null,
  unpartitionedOnly: true,
};

function machineId(index) {
  return `00000000-0000-0000-0000-${String(index + 1).padStart(12, "0")}`;
}

function sources(count) {
  return Array.from({ length: count }, (_, index) => ({
    machineId: machineId(index),
    processorId,
    displayName: `Machine ${index + 1}`,
    groupName: index % 2 === 0 ? "Line A" : "Line B",
    displayOrder: index,
  }));
}

function item(source, metricKey, value = "0.8", overrides = {}) {
  return {
    scope: "production-day",
    processorId: source.processorId,
    machineId: source.machineId,
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    metricKey,
    definitionVersion: "1.0",
    status: "calculated",
    value,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: source.processorId,
      machineId: source.machineId,
      streamKey: `stream:${source.machineId}`,
      position: "18446744073709551615",
    },
    shift: null,
    productionDay: { siteId: "factory", businessDate: day },
    ...overrides,
  };
}

function allItems(configuredSources) {
  return configuredSources.flatMap((source) => expectedMetrics.map(({ metricKey }) => item(source, metricKey)));
}

function binding(state) {
  return { state, lastSuccessfulRetrieval: null, async refresh() {} };
}

function renderState(state) {
  return renderToStaticMarkup(React.createElement(ProductionDayOverviewSurface, {
    productionDay: day,
    overview: binding(state),
    onProductionDayChange() {},
  }));
}

function machineCount(model) {
  return model.groups.flatMap((group) => group.machines).length;
}

async function traverse(configuredSources, pages) {
  const requests = [];
  let index = 0;
  const result = await queryAuthoritativeProductionDay(day, configuredSources, {
    async queryProductionDayMetrics(request) {
      requests.push(request);
      return pages[index++];
    },
  });
  return { result, requests };
}

function assertExactRequest(request, configuredSources, continuationToken = null) {
  assert.deepEqual(request.sources, configuredSources.map(({ machineId, processorId }) => ({ machineId, processorId })));
  assert.deepEqual(request.metrics, expectedMetrics);
  assert.deepEqual(request.context, expectedContext);
  assert.equal(request.fromInclusive, day);
  assert.equal(request.toExclusive, "2026-09-01");
  assert.equal(request.continuationToken, continuationToken);
  assert.doesNotMatch(request.fromInclusive, /T/);
  assert.doesNotMatch(request.toExclusive, /T/);
}

test("configured populations 0 1 7 and 50 traverse the real query mapping and rendering boundaries", async () => {
  for (const count of [0, 1, 7, 50]) {
    const configured = sources(count);
    let calls = 0;
    const authoritative = await queryAuthoritativeProductionDay(day, configured, {
      async queryProductionDayMetrics() {
        calls++;
        return { items: allItems(configured), continuationToken: null };
      },
    });
    const state = deriveProductionDayOverviewViewState(
      authoritative.items.length === 0 ? { kind: "empty", data: authoritative } : { kind: "success", data: authoritative },
      day,
      configured,
    );

    if (count === 0) {
      assert.equal(calls, 0);
      assert.equal(state.kind, "empty-factory");
      assert.match(renderState(state), /No machines are configured/);
    } else {
      assert.equal(calls, 1);
      assert.equal(state.kind, "success");
      assert.equal(machineCount(state.model), count);
      const html = renderState(state);
      for (const source of configured) assert.match(html, new RegExp(`${source.displayName}<`));
    }
  }
});

test("every page preserves exact source metric context and date-only request identity", async () => {
  const configured = sources(2);
  const { requests } = await traverse(configured, [
    { items: [], continuationToken: "token-A" },
    { items: [], continuationToken: "opaque+/=token-B" },
    { items: [], continuationToken: null },
  ]);

  assert.equal(requests.length, 3);
  assertExactRequest(requests[0], configured, null);
  assertExactRequest(requests[1], configured, "token-A");
  assertExactRequest(requests[2], configured, "opaque+/=token-B");

  assert.equal(buildProductionDayQueryRequest("2026-12-31", configured).toExclusive, "2027-01-01");
  assert.equal(buildProductionDayQueryRequest("2028-02-29", configured).toExclusive, "2028-03-01");
});

test("50-machine overview consumes all continuation pages before mapping 250 identities", async () => {
  const configured = sources(50);
  const authoritativeItems = allItems(configured);
  const pages = [
    { items: authoritativeItems.slice(0, 80), continuationToken: "token-A" },
    { items: authoritativeItems.slice(80, 170), continuationToken: "opaque+/=token-B" },
    { items: authoritativeItems.slice(170), continuationToken: null },
  ];
  const { result, requests } = await traverse(configured, pages);

  assert.equal(requests.length, 3);
  assert.equal(result.items.length, 250);
  assert.deepEqual(result.items, authoritativeItems);
  const state = deriveProductionDayOverviewViewState({ kind: "success", data: result }, day, configured);
  assert.equal(state.kind, "success");
  assert.equal(machineCount(state.model), 50);
});

test("mixed authoritative states and absence survive through presentation and UI without zero synthesis", async () => {
  const configured = sources(2);
  const a = configured[0];
  const revision = { processorId, machineId: a.machineId, streamKey: "mixed", position: "42" };
  const mixed = [
    item(a, "availability", "0.8000000000000000001"),
    item(a, "utilization.elr", 0),
    item(a, "performance", null, { status: "unavailable", reasonCode: "no-reference", reasonOperandName: "ReferenceTime", sourceRevision: revision }),
    item(a, "quality", null, { status: "insufficient-evidence", reasonCode: "no-quality", sourceRevision: revision }),
  ];
  const result = await queryAuthoritativeProductionDay(day, configured, {
    async queryProductionDayMetrics() { return { items: mixed, continuationToken: null }; },
  });
  const state = deriveProductionDayOverviewViewState({ kind: "success", data: result }, day, configured);

  assert.equal(state.kind, "success");
  const machines = state.model.groups.flatMap((group) => group.machines);
  const machineA = machines.find((machine) => machine.machineId === a.machineId);
  const machineB = machines.find((machine) => machine.machineId === configured[1].machineId);
  assert.deepEqual(Object.values(machineA.metrics).map((metric) => metric.kind), ["calculated", "calculated", "unavailable", "insufficient-evidence", "missing"]);
  assert.deepEqual(Object.values(machineB.metrics).map((metric) => metric.kind), ["missing", "missing", "missing", "missing", "missing"]);
  assert.equal(machineA.metrics.availability.value, "0.8000000000000000001");
  assert.equal(machineA.metrics.performance.reasonCode, "no-reference");
  assert.equal(machineA.metrics.performance.sourceRevision, revision);
  const html = renderState(state);
  assert.match(html, /80\.00000000000000001%/);
  assert.match(html, /— Unavailable/);
  assert.match(html, /— Insufficient evidence/);
  assert.match(html, /— Missing/);
});

test("authoritative OEE crosses the whole result-to-render path as 37 percent rather than recalculated 36", async () => {
  const configured = sources(1);
  const source = configured[0];
  const authoritative = [
    item(source, "availability", "0.80"),
    item(source, "performance", "0.50"),
    item(source, "quality", "0.90"),
    item(source, "oee", "0.37"),
  ];
  const result = await queryAuthoritativeProductionDay(day, configured, {
    async queryProductionDayMetrics() { return { items: authoritative, continuationToken: null }; },
  });
  const state = deriveProductionDayOverviewViewState({ kind: "success", data: result }, day, configured);
  const html = renderState(state);
  const oee = html.indexOf(">OEE</th>");
  assert.ok(oee >= 0);
  assert.match(html.slice(oee), /37%/);
  assert.doesNotMatch(html.slice(oee), /36%/);
});

test("configured first-occurrence grouping and relative machine order win over reversed reporting arrival", async () => {
  const configured = [
    { ...sources(1)[0], machineId: machineId(0), displayName: "A1", groupName: "Line A", displayOrder: 10 },
    { ...sources(1)[0], machineId: machineId(1), displayName: "B1", groupName: "Line B", displayOrder: 20 },
    { ...sources(1)[0], machineId: machineId(2), displayName: "A2", groupName: "Line A", displayOrder: 30 },
    { ...sources(1)[0], machineId: machineId(3), displayName: "U1", groupName: null, displayOrder: 40 },
  ];
  const reversed = allItems(configured).reverse();
  const result = await queryAuthoritativeProductionDay(day, configured, {
    async queryProductionDayMetrics() { return { items: reversed, continuationToken: null }; },
  });
  const state = deriveProductionDayOverviewViewState({ kind: "success", data: result }, day, configured);
  assert.equal(state.kind, "success");
  assert.deepEqual(state.model.groups.map((group) => group.groupName), ["Line A", "Line B", null]);
  assert.deepEqual(state.model.groups[0].machines.map((machine) => machine.displayName), ["A1", "A2"]);
  const html = renderState(state);
  assert.ok(html.indexOf("A1") < html.indexOf("A2"));
  assert.ok(html.indexOf("A2") < html.indexOf("Line B"));
});

test("identity metric version and context violations become controlled presentation alerts", () => {
  const configured = sources(1);
  const source = configured[0];
  const violations = [
    item(source, "oee", "0.37", { processorId: "wrong-processor" }),
    item(source, "oee", "0.37", { machineId: machineId(9) }),
    item(source, "oee", "0.37", { definitionVersion: "2.0" }),
    item(source, "UnknownMetric", "0.37"),
    item(source, "oee", "0.37", { context: { productionOrderId: "PO-1", operationId: null, partId: null, operatorId: null } }),
  ];

  for (const violation of violations) {
    const state = deriveProductionDayOverviewViewState({ kind: "success", data: { items: [violation] } }, day, configured);
    assert.equal(state.kind, "presentation-failed");
    assert.match(renderState(state), /role="alert"/);
  }
});

test("later-page failures and cursor guards reject the whole retrieval without returning partial results", async () => {
  const configured = sources(1);
  let call = 0;
  await assert.rejects(
    queryAuthoritativeProductionDay(day, configured, {
      async queryProductionDayMetrics() {
        call++;
        if (call === 1) return { items: [item(configured[0], "oee", "0.37")], continuationToken: "next" };
        throw new ReportingNetworkFailure(new Error("offline"));
      },
    }),
    ReportingNetworkFailure,
  );

  await assert.rejects(
    queryAuthoritativeProductionDay(day, configured, {
      async queryProductionDayMetrics() { return { items: [], continuationToken: "cycle" }; },
    }),
    ReportingProtocolFailure,
  );

  let pages = 0;
  await assert.rejects(
    queryAuthoritativeProductionDay(day, configured, {
      async queryProductionDayMetrics() { pages++; return { items: [], continuationToken: `token-${pages}` }; },
    }),
    ReportingProtocolFailure,
  );
  assert.equal(pages, 100);
});

test("zero and absent production-day metrics never infer current machine state", () => {
  const configured = sources(1);
  for (const result of [
    { items: [item(configured[0], "availability", 0), item(configured[0], "oee", 0)] },
    { items: [] },
  ]) {
    const state = deriveProductionDayOverviewViewState(result.items.length === 0 ? { kind: "empty", data: result } : { kind: "success", data: result }, day, configured);
    assert.equal(state.kind, "success");
    const machine = state.model.groups[0].machines[0];
    for (const forbidden of ["currentState", "machineState", "running", "idle", "fault", "alarm", "online", "offline", "lastSeen"]) {
      assert.equal(Object.hasOwn(machine, forbidden), false);
    }
    const html = renderState(state);
    for (const label of ["Running", "Idle", "Fault", "Alarm", "Online", "Offline"]) {
      assert.doesNotMatch(html, new RegExp(`>${label}<`, "i"));
    }
  }
});

test("overview production modules stay inside the reporting presentation dependency boundary", () => {
  const modules = [
    "src/application/production-day-reporting.ts",
    "src/application/production-day-presentation.ts",
    "src/application/production-day-overview-state.ts",
    "src/application/use-production-day-overview.ts",
    "src/application/ProductionDayOverviewSurface.ts",
    "src/application/ProductionDayOverviewMatrix.ts",
  ];
  const allowedPrefixes = ["../api/reporting/", "../query/", "./"];

  for (const modulePath of modules) {
    const source = fs.readFileSync(new URL(`../${modulePath}`, import.meta.url), "utf8");
    const imports = [...source.matchAll(/from\s+["']([^"']+)["']/g)].map((match) => match[1]);
    for (const dependency of imports) {
      assert.ok(
        dependency === "../presentation/date-formatting.ts" || dependency === "react" || allowedPrefixes.some((prefix) => dependency.startsWith(prefix)),
        `${modulePath} imports disallowed dependency ${dependency}`,
      );
    }
  }
});
