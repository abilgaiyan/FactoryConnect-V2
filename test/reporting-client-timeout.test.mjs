import assert from "node:assert/strict";
import test from "node:test";

import {
  ReportingTimeoutFailure,
  createReportingClient,
} from "../src/api/reporting/index.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

function shiftRequest() {
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
  };
}

test("public client enforces the configured timeout through composition", async () => {
  const timeoutMilliseconds = 25;
  let observedSignal;
  const fetch = (_url, init) => {
    observedSignal = init.signal;
    return new Promise((_resolve, reject) => {
      init.signal.addEventListener(
        "abort",
        () => reject(init.signal.reason),
        { once: true },
      );
    });
  };
  const client = createReportingClient({
    baseAddress: "http://factory-server:5080/factoryconnect",
    timeoutMilliseconds,
    fetch,
  });

  await assert.rejects(
    client.queryShiftMetrics(shiftRequest()),
    (failure) => failure instanceof ReportingTimeoutFailure
      && failure.timeoutMilliseconds === timeoutMilliseconds,
  );

  assert.equal(observedSignal.aborted, true);
  assert.ok(observedSignal.reason instanceof ReportingTimeoutFailure);
  assert.equal(observedSignal.reason.timeoutMilliseconds, timeoutMilliseconds);
});
