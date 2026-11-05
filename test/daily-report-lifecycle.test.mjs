import assert from "node:assert/strict";
import test from "node:test";

import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingNetworkFailure,
} from "../src/api/reporting/index.ts";
import { createDailyReportLifecycleController } from "../src/application/daily-report-lifecycle.ts";

const day = "2026-09-09";
const nextDay = "2026-09-10";

function source(index = 1) {
  return {
    machineId: `M${index}`,
    processorId: `P${index}`,
    siteId: "site-a",
    productionLineId: "line-a",
    displayName: `Machine ${index}`,
    groupName: "Line A",
    displayOrder: index,
  };
}

function page(items = [], continuationToken = null) {
  return { items, continuationToken };
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

function problemDetails() {
  return {
    type: "about:blank",
    title: "Roster coverage required",
    status: 409,
    detail: "Coverage is required.",
    instance: null,
    extensions: {},
  };
}

test("invalid selection fails before either reporting read and cannot print", async () => {
  let calls = 0;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        calls += 1;
        return page();
      },
      async queryProductionDayShiftMetrics() {
        calls += 1;
        return page();
      },
    },
  });

  const state = await controller.execute("2026-02-30", [source()]);

  assert.deepEqual(state, {
    kind: "invalid-selection",
    productionDay: "2026-02-30",
    canPrint: false,
  });
  assert.equal(calls, 0);
});

test("one generation drives both existing continuation traversals to exhaustion before publishing", async () => {
  const productionDayTokens = [];
  const shiftTokens = [];
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics(request) {
        productionDayTokens.push(request.continuationToken);
        return request.continuationToken === null
          ? page([], "pd-next")
          : page();
      },
      async queryProductionDayShiftMetrics(request) {
        shiftTokens.push(request.continuationToken);
        return request.continuationToken === null
          ? page([], "shift-next")
          : page();
      },
    },
  });

  const state = await controller.execute(day, [source()]);

  assert.equal(state.kind, "success");
  assert.equal(state.canPrint, true);
  assert.deepEqual(productionDayTokens, [null, "pd-next"]);
  assert.deepEqual(shiftTokens, [null, "shift-next"]);
  assert.equal(state.model.productionDay, day);
  assert.equal(state.model.groups[0].machines[0].productionDayCells.length, 5);
  assert.deepEqual(state.model.groups[0].machines[0].shifts, []);
});

test("zero configured sources publishes a successful empty report without reporting requests", async () => {
  let calls = 0;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        calls += 1;
        return page();
      },
      async queryProductionDayShiftMetrics() {
        calls += 1;
        return page();
      },
    },
  });

  const state = await controller.execute(day, []);

  assert.equal(state.kind, "success");
  assert.equal(state.canPrint, true);
  assert.deepEqual(state.model, { productionDay: day, groups: [] });
  assert.equal(calls, 0);
});

test("same-day refresh keeps the previous model visible but stale and non-printable until the new generation succeeds", async () => {
  const productionDayPending = deferred();
  const shiftPending = deferred();
  let pending = false;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        return pending ? productionDayPending.promise : page();
      },
      async queryProductionDayShiftMetrics() {
        return pending ? shiftPending.promise : page();
      },
    },
  });

  const initial = await controller.execute(day, [source()]);
  assert.equal(initial.kind, "success");
  const previous = initial.model;

  pending = true;
  const refresh = controller.execute(day, [source()]);
  const refreshing = controller.current();
  assert.equal(refreshing.kind, "refreshing");
  assert.equal(refreshing.previous, previous);
  assert.equal(refreshing.canPrint, false);

  productionDayPending.resolve(page());
  shiftPending.resolve(page());
  const completed = await refresh;

  assert.equal(completed.kind, "success");
  assert.equal(completed.canPrint, true);
});

test("same-day reporting failure may retain the previous model visibly but never restores print authority", async () => {
  let fail = false;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        if (fail) {
          throw new ReportingNetworkFailure(new Error("offline"));
        }
        return page();
      },
      async queryProductionDayShiftMetrics() {
        return page();
      },
    },
  });

  const initial = await controller.execute(day, [source()]);
  assert.equal(initial.kind, "success");

  fail = true;
  const failed = await controller.execute(day, [source()]);

  assert.equal(failed.kind, "reporting-failure");
  assert.equal(failed.previous, initial.model);
  assert.equal(failed.canPrint, false);
});

