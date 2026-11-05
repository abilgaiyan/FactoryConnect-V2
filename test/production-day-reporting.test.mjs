import assert from "node:assert/strict";
import test from "node:test";

import { ReportingProtocolFailure } from "../src/api/reporting/index.ts";
import {
  buildProductionDayQueryRequest,
  isProductionDaySelection,
  ProductionDayReportingTraversalFailure,
  queryAuthoritativeProductionDay,
} from "../src/application/production-day-reporting.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

const sources = [
  {
    machineId: "11111111-1111-1111-1111-111111111111",
    processorId: "operational-metrics-a",
    displayName: "Machine A",
    groupName: "Line 1",
    displayOrder: 10,
  },
  {
    machineId: "22222222-2222-2222-2222-222222222222",
    processorId: "operational-metrics-b",
    displayName: "Machine B",
    groupName: "Line 1",
    displayOrder: 20,
  },
];

const expectedMetrics = [
  { metricKey: "availability", version: "1.0" },
  { metricKey: "utilization.elr", version: "1.0" },
  { metricKey: "performance", version: "1.0" },
  { metricKey: "quality", version: "1.0" },
  { metricKey: "oee", version: "1.0" },
];

test("production-day request selects one day, exact configured identities, exact metric versions, and unpartitioned context", () => {
  assert.deepEqual(buildProductionDayQueryRequest("2026-08-31", sources), {
    sources: [
      {
        machineId: "11111111-1111-1111-1111-111111111111",
        processorId: "operational-metrics-a",
      },
      {
        machineId: "22222222-2222-2222-2222-222222222222",
        processorId: "operational-metrics-b",
      },
    ],
    fromInclusive: "2026-08-31",
    toExclusive: "2026-09-01",
    metrics: expectedMetrics,
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
      unpartitionedOnly: true,
    },
    statuses: null,
    order: "period-ascending",
    pageSize: 200,
    continuationToken: null,
  });
});

test("production-day request preserves an opaque continuation token exactly", () => {
  const opaqueToken = " opaque+/=::not-json ";

  assert.equal(
    buildProductionDayQueryRequest("2026-08-31", sources, opaqueToken).continuationToken,
    opaqueToken,
  );
});

test("authoritative query consumes every continuation page without interpreting or deduplicating items", async () => {
  const opaqueToken = "opaque+/=::not-json";
  const calls = [];
  const repeatedItem = { marker: "same-authoritative-item" };
  const pages = [
    { items: [repeatedItem], continuationToken: opaqueToken },
    { items: [repeatedItem, { marker: "second-page" }], continuationToken: null },
  ];
  const reportingClient = {
    async queryProductionDayMetrics(request, options) {
      calls.push({ request, options });
      return pages[calls.length - 1];
    },
  };
  const signal = new AbortController().signal;

  const result = await queryAuthoritativeProductionDay(
    "2026-08-31",
    sources,
    reportingClient,
    { signal },
  );

  assert.equal(calls.length, 2);
  assert.equal(calls[0].request.continuationToken, null);
  assert.equal(calls[1].request.continuationToken, opaqueToken);
  assert.equal(calls[0].options.signal, signal);
  assert.equal(calls[1].options.signal, signal);
  assert.deepEqual(result.items, [repeatedItem, repeatedItem, { marker: "second-page" }]);
});

