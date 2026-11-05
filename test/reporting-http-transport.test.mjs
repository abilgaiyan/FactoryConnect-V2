import assert from "node:assert/strict";
import test from "node:test";

import { createReportingHttpTransport } from "../src/api/reporting/reporting-http-transport.ts";
import { reportingRoutes } from "../src/api/reporting/reporting-routes.ts";

const timeoutMilliseconds = 30_000;

function createCaptureFetch(response = new Response(null, { status: 200 })) {
  const calls = [];
  const fetch = async (input, init) => {
    calls.push({ input, init });
    return response;
  };

  return { calls, fetch };
}

function createTransport(baseAddress, fetch) {
  return createReportingHttpTransport({
    baseAddress,
    timeoutMilliseconds,
    fetch,
  });
}

test("uses the exact shift reporting route", async () => {
  const capture = createCaptureFetch();
  const transport = createTransport("http://server:5080", capture.fetch);

  await transport.post(reportingRoutes.shiftQuery, { sources: [] });

  assert.equal(
    capture.calls[0].input.href,
    "http://server:5080/api/reporting/v1/operational-metrics/shifts/query",
  );
});

test("uses the exact production-day reporting route", async () => {
  const capture = createCaptureFetch();
  const transport = createTransport("http://server:5080", capture.fetch);

  await transport.post(reportingRoutes.productionDayQuery, { sources: [] });

  assert.equal(
    capture.calls[0].input.href,
    "http://server:5080/api/reporting/v1/operational-metrics/production-days/query",
  );
});

test("normalizes root and base-path addresses without discarding the base path", async () => {
  const cases = [
    ["http://server:5080", "http://server:5080/api/reporting/v1/operational-metrics/shifts/query"],
    ["http://server:5080/", "http://server:5080/api/reporting/v1/operational-metrics/shifts/query"],
    ["http://server:5080/factoryconnect", "http://server:5080/factoryconnect/api/reporting/v1/operational-metrics/shifts/query"],
    ["http://server:5080/factoryconnect/", "http://server:5080/factoryconnect/api/reporting/v1/operational-metrics/shifts/query"],
  ];

  for (const [baseAddress, expected] of cases) {
    const capture = createCaptureFetch();
    const transport = createTransport(baseAddress, capture.fetch);

    await transport.post(reportingRoutes.shiftQuery, {});

    assert.equal(capture.calls[0].input.href, expected);
  }
});

test("sends exact POST headers and JSON serialization", async () => {
  const capture = createCaptureFetch();
  const transport = createTransport("https://factory.example/factoryconnect", capture.fetch);
  const request = {
    sources: [
      { machineId: "machine-b", processorId: "processor-2" },
      { machineId: "machine-a", processorId: "processor-1" },
    ],
    metrics: [],
    context: null,
    statuses: [],
    order: "period-descending",
    pageSize: 50,
    continuationToken: "opaque+/= token",
  };

  await transport.post(reportingRoutes.productionDayQuery, request);

  const { init } = capture.calls[0];
  assert.equal(init.method, "POST");
  assert.deepEqual(init.headers, {
    "Content-Type": "application/json",
    Accept: "application/json, application/problem+json",
  });
  assert.equal(init.body, JSON.stringify(request));
  assert.deepEqual(request.sources.map((source) => source.machineId), ["machine-b", "machine-a"]);
  assert.equal(request.context, null);
  assert.deepEqual(request.metrics, []);
  assert.deepEqual(request.statuses, []);
  assert.equal(request.continuationToken, "opaque+/= token");
  assert.equal("signal" in init, false);
});

test("forwards the caller AbortSignal by identity", async () => {
  const capture = createCaptureFetch();
  const transport = createTransport("http://server:5080", capture.fetch);
  const controller = new AbortController();

  await transport.post(reportingRoutes.shiftQuery, {}, controller.signal);

  assert.equal(capture.calls[0].init.signal, controller.signal);
});

test("uses injected fetch and returns its Response unchanged", async () => {
  const response = new Response("failure", { status: 503 });
  const capture = createCaptureFetch(response);
  const transport = createTransport("http://server:5080", capture.fetch);

  const actual = await transport.post(reportingRoutes.shiftQuery, {});

  assert.equal(capture.calls.length, 1);
  assert.equal(actual, response);
  assert.equal(actual.status, 503);
});

test("does not translate fetch rejection", async () => {
  const failure = new TypeError("network rejected");
  const fetch = async () => {
    throw failure;
  };
  const transport = createTransport("http://server:5080", fetch);

  await assert.rejects(
    transport.post(reportingRoutes.shiftQuery, {}),
    (error) => error === failure,
  );
});

test("rejects invalid construction options before any request", () => {
  const invalidBaseAddresses = [
    "/relative",
    "ftp://server/reporting",
    "http://user:password@server:5080",
    "http://server:5080?tenant=a",
    "http://server:5080/#section",
  ];

  for (const baseAddress of invalidBaseAddresses) {
    let called = false;
    const fetch = async () => {
      called = true;
      return new Response();
    };

    assert.throws(() => createTransport(baseAddress, fetch));
    assert.equal(called, false);
  }

  for (const value of [0, -1, Number.NaN, Number.POSITIVE_INFINITY, 300_001]) {
    assert.throws(() =>
      createReportingHttpTransport({
        baseAddress: "http://server:5080",
        timeoutMilliseconds: value,
        fetch: async () => new Response(),
      }),
    );
  }
});

test("one request does not mutate another", async () => {
  const capture = createCaptureFetch();
  const transport = createTransport("http://server:5080/factoryconnect", capture.fetch);
  const first = { continuationToken: "first", statuses: ["calculated"] };
  const second = { continuationToken: null, statuses: [] };

  await transport.post(reportingRoutes.shiftQuery, first);
  await transport.post(reportingRoutes.shiftQuery, second);

  assert.equal(capture.calls[0].init.body, JSON.stringify(first));
  assert.equal(capture.calls[1].init.body, JSON.stringify(second));
  assert.deepEqual(first, { continuationToken: "first", statuses: ["calculated"] });
  assert.deepEqual(second, { continuationToken: null, statuses: [] });
});