test("changing production day removes the prior selection model from the new generation immediately", async () => {
  const productionDayPending = deferred();
  const shiftPending = deferred();
  let pendingDay = null;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics(request) {
        return request.fromInclusive === pendingDay ? productionDayPending.promise : page();
      },
      async queryProductionDayShiftMetrics(request) {
        return request.sources[0]?.businessDate === pendingDay ? shiftPending.promise : page();
      },
    },
  });

  const initial = await controller.execute(day, [source()]);
  assert.equal(initial.kind, "success");

  pendingDay = nextDay;
  const changed = controller.execute(nextDay, [source()]);
  assert.deepEqual(controller.current(), {
    kind: "loading",
    productionDay: nextDay,
    canPrint: false,
  });

  productionDayPending.resolve(page());
  shiftPending.resolve(page());
  const completed = await changed;
  assert.equal(completed.kind, "success");
  assert.equal(completed.productionDay, nextDay);
});

test("a superseded generation cannot publish after its successor succeeds", async () => {
  const firstProductionDay = deferred();
  const firstShift = deferred();
  let firstGeneration = true;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        return firstGeneration ? firstProductionDay.promise : page();
      },
      async queryProductionDayShiftMetrics() {
        return firstGeneration ? firstShift.promise : page();
      },
    },
  });

  const first = controller.execute(day, [source()]);
  assert.equal(controller.current().kind, "loading");

  firstGeneration = false;
  const second = await controller.execute(day, [source()]);
  assert.equal(second.kind, "success");
  const successorModel = second.model;

  firstProductionDay.resolve(page());
  firstShift.resolve(page());
  const supersededResult = await first;

  assert.equal(supersededResult.kind, "success");
  assert.equal(controller.current().kind, "success");
  assert.equal(controller.current().model, successorModel);
});

test("roster coverage failure is classified as a prerequisite failure and publishes no report", async () => {
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        return page();
      },
      async queryProductionDayShiftMetrics() {
        throw new ProductionDayShiftRosterCoverageRequiredFailure(problemDetails(), {
          machineId: "M1",
          siteId: "site-a",
          businessDate: day,
        });
      },
    },
  });

  const state = await controller.execute(day, [source()]);

  assert.equal(state.kind, "roster-prerequisite-failure");
  assert.deepEqual(state.details, {
    machineId: "M1",
    siteId: "site-a",
    businessDate: day,
  });
  assert.equal(state.previous, undefined);
  assert.equal(state.canPrint, false);
});

test("same-day presentation-contract failure retains only the stale previous model and cannot print", async () => {
  const configured = source();
  let violatePresentationContract = false;
  const controller = createDailyReportLifecycleController({
    reportingClient: {
      async queryProductionDayMetrics() {
        if (!violatePresentationContract) {
          return page();
        }

        return page([{
          scope: "production-day",
          processorId: "unexpected-processor",
          machineId: "unexpected-machine",
          shift: null,
          productionDay: { siteId: configured.siteId, businessDate: day },
          context: {
            productionOrderId: null,
            operationId: null,
            partId: null,
            operatorId: null,
          },
          metricKey: "availability",
          definitionVersion: "1.0",
          status: "calculated",
          value: "0.80",
          unit: "Ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: null,
        }]);
      },
      async queryProductionDayShiftMetrics() {
        return page();
      },
    },
  });

  const initial = await controller.execute(day, [configured]);
  assert.equal(initial.kind, "success");
  assert.equal(initial.canPrint, true);

  violatePresentationContract = true;
  const failed = await controller.execute(day, [configured]);

  assert.equal(failed.kind, "presentation-contract-failure");
  assert.equal(failed.previous, initial.model);
  assert.equal(failed.canPrint, false);
  assert.equal("model" in failed, false);
  assert.equal(controller.current(), failed);
});