test("repeated continuation token terminates as protocol failure with typed traversal cause", async () => {
  let callCount = 0;
  const reportingClient = {
    async queryProductionDayMetrics() {
      callCount += 1;
      return { items: [], continuationToken: "same-opaque-token" };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDay("2026-08-31", sources, reportingClient),
    (error) =>
      error instanceof ReportingProtocolFailure &&
      error.cause instanceof ProductionDayReportingTraversalFailure &&
      error.cause.reason === "continuation-cycle",
  );
  assert.equal(callCount, 2);
});

test("multi-token continuation cycle terminates without decoding tokens", async () => {
  const returnedTokens = ["opaque-A", "opaque-B", "opaque-A"];
  const receivedTokens = [];
  const reportingClient = {
    async queryProductionDayMetrics(request) {
      receivedTokens.push(request.continuationToken);
      return {
        items: [],
        continuationToken: returnedTokens[receivedTokens.length - 1],
      };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDay("2026-08-31", sources, reportingClient),
    (error) =>
      error instanceof ReportingProtocolFailure &&
      error.cause instanceof ProductionDayReportingTraversalFailure &&
      error.cause.reason === "continuation-cycle",
  );
  assert.deepEqual(receivedTokens, [null, "opaque-A", "opaque-B"]);
});

test("maximum page count bounds an otherwise unique continuation sequence", async () => {
  let callCount = 0;
  const reportingClient = {
    async queryProductionDayMetrics(request) {
      assert.equal(request.continuationToken, callCount === 0 ? null : `opaque-${callCount}`);
      callCount += 1;
      return { items: [], continuationToken: `opaque-${callCount}` };
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDay("2026-08-31", sources, reportingClient),
    (error) =>
      error instanceof ReportingProtocolFailure &&
      error.cause instanceof ProductionDayReportingTraversalFailure &&
      error.cause.reason === "page-limit-exceeded",
  );
  assert.equal(callCount, 100);
});

test("continuation cycle publishes failed through the query lifecycle", async () => {
  const reportingClient = {
    async queryProductionDayMetrics() {
      return { items: [], continuationToken: "same-opaque-token" };
    },
  };
  const controller = createQueryLifecycleController({
    query: (signal) => queryAuthoritativeProductionDay(
      "2026-08-31",
      sources,
      reportingClient,
      { signal },
    ),
    isEmpty: (result) => result.items.length === 0,
  });

  const state = await controller.execute();

  assert.equal(state.kind, "failed");
  assert.ok(state.failure instanceof ReportingProtocolFailure);
  assert.ok(state.failure.cause instanceof ProductionDayReportingTraversalFailure);
  assert.equal(state.failure.cause.reason, "continuation-cycle");
});

test("page-limit violation publishes failed through the query lifecycle", async () => {
  let callCount = 0;
  const reportingClient = {
    async queryProductionDayMetrics() {
      callCount += 1;
      return { items: [], continuationToken: `opaque-${callCount}` };
    },
  };
  const controller = createQueryLifecycleController({
    query: (signal) => queryAuthoritativeProductionDay(
      "2026-08-31",
      sources,
      reportingClient,
      { signal },
    ),
    isEmpty: (result) => result.items.length === 0,
  });

  const state = await controller.execute();

  assert.equal(state.kind, "failed");
  assert.ok(state.failure instanceof ReportingProtocolFailure);
  assert.ok(state.failure.cause instanceof ProductionDayReportingTraversalFailure);
  assert.equal(state.failure.cause.reason, "page-limit-exceeded");
  assert.equal(callCount, 100);
});

test("empty configured factory validates production day before returning an empty authoritative result", async () => {
  let callCount = 0;
  const reportingClient = {
    async queryProductionDayMetrics() {
      callCount += 1;
      throw new Error("must not be called");
    },
  };

  const result = await queryAuthoritativeProductionDay(
    "2026-08-31",
    [],
    reportingClient,
  );

  assert.deepEqual(result, { items: [] });
  assert.equal(callCount, 0);

  await assert.rejects(
    queryAuthoritativeProductionDay("2026-02-29", [], reportingClient),
    RangeError,
  );
  assert.equal(callCount, 0);
});

test("failure on a later continuation page rejects the whole authoritative query", async () => {
  const expectedFailure = new Error("second page failed");
  let callCount = 0;
  const reportingClient = {
    async queryProductionDayMetrics() {
      callCount += 1;
      if (callCount === 1) {
        return { items: [{ marker: "first-page" }], continuationToken: "next" };
      }

      throw expectedFailure;
    },
  };

  await assert.rejects(
    queryAuthoritativeProductionDay("2026-08-31", sources, reportingClient),
    (error) => error === expectedFailure,
  );
  assert.equal(callCount, 2);
});

test("production-day request advances calendar boundaries without local-time interpretation", () => {
  assert.equal(buildProductionDayQueryRequest("2026-12-31", sources).toExclusive, "2027-01-01");
  assert.equal(buildProductionDayQueryRequest("2028-02-29", sources).toExclusive, "2028-03-01");
  assert.equal(buildProductionDayQueryRequest("9999-12-30", sources).toExclusive, "9999-12-31");
});

test("production-day selection accepts only valid queryable DateOnly calendar dates", () => {
  assert.equal(isProductionDaySelection("0000-01-01"), false);
  assert.equal(isProductionDaySelection("0001-01-01"), true);
  assert.equal(isProductionDaySelection("2026-08-31"), true);
  assert.equal(isProductionDaySelection("2028-02-29"), true);
  assert.equal(isProductionDaySelection("9999-12-30"), true);
  assert.equal(isProductionDaySelection("9999-12-31"), false);
  assert.equal(isProductionDaySelection("2026-02-29"), false);
  assert.equal(isProductionDaySelection("2026-8-31"), false);
  assert.equal(isProductionDaySelection("not-a-date"), false);
});

test("invalid production-day boundaries cannot construct a reporting request", () => {
  for (const productionDay of ["0000-01-01", "9999-12-31", "2026-02-29"]) {
    assert.throws(
      () => buildProductionDayQueryRequest(productionDay, sources),
      /valid queryable YYYY-MM-DD calendar date/,
    );
  }
});
