import assert from "node:assert/strict";
import test from "node:test";

import { createReportingResponseDecoder } from "../src/api/reporting/reporting-response-decoder.ts";
import {
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingProtocolFailure,
} from "../src/api/reporting/reporting-response-failures.ts";

const decoder = createReportingResponseDecoder();

function validContext(overrides = {}) {
  return {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    ...overrides,
  };
}

function validSourceRevision(overrides = {}) {
  return {
    processorId: "operational-metrics",
    machineId: "11111111-1111-1111-1111-111111111111",
    streamKey: "machine-1",
    position: 42,
    ...overrides,
  };
}

function validShift(overrides = {}) {
  return {
    siteId: "site-1",
    shiftScheduleAssignmentId: "assignment-1",
    shiftId: "shift-a",
    startsAtUtc: "2026-08-30T00:00:00Z",
    endsAtUtc: "2026-08-30T08:00:00Z",
    ...overrides,
  };
}

function validProductionDay(overrides = {}) {
  return {
    siteId: "site-1",
    businessDate: "2026-08-30",
    ...overrides,
  };
}

function validShiftItem(overrides = {}) {
  return {
    scope: "shift",
    processorId: "operational-metrics",
    machineId: "11111111-1111-1111-1111-111111111111",
    shift: validShift(),
    productionDay: null,
    context: validContext(),
    metricKey: "availability",
    definitionVersion: "1.0",
    status: "calculated",
    value: 0.75,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: validSourceRevision(),
    ...overrides,
  };
}

function validProductionDayItem(overrides = {}) {
  return validShiftItem({
    scope: "production-day",
    shift: null,
    productionDay: validProductionDay(),
    ...overrides,
  });
}

function jsonResponse(body, status = 200, contentType = "application/json") {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": contentType },
  });
}

function problemResponse(body, contentType = "application/problem+json") {
  return jsonResponse(body, 400, contentType);
}

function validPage(item = validShiftItem(), continuationToken = null) {
  return { items: [item], continuationToken };
}

async function assertProtocolFailure(response) {
  await assert.rejects(decoder.decode(response), ReportingProtocolFailure);
}

test("decodes a valid calculated metric page", async () => {
  const page = validPage();
  const actual = await decoder.decode(jsonResponse(page));
  assert.deepEqual(actual, page);
});

test("preserves calculated zero", async () => {
  const page = validPage(validShiftItem({ value: 0 }));
  const actual = await decoder.decode(jsonResponse(page));
  assert.equal(actual.items[0].value, 0);
});

test("decodes unavailable metric reasons", async () => {
  const item = validShiftItem({
    status: "unavailable",
    value: null,
    reasonCode: "missing-operand",
    reasonOperandName: "ActualProductionTime",
  });
  assert.deepEqual((await decoder.decode(jsonResponse(validPage(item)))).items[0], item);
});

test("decodes insufficient-evidence metric reasons", async () => {
  const item = validShiftItem({
    status: "insufficient-evidence",
    value: null,
    reasonCode: "insufficient-input",
    reasonOperandName: "GoodCount",
  });
  assert.deepEqual((await decoder.decode(jsonResponse(validPage(item)))).items[0], item);
});

test("decodes an empty page", async () => {
  assert.deepEqual(await decoder.decode(jsonResponse({ items: [], continuationToken: null })), {
    items: [],
    continuationToken: null,
  });
});

test("preserves an opaque continuation token", async () => {
  const token = "opaque+/=token.do-not-parse";
  assert.equal((await decoder.decode(jsonResponse(validPage(validShiftItem(), token)))).continuationToken, token);
});

test("accepts JSON media type parameters", async () => {
  await decoder.decode(jsonResponse(validPage(), 200, "application/json; charset=utf-8"));
});

test("accepts both shift and production-day period shapes", async () => {
  const page = { items: [validShiftItem(), validProductionDayItem()], continuationToken: null };
  assert.equal((await decoder.decode(jsonResponse(page))).items.length, 2);
});

test("rejects invalid scope and period combinations", async () => {
  await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ productionDay: validProductionDay() }))));
  await assertProtocolFailure(jsonResponse(validPage(validProductionDayItem({ shift: validShift() }))));
  await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ shift: null }))));
});

test("rejects unknown metric status", async () => {
  await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ status: "not-evaluated" }))));
});

test("rejects missing required fields", async () => {
  const item = validShiftItem();
  delete item.metricKey;
  await assertProtocolFailure(jsonResponse(validPage(item)));
});

test("rejects malformed context", async () => {
  await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ context: { productionOrderId: null } }))));
});

