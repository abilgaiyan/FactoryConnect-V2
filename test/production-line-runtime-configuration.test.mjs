import assert from "node:assert/strict";
import test from "node:test";

import { loadDashboardRuntimeConfiguration } from "../src/application/runtime-configuration.ts";
import { buildProductionDayShiftQueryRequest } from "../src/application/production-day-shift-reporting.ts";

const baseSource = {
  machineId: "11111111-1111-1111-1111-111111111111",
  processorId: "operational-metrics",
  siteId: "plant-1",
  productionLineId: "canonical-line-17",
  displayName: "Machine 1",
  groupName: "Turning Cell A",
  displayOrder: 10,
};

function responseWith(source = baseSource) {
  return async () => new Response(JSON.stringify({
    reportingBasePath: "/",
    requestTimeoutMilliseconds: 30_000,
    sources: [source],
  }), { status: 200, headers: { "Content-Type": "application/json" } });
}

test("runtime decoder requires and preserves canonical production line identity", async () => {
  const configuration = await loadDashboardRuntimeConfiguration(responseWith());
  assert.equal(configuration.sources[0].productionLineId, "canonical-line-17");
  assert.equal(configuration.sources[0].groupName, "Turning Cell A");
  assert.notEqual(configuration.sources[0].productionLineId, configuration.sources[0].groupName);
});

test("runtime decoder rejects missing malformed and non-canonical production line identity", async () => {
  const { productionLineId: _, ...missing } = baseSource;
  for (const source of [
    missing,
    { ...baseSource, productionLineId: "" },
    { ...baseSource, productionLineId: " " },
    { ...baseSource, productionLineId: " canonical-line-17" },
    { ...baseSource, productionLineId: "canonical-line-17 " },
  ]) {
    await assert.rejects(
      loadDashboardRuntimeConfiguration(responseWith(source)),
      /malformed/,
    );
  }
});

test("enriched source does not widen production-day-shift reporting selection", () => {
  const request = buildProductionDayShiftQueryRequest("2026-09-02", [baseSource]);
  assert.deepEqual(request.sources, [{
    machineId: baseSource.machineId,
    processorId: baseSource.processorId,
    siteId: baseSource.siteId,
    businessDate: "2026-09-02",
  }]);
  assert.equal("productionLineId" in request.sources[0], false);
  assert.equal("groupName" in request.sources[0], false);
});
