import assert from "node:assert/strict";
import test from "node:test";

import { createReportingResponseDecoder } from "../src/api/reporting/reporting-response-decoder.ts";
import { ReportingProtocolFailure } from "../src/api/reporting/reporting-response-failures.ts";

function pageWithPosition(position) {
  return {
    items: [{
      scope: "shift",
      processorId: "operational-metrics",
      machineId: "11111111-1111-1111-1111-111111111111",
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
      value: 1,
      unit: "ratio",
      reasonCode: null,
      reasonOperandName: null,
      sourceRevision: {
        processorId: "operational-metrics",
        machineId: "11111111-1111-1111-1111-111111111111",
        streamKey: "machine-1",
        position,
      },
    }],
    continuationToken: null,
  };
}

function response(position) {
  return new Response(JSON.stringify(pageWithPosition(position)), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

test("string source positions are constrained to the UInt64 range", async () => {
  const decoder = createReportingResponseDecoder();

  await decoder.decode(response("18446744073709551615"));
  await assert.rejects(
    decoder.decode(response("18446744073709551616")),
    ReportingProtocolFailure,
  );
});
