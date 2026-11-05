import assert from "node:assert/strict";
import test from "node:test";

import * as reporting from "../src/api/reporting/index.ts";
import {
  ReportingCancellationFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingNetworkFailure,
  ReportingProtocolFailure,
  createReportingClient,
} from "../src/api/reporting/index.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

function shiftRequest(overrides = {}) {
  return {
    sources: [{ machineId, processorId: "operational-metrics" }],
    startsAtOrAfterUtc: "2026-08-30T00:00:00Z",
    startsBeforeUtc: "2026-08-31T00:00:00Z",
    metrics: null,
    context: null,
    statuses: null,
    order: "period-ascending",
    pageSize: 50,
    continuationToken: null,
    ...overrides,
  };
}

function productionDayRequest(overrides = {}) {
  return {
    sources: [{ machineId, processorId: "operational-metrics" }],
    fromInclusive: "2026-08-30",
    toExclusive: "2026-08-31",
    metrics: null,
    context: null,
    statuses: null,
    order: "period-ascending",
    pageSize: 50,
    continuationToken: null,
    ...overrides,
  };
}

function shiftItem(overrides = {}) {
  return {
    scope: "shift",
    processorId: "operational-metrics",
    machineId,
    shift: {
      siteId: "site-1",
      shiftScheduleAssignmentId: "assignment-1",
      shiftId: "shift-a",
      startsAtUtc: "2026-08-30T00:00:00Z",
      endsAtUtc: "2026-08-30T08:00:00Z",
    },
    productionDay: null,
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    metricKey: "availability",
    definitionVersion: "1.0",
    status: "calculated",
    value: 0.75,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: "operational-metrics",
      machineId,
      streamKey: "machine-1",
      position: 42,
    },
    ...overrides,
  };
}

function productionDayItem(overrides = {}) {
  return shiftItem({
    scope: "production-day",
    shift: null,
    productionDay: { siteId: "site-1", businessDate: "2026-08-30" },
    ...overrides,
  });
}

function page(item = shiftItem(), continuationToken = null) {
  return { items: [item], continuationToken };
}

function jsonResponse(body, status = 200, contentType = "application/json") {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": contentType },
  });
}

function problemResponse(type) {
  return jsonResponse(
    { type, title: "Reporting problem", status: 400, detail: "problem" },
    400,
    "application/problem+json",
  );
}

function createClient(fetch, timeoutMilliseconds = 1000) {
  return createReportingClient({
    baseAddress: "http://factory-server:5080/factoryconnect",
    timeoutMilliseconds,
    fetch,
  });
}

test("public shift query uses the shift operation and decodes its page", async () => {
  let requestUrl;
  const expected = page();
  const client = createClient(async (url) => {
    requestUrl = url;
    return jsonResponse(expected);
  });

  const actual = await client.queryShiftMetrics(shiftRequest());

  assert.deepEqual(actual, expected);
  assert.equal(
    requestUrl.href,
    "http://factory-server:5080/factoryconnect/api/reporting/v1/operational-metrics/shifts/query",
  );
});

test("public production-day query uses the production-day operation and decodes its page", async () => {
  let requestUrl;
  const expected = page(productionDayItem());
  const client = createClient(async (url) => {
    requestUrl = url;
    return jsonResponse(expected);
  });

  const actual = await client.queryProductionDayMetrics(productionDayRequest());

  assert.deepEqual(actual, expected);
  assert.equal(
    requestUrl.href,
    "http://factory-server:5080/factoryconnect/api/reporting/v1/operational-metrics/production-days/query",
  );
});

test("public composition preserves exact request serialization", async () => {
  const request = shiftRequest({
    metrics: [{ metricKey: "oee", version: "1.0" }],
    statuses: ["calculated", "unavailable"],
    continuationToken: "opaque+/=token",
  });
  let body;
  const client = createClient(async (_url, init) => {
    body = init.body;
    return jsonResponse({ items: [], continuationToken: null });
  });

  await client.queryShiftMetrics(request);

  assert.equal(body, JSON.stringify(request));
  assert.deepEqual(JSON.parse(body), request);
});

test("calculated zero survives public composition", async () => {
  const client = createClient(async () => jsonResponse(page(shiftItem({ value: 0 }))));
  const actual = await client.queryShiftMetrics(shiftRequest());
  assert.equal(actual.items[0].value, 0);
});

test("empty page survives public composition", async () => {
  const expected = { items: [], continuationToken: null };
  const client = createClient(async () => jsonResponse(expected));
  assert.deepEqual(await client.queryShiftMetrics(shiftRequest()), expected);
});

