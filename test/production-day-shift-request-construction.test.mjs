import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import {
  buildProductionDayShiftQueryRequest,
  isProductionDayIdentity,
} from "../src/application/production-day-shift-reporting.ts";

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

function source(index) {
  return {
    machineId: `00000000-0000-0000-0000-${String(index + 1).padStart(12, "0")}`,
    processorId,
    siteId: index % 2 === 0 ? "site-a" : "site-b",
    displayName: `Machine ${index + 1}`,
    groupName: index % 2 === 0 ? "Line A" : "Line B",
    displayOrder: index,
  };
}

test("constructs exact authoritative business-day source identities", () => {
  const sources = [source(0), source(1)];
  const request = buildProductionDayShiftQueryRequest("2026-09-02", sources);

  assert.deepEqual(request.sources, [
    {
      machineId: sources[0].machineId,
      processorId,
      siteId: "site-a",
      businessDate: "2026-09-02",
    },
    {
      machineId: sources[1].machineId,
      processorId,
      siteId: "site-b",
      businessDate: "2026-09-02",
    },
  ]);
  assert.deepEqual(request.context, expectedContext);
  assert.deepEqual(request.metrics, expectedMetrics);
  assert.equal(request.statuses, null);
  assert.equal(request.pageSize, 200);
  assert.equal(request.continuationToken, null);
  assert.equal("fromInclusive" in request, false);
  assert.equal("toExclusive" in request, false);
  assert.equal("order" in request, false);
});

test("preserves an opaque continuation token verbatim", () => {
  const token = "opaque+/=token.do-not-decode";
  assert.equal(
    buildProductionDayShiftQueryRequest("2026-09-02", [source(0)], token).continuationToken,
    token,
  );
});

test("0 1 7 and 50 configured populations map exactly without a fixed machine assumption", () => {
  for (const count of [0, 1, 7, 50]) {
    const configured = Array.from({ length: count }, (_, index) => source(index));
    const request = buildProductionDayShiftQueryRequest("2026-09-02", configured);

    assert.equal(request.sources.length, count);
    assert.deepEqual(
      request.sources,
      configured.map(({ machineId, processorId, siteId }) => ({
        machineId,
        processorId,
        siteId,
        businessDate: "2026-09-02",
      })),
    );
  }
});

test("validates the business-date identity without Date or timezone arithmetic", () => {
  for (const valid of ["0001-01-01", "2000-02-29", "2026-09-02", "9999-12-31"]) {
    assert.equal(isProductionDayIdentity(valid), true, valid);
  }

  for (const invalid of [
    "2026-9-02",
    "2026-09-2",
    "2026-02-29",
    "2100-02-29",
    "2026-04-31",
    "2026-13-01",
    "0000-12-31",
    "10000-01-01",
    "2026-09-02T00:00:00Z",
  ]) {
    assert.equal(isProductionDayIdentity(invalid), false, invalid);
    assert.throws(
      () => buildProductionDayShiftQueryRequest(invalid, [source(0)]),
      RangeError,
    );
  }
});

test("shift request construction contains no date arithmetic UTC range or schedule reconstruction", () => {
  const sourceText = fs.readFileSync(
    new URL("../src/application/production-day-shift-reporting.ts", import.meta.url),
    "utf8",
  );

  for (const forbidden of [
    "new Date(",
    "Date.parse(",
    "setUTCDate(",
    "nextProductionDay",
    "fromInclusive",
    "toExclusive",
    "startsAtUtc",
    "endsAtUtc",
    "shiftScheduleAssignmentId",
  ]) {
    assert.equal(sourceText.includes(forbidden), false, `forbidden shift selection construct: ${forbidden}`);
  }
});
