import assert from "node:assert/strict";
import test from "node:test";

import {
  ReportingCancellationFailure,
  ReportingProtocolFailure,
  createReportingClient,
} from "../src/api/reporting/index.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

function request(overrides = {}) {
  return {
    sources: [{
      machineId,
      processorId: "operational-metrics",
      siteId: "site-1",
      businessDate: "2026-09-02",
    }],
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
      unpartitionedOnly: true,
    },
    metrics: [{ metricKey: "oee", version: "1.0" }],
    statuses: ["calculated", "unavailable", "insufficient-evidence"],
    pageSize: 50,
    continuationToken: "request+/=opaque",
    ...overrides,
  };
}

function metric(overrides = {}) {
  return {
    metricKey: "oee",
    definitionVersion: "1.0",
    status: "calculated",
    value: "0.37000000000000000001",
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    ...overrides,
  };
}

function item(overrides = {}) {
  return {
    processorId: "operational-metrics",
    machineId,
    productionDay: { siteId: "site-1", businessDate: "2026-09-02" },
    productionLineId: "line-1",
    shift: {
      siteId: "site-1",
      shiftScheduleAssignmentId: "assignment-a",
      shiftId: "shift-a",
      startsAtUtc: "2026-09-01T20:00:00Z",
      endsAtUtc: "2026-09-02T04:00:00Z",
    },
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    sourceRevision: {
      processorId: "aggregation",
      machineId,
      streamKey: "metric-inputs",
      position: "18446744073709551615",
    },
    metrics: [metric()],
    ...overrides,
  };
}

function jsonResponse(body, status = 200, contentType = "application/json") {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": contentType },
  });
}

function createClient(fetch) {
  return createReportingClient({
    baseAddress: "http://factory-server:5080/factoryconnect",
    timeoutMilliseconds: 1000,
    fetch,
  });
}

test("requests and losslessly decodes one authoritative production-day shift page", async () => {
  const expected = {
    items: [
      item(),
      item({
        sourceRevision: null,
        metrics: [
          metric({
            metricKey: "performance",
            status: "unavailable",
            value: null,
            reasonCode: "missing-operand",
            reasonOperandName: "IdealCycleTime",
          }),
          metric({
            metricKey: "quality",
            status: "insufficient-evidence",
            value: null,
            reasonCode: "insufficient-input",
            reasonOperandName: "GoodCount",
          }),
        ],
      }),
    ],
    continuationToken: "response+/=opaque.do-not-interpret",
  };
  let observedUrl;
  let observedBody;
  const client = createClient(async (url, init) => {
    observedUrl = url;
    observedBody = init.body;
    return jsonResponse(expected);
  });
  const expectedRequest = request();

  const actual = await client.queryProductionDayShiftMetrics(expectedRequest);

  assert.equal(
    observedUrl.href,
    "http://factory-server:5080/factoryconnect/api/reporting/v1/operational-metrics/production-day-shifts/query",
  );
  assert.equal(observedBody, JSON.stringify(expectedRequest));
  assert.deepEqual(actual, expected);
  assert.equal(actual.items[0].metrics[0].value, "0.37000000000000000001");
  assert.equal(actual.items[0].sourceRevision.position, "18446744073709551615");
  assert.equal(actual.continuationToken, expected.continuationToken);
});

test("accepts an authoritative zero-evidence occurrence", async () => {
  const expected = {
    items: [item({ sourceRevision: null, metrics: [] })],
    continuationToken: null,
  };
  const client = createClient(async () => jsonResponse(expected));

  assert.deepEqual(await client.queryProductionDayShiftMetrics(request()), expected);
});

test("rejects malformed nested reporting identity and metric status", async () => {
  for (const malformed of [
    item({ productionDay: { businessDate: "2026-09-02" } }),
    item({ shift: { shiftId: "shift-a" } }),
    item({ context: { productionOrderId: null } }),
    item({ metrics: [metric({ status: "not-evaluated" })] }),
    item({ sourceRevision: { processorId: "aggregation" } }),
  ]) {
    const client = createClient(async () => jsonResponse({
      items: [malformed],
      continuationToken: null,
    }));
    await assert.rejects(
      client.queryProductionDayShiftMetrics(request()),
      ReportingProtocolFailure,
    );
  }
});

test("uses the existing caller cancellation infrastructure", async () => {
  const controller = new AbortController();
  const reason = new Error("selection replaced");
  controller.abort(reason);
  let calls = 0;
  const client = createClient(async () => {
    calls += 1;
    return jsonResponse({ items: [], continuationToken: null });
  });

  await assert.rejects(
    client.queryProductionDayShiftMetrics(request(), { signal: controller.signal }),
    (failure) => failure instanceof ReportingCancellationFailure && failure.cause === reason,
  );
  assert.equal(calls, 0);
});