test("continuation tokens survive request and response unchanged", async () => {
  const requestToken = "request+/=opaque";
  const responseToken = "response+/=opaque";
  let observedRequest;
  const client = createClient(async (_url, init) => {
    observedRequest = JSON.parse(init.body);
    return jsonResponse({ items: [], continuationToken: responseToken });
  });

  const actual = await client.queryShiftMetrics(shiftRequest({ continuationToken: requestToken }));

  assert.equal(observedRequest.continuationToken, requestToken);
  assert.equal(actual.continuationToken, responseToken);
});

test("pre-aborted caller remains cancellation and fetch is not invoked", async () => {
  const controller = new AbortController();
  const reason = new Error("navigation replaced");
  controller.abort(reason);
  let calls = 0;
  const client = createClient(async () => {
    calls += 1;
    return jsonResponse(page());
  });

  await assert.rejects(
    client.queryShiftMetrics(shiftRequest(), { signal: controller.signal }),
    (failure) => failure instanceof ReportingCancellationFailure && failure.cause === reason,
  );
  assert.equal(calls, 0);
});

test("network rejection remains ReportingNetworkFailure", async () => {
  const cause = { code: "ECONNRESET" };
  const client = createClient(async () => { throw cause; });

  await assert.rejects(
    client.queryShiftMetrics(shiftRequest()),
    (failure) => failure instanceof ReportingNetworkFailure && failure.cause === cause,
  );
});

test("known Problem Details failures remain individually typed", async () => {
  const cases = [
    ["urn:factoryconnect:problem:reporting:invalid-request", ReportingInvalidQueryFailure],
    ["urn:factoryconnect:problem:reporting:malformed-continuation-token", ReportingMalformedContinuationTokenFailure],
    ["urn:factoryconnect:problem:reporting:incompatible-continuation-token", ReportingIncompatibleContinuationTokenFailure],
  ];

  for (const [type, failureType] of cases) {
    const client = createClient(async () => problemResponse(type));
    await assert.rejects(client.queryShiftMetrics(shiftRequest()), failureType);
  }
});

test("unknown Problem Details remains ReportingHttpFailure with details", async () => {
  const type = "urn:factoryconnect:problem:reporting:future";
  const client = createClient(async () => problemResponse(type));

  await assert.rejects(
    client.queryShiftMetrics(shiftRequest()),
    (failure) => failure instanceof ReportingHttpFailure
      && failure.status === 400
      && failure.problemDetails?.type === type,
  );
});

test("malformed successful response remains ReportingProtocolFailure", async () => {
  const client = createClient(async () => jsonResponse({ items: "not-an-array", continuationToken: null }));
  await assert.rejects(client.queryShiftMetrics(shiftRequest()), ReportingProtocolFailure);
});

test("5xx remains ReportingHttpFailure", async () => {
  const client = createClient(async () => new Response("failure", { status: 503 }));
  await assert.rejects(
    client.queryShiftMetrics(shiftRequest()),
    (failure) => failure instanceof ReportingHttpFailure && failure.status === 503,
  );
});

test("concurrent public calls remain isolated", async () => {
  const calls = [];
  const fetch = (_url, init) => new Promise((resolve, reject) => {
    const call = { signal: init.signal, resolve, reject };
    calls.push(call);
    init.signal.addEventListener("abort", () => reject(init.signal.reason), { once: true });
  });
  const client = createClient(fetch);
  const firstCaller = new AbortController();
  const secondCaller = new AbortController();

  const first = client.queryShiftMetrics(shiftRequest(), { signal: firstCaller.signal });
  const second = client.queryShiftMetrics(shiftRequest(), { signal: secondCaller.signal });

  assert.notEqual(calls[0].signal, calls[1].signal);
  firstCaller.abort("superseded");
  calls[1].resolve(jsonResponse({ items: [], continuationToken: null }));

  await assert.rejects(first, ReportingCancellationFailure);
  assert.deepEqual(await second, { items: [], continuationToken: null });
  assert.equal(calls[1].signal.aborted, false);
});

test("a failure is never converted into an empty page", async () => {
  const client = createClient(async () => new Response("failure", { status: 500 }));
  await assert.rejects(client.queryShiftMetrics(shiftRequest()), ReportingHttpFailure);
});

test("public entry point does not expose internal composition modules", () => {
  for (const internalName of [
    "reportingRoutes",
    "createReportingHttpTransport",
    "createReportingRequestExecutor",
    "createReportingResponseDecoder",
  ]) {
    assert.equal(Object.hasOwn(reporting, internalName), false);
  }

  assert.equal(typeof reporting.createReportingClient, "function");
});
