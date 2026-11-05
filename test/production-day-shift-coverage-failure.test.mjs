import assert from "node:assert/strict";
import test from "node:test";

import { createReportingClient } from "../src/api/reporting/reporting-client.ts";
import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingHttpFailure,
  ReportingProtocolFailure,
} from "../src/api/reporting/reporting-response-failures.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";
import { presentQueryState } from "../src/query/query-state-presentation.ts";
import { deriveProductionDayOverviewViewState } from "../src/application/production-day-overview-state.ts";

const machineId = "11111111-1111-1111-1111-111111111111";
const siteId = "site-a";
const businessDate = "2026-09-02";
const problemType = "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required";
const problemCode = "production-day-shift-roster-coverage-required";

function problem(overrides = {}) {
  return {
    type: problemType,
    title: "Production-day shift roster coverage required",
    status: 409,
    code: problemCode,
    machineId,
    siteId,
    businessDate,
    ...overrides,
  };
}

function coverageFailure(overrides = {}) {
  const details = { machineId, siteId, businessDate, ...overrides };
  return new ProductionDayShiftRosterCoverageRequiredFailure(problem(details), details);
}

function clientFor(responseFactory) {
  return createReportingClient({
    baseAddress: "http://dashboard.test/",
    timeoutMilliseconds: 1000,
    fetch: async () => responseFactory(),
  });
}

function conflict(body, contentType = "application/problem+json") {
  return new Response(JSON.stringify(body), { status: 409, headers: { "Content-Type": contentType } });
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

test("production-day shift 409 becomes typed roster coverage failure with authoritative identity", async () => {
  const client = clientFor(() => conflict(problem()));
  await assert.rejects(
    client.queryProductionDayShiftMetrics({}),
    failure => failure instanceof ProductionDayShiftRosterCoverageRequiredFailure
      && failure.machineId === machineId
      && failure.siteId === siteId
      && failure.businessDate === businessDate
      && failure.problemDetails.type === problemType,
  );
});

test("unknown 409 remains generic HTTP failure and retains Problem Details", async () => {
  const client = clientFor(() => conflict({ type: "urn:factoryconnect:problem:reporting:future-conflict", status: 409 }));
  await assert.rejects(
    client.queryProductionDayShiftMetrics({}),
    failure => failure instanceof ReportingHttpFailure
      && failure.status === 409
      && failure.problemDetails?.type === "urn:factoryconnect:problem:reporting:future-conflict",
  );
});

test("malformed known coverage identity is a protocol failure rather than a partial typed failure", async () => {
  for (const malformed of [
    problem({ code: "wrong" }),
    problem({ machineId: "" }),
    problem({ machineId: "not-a-guid" }),
    problem({ machineId: "00000000-0000-0000-0000-000000000000" }),
    problem({ siteId: " " }),
    problem({ siteId: " site-a" }),
    problem({ siteId: "site-a " }),
    problem({ businessDate: "02-09-2026" }),
    problem({ businessDate: "2026-02-30" }),
    problem({ businessDate: "0000-01-01" }),
  ]) {
    const client = clientFor(() => conflict(malformed));
    await assert.rejects(client.queryProductionDayShiftMetrics({}), ReportingProtocolFailure);
  }
});

test("coverage business date accepts DateOnly calendar boundaries and leap days", async () => {
  for (const validDate of ["0001-01-01", "2000-02-29", "9999-12-31"]) {
    const client = clientFor(() => conflict(problem({ businessDate: validDate })));
    await assert.rejects(
      client.queryProductionDayShiftMetrics({}),
      failure => failure instanceof ProductionDayShiftRosterCoverageRequiredFailure
        && failure.businessDate === validDate,
    );
  }
});

test("coverage conflict requires Problem Details media type", async () => {
  const client = clientFor(() => conflict(problem(), "application/json"));
  await assert.rejects(client.queryProductionDayShiftMetrics({}), ReportingProtocolFailure);
});

test("query lifecycle publishes roster coverage separately from generic failure", async () => {
  const failure = coverageFailure();
  const controller = createQueryLifecycleController({
    query: async () => { throw failure; },
    isEmpty: () => false,
  });

  assert.deepEqual(await controller.execute(), {
    kind: "coverageRequired",
    details: { machineId, siteId, businessDate },
  });
});

test("query-state presentation renders roster coverage identity distinctly", () => {
  assert.deepEqual(
    presentQueryState({ kind: "coverageRequired", details: { machineId, siteId, businessDate } }),
    {
      kind: "coverageRequired",
      message: `Shift roster coverage is required for machine ${machineId}, site ${siteId}, production day ${businessDate}.`,
    },
  );
});

test("superseded coverage failure cannot overwrite the current request", async () => {
  const first = deferred();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => (++call === 1 ? first.promise : { value: "current" }),
    isEmpty: () => false,
  });

  const obsoleteExecution = controller.execute();
  const currentState = await controller.execute();
  assert.deepEqual(currentState, { kind: "success", data: { value: "current" } });

  first.reject(coverageFailure());
  await obsoleteExecution;
  assert.deepEqual(controller.current(), { kind: "success", data: { value: "current" } });
});

test("coverage failure after disposal cannot publish", async () => {
  const pending = deferred();
  const controller = createQueryLifecycleController({
    query: async () => pending.promise,
    isEmpty: () => false,
  });

  const execution = controller.execute();
  controller.dispose();
  pending.reject(coverageFailure());
  await execution;
  assert.deepEqual(controller.current(), { kind: "loading" });
});

test("production-day overview handles globally extended coverage state without throwing", () => {
  const source = {
    machineId,
    processorId: "operational-metrics",
    siteId,
    productionLineId: "line-a",
    displayName: "Machine A",
    groupName: "Line A",
    displayOrder: 1,
  };
  assert.deepEqual(
    deriveProductionDayOverviewViewState(
      { kind: "coverageRequired", details: { machineId, siteId, businessDate } },
      businessDate,
      [source],
    ),
    {
      kind: "reporting-failed",
      message: "Production-day reporting is unavailable because the requested shift roster has not been materialized.",
    },
  );
});

test("generic reporting failures remain failed rather than coverageRequired", async () => {
  const failure = new ReportingHttpFailure(503);
  const controller = createQueryLifecycleController({
    query: async () => { throw failure; },
    isEmpty: () => false,
  });

  assert.deepEqual(await controller.execute(), { kind: "failed", failure });
});