test("rejects malformed source revision", async () => {
  await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ sourceRevision: { processorId: "x" } }))));
});

test("validates source position representations", async () => {
  for (const position of [0, Number.MAX_SAFE_INTEGER, "0", "18446744073709551615"]) {
    await decoder.decode(jsonResponse(validPage(validShiftItem({ sourceRevision: validSourceRevision({ position }) }))));
  }

  for (const position of [-1, 1.5, Number.MAX_SAFE_INTEGER + 1, "-1", "1.5", "abc", ""]) {
    await assertProtocolFailure(
      jsonResponse(validPage(validShiftItem({ sourceRevision: validSourceRevision({ position }) }))),
    );
  }
});

test("validates finite metric numeric representations", async () => {
  for (const value of [0, -1.25, 1e6, "0", "-1.25", "1e6"]) {
    await decoder.decode(jsonResponse(validPage(validShiftItem({ value }))));
  }

  for (const value of ["NaN", "Infinity", "1e309", "1x", ""]) {
    await assertProtocolFailure(jsonResponse(validPage(validShiftItem({ value }))));
  }

  const overflowingNumericJson = JSON.stringify(validPage())
    .replace('"value":0.75', '"value":1e309');
  await assertProtocolFailure(new Response(overflowingNumericJson, {
    status: 200,
    headers: { "Content-Type": "application/json" },
  }));
});

test("rejects malformed and empty JSON success bodies", async () => {
  await assertProtocolFailure(new Response("{", { status: 200, headers: { "Content-Type": "application/json" } }));
  await assertProtocolFailure(new Response("", { status: 200, headers: { "Content-Type": "application/json" } }));
});

test("rejects wrong success content type", async () => {
  await assertProtocolFailure(new Response("<html></html>", { status: 200, headers: { "Content-Type": "text/html" } }));
});

test("classifies invalid reporting query Problem Details", async () => {
  const problem = {
    type: "urn:factoryconnect:problem:reporting:invalid-request",
    title: "Invalid reporting query",
    status: 400,
    detail: "invalid",
    code: "invalid-reporting-query",
  };
  await assert.rejects(
    decoder.decode(problemResponse(problem)),
    (failure) => failure instanceof ReportingInvalidQueryFailure && failure.problemDetails.type === problem.type,
  );
});

test("classifies malformed continuation token Problem Details", async () => {
  const problem = {
    type: "urn:factoryconnect:problem:reporting:malformed-continuation-token",
    title: "Malformed continuation token",
    status: 400,
  };
  await assert.rejects(
    decoder.decode(problemResponse(problem, "application/problem+json; charset=utf-8")),
    ReportingMalformedContinuationTokenFailure,
  );
});

test("classifies incompatible continuation token Problem Details", async () => {
  const problem = {
    type: "urn:factoryconnect:problem:reporting:incompatible-continuation-token",
    title: "Incompatible continuation token",
    status: "400",
  };
  await assert.rejects(
    decoder.decode(problemResponse(problem)),
    ReportingIncompatibleContinuationTokenFailure,
  );
});

test("unknown valid Problem Details remains HTTP failure and retains details", async () => {
  const problem = {
    type: "urn:factoryconnect:problem:reporting:future-problem",
    title: "Future problem",
    status: 400,
    detail: null,
  };
  await assert.rejects(
    decoder.decode(problemResponse(problem)),
    (failure) => failure instanceof ReportingHttpFailure
      && failure.status === 400
      && failure.problemDetails?.type === problem.type,
  );
});

test("rejects malformed Problem Details", async () => {
  await assertProtocolFailure(problemResponse({ type: 42, title: "bad" }));
});

test("rejects wrong Problem Details content type without parsing it", async () => {
  await assertProtocolFailure(problemResponse({ type: "unknown" }, "text/html; charset=utf-8"));
});

test("unexpected successful status is protocol failure", async () => {
  await assertProtocolFailure(new Response(null, { status: 204 }));
});

test("unexpected 4xx remains HTTP failure", async () => {
  await assert.rejects(decoder.decode(new Response("missing", { status: 404 })), ReportingHttpFailure);
});

test("5xx remains HTTP failure", async () => {
  await assert.rejects(decoder.decode(new Response("failure", { status: 503 })), ReportingHttpFailure);
});

test("response body is consumed exactly once", async () => {
  const response = jsonResponse(validPage());
  assert.equal(response.bodyUsed, false);
  await decoder.decode(response);
  assert.equal(response.bodyUsed, true);
  await assert.rejects(response.json());
});
