import assert from "node:assert/strict";
import test from "node:test";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { createDashboardApplicationRuntime } from "../src/application/application-runtime.ts";
import { loadDashboardRuntimeConfiguration } from "../src/application/runtime-configuration.ts";
import { presentQueryState } from "../src/query/query-state-presentation.ts";

const runtimeConfiguration = {
  reportingBasePath: "/",
  requestTimeoutMilliseconds: 30_000,
  sources: [
    {
      machineId: "11111111-1111-1111-1111-111111111111",
      processorId: "operational-metrics",
      siteId: "factory-1",
      productionLineId: "line-1",
      displayName: "Machine 1",
      groupName: "Line 1",
      displayOrder: 10,
    },
  ],
};

const productionDayRequest = {
  sources: [
    {
      machineId: "11111111-1111-1111-1111-111111111111",
      processorId: "operational-metrics",
    },
  ],
  fromInclusive: "2026-08-30",
  toExclusive: "2026-08-31",
  order: "period-ascending",
  pageSize: 25,
};

test("runtime configuration loads only from the dashboard same-origin endpoint", async () => {
  const calls = [];
  const configuration = await loadDashboardRuntimeConfiguration(async (input, init) => {
    calls.push({ input, init });
    return new Response(JSON.stringify(runtimeConfiguration), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  });

  assert.deepEqual(configuration, runtimeConfiguration);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].input, "/dashboard/config");
  assert.equal(calls[0].init.method, "GET");
});

test("runtime configuration rejects cross-origin and scheme-relative reporting paths", async () => {
  for (const reportingBasePath of [
    "https://other-host.example/",
    "//other-host.example/",
    "/factoryconnect/",
  ]) {
    await assert.rejects(
      loadDashboardRuntimeConfiguration(async () => new Response(JSON.stringify({
        ...runtimeConfiguration,
        reportingBasePath,
      }), { status: 200 })),
      /malformed/,
    );
  }
});

test("runtime configuration accepts an empty configured factory", async () => {
  const emptyConfiguration = {
    ...runtimeConfiguration,
    sources: [],
  };

  const configuration = await loadDashboardRuntimeConfiguration(async () => new Response(JSON.stringify(
    emptyConfiguration,
  ), { status: 200 }));

  assert.deepEqual(configuration, emptyConfiguration);
});

test("runtime configuration rejects malformed overview presentation metadata", async () => {
  for (const source of [
    { ...runtimeConfiguration.sources[0], siteId: "" },
    { ...runtimeConfiguration.sources[0], productionLineId: "" },
    { ...runtimeConfiguration.sources[0], groupName: "" },
    { ...runtimeConfiguration.sources[0], displayOrder: -1 },
    { ...runtimeConfiguration.sources[0], displayOrder: 1.5 },
  ]) {
    await assert.rejects(
      loadDashboardRuntimeConfiguration(async () => new Response(JSON.stringify({
        ...runtimeConfiguration,
        sources: [source],
      }), { status: 200 })),
      /malformed/,
    );
  }
});

test("application runtime composes reporting requests against the dashboard origin", async () => {
  const calls = [];
  const fetchImplementation = async (input, init) => {
    calls.push({ input: input.toString(), init });

    if (input.toString() === "/dashboard/config") {
      return new Response(JSON.stringify(runtimeConfiguration), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }

    return new Response(JSON.stringify({
      items: [],
      continuationToken: null,
    }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };

  const runtime = await createDashboardApplicationRuntime(
    "http://factory-dashboard:5090",
    fetchImplementation,
  );

  const page = await runtime.reportingClient.queryProductionDayMetrics(productionDayRequest);

  assert.deepEqual(runtime.configuration, runtimeConfiguration);
  assert.deepEqual(page, { items: [], continuationToken: null });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].input, "/dashboard/config");
  assert.equal(
    calls[1].input,
    "http://factory-dashboard:5090/api/reporting/v1/operational-metrics/production-days/query",
  );
});

test("malformed runtime configuration is rejected before application composition", async () => {
  await assert.rejects(
    loadDashboardRuntimeConfiguration(async () => new Response(JSON.stringify({
      ...runtimeConfiguration,
      requestTimeoutMilliseconds: 0,
    }), { status: 200 })),
    /malformed/,
  );
});

test("all query states have deterministic presentation without metric interpretation", () => {
  const problem = { title: "Invalid", detail: "Bad query" };
  const failure = new ReportingNetworkFailure(new Error("offline"));

  assert.deepEqual(presentQueryState({ kind: "idle" }), { kind: "idle", message: "Ready." });
  assert.deepEqual(presentQueryState({ kind: "loading" }), { kind: "loading", message: "Loading." });
  assert.deepEqual(
    presentQueryState({ kind: "success", data: { items: [{ value: 0 }] } }),
    { kind: "success", message: "Data loaded." },
  );
  assert.deepEqual(presentQueryState({ kind: "empty" }), { kind: "empty", message: "No matching data." });
  assert.deepEqual(
    presentQueryState({ kind: "invalidRequest", details: problem }),
    { kind: "invalidRequest", message: "Bad query" },
  );
  assert.deepEqual(
    presentQueryState({ kind: "failed", failure }),
    { kind: "failed", message: failure.message },
  );
});
