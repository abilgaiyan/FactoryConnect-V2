import assert from "node:assert/strict";
import test from "node:test";

import { dailyReportPath } from "../src/application/daily-report-navigation.ts";
import {
  canPrintDailyReportState,
  visibleDailyReportModel,
} from "../src/presentation/daily-report-page-policy.ts";

const day = "2026-09-09";

function model() {
  return {
    productionDay: day,
    groups: [{
      groupName: "Line A",
      machines: [{
        machineId: "M1",
        processorId: "P1",
        siteId: "site-a",
        productionLineId: "line-a",
        displayName: "Machine 1",
        groupName: "Line A",
        displayOrder: 10,
        productionDayCells: [],
        shifts: [],
      }],
    }],
  };
}

test("canonical Daily Report navigation path remains production-day scoped", () => {
  assert.equal(dailyReportPath(day), "/production-days/2026-09-09/report");
});

test("print is authorized only for the current successful generation", () => {
  const currentModel = model();
  const success = { kind: "success", productionDay: day, model: currentModel, canPrint: true };
  const refreshing = { kind: "refreshing", productionDay: day, previous: currentModel, canPrint: false };
  const failed = {
    kind: "presentation-contract-failure",
    productionDay: day,
    failure: new Error("contract"),
    previous: currentModel,
    canPrint: false,
  };

  assert.equal(canPrintDailyReportState(success), true);
  assert.equal(canPrintDailyReportState(refreshing), false);
  assert.equal(canPrintDailyReportState(failed), false);
});

test("same-day refresh and failure may expose only the stale previous model", () => {
  const previous = model();

  assert.equal(visibleDailyReportModel({
    kind: "refreshing",
    productionDay: day,
    previous,
    canPrint: false,
  }), previous);

  assert.equal(visibleDailyReportModel({
    kind: "reporting-failure",
    productionDay: day,
    failure: new Error("offline"),
    previous,
    canPrint: false,
  }), previous);

  assert.equal(visibleDailyReportModel({
    kind: "presentation-contract-failure",
    productionDay: day,
    failure: new Error("contract"),
    previous,
    canPrint: false,
  }), previous);
});

test("loading, invalid selection, and failures without a previous model expose no report", () => {
  assert.equal(visibleDailyReportModel({
    kind: "loading",
    productionDay: day,
    canPrint: false,
  }), null);

  assert.equal(visibleDailyReportModel({
    kind: "invalid-selection",
    productionDay: "2026-02-30",
    canPrint: false,
  }), null);

  assert.equal(visibleDailyReportModel({
    kind: "presentation-contract-failure",
    productionDay: day,
    failure: new Error("contract"),
    previous: undefined,
    canPrint: false,
  }), null);
});
