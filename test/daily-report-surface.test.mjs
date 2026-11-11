import assert from "node:assert/strict";
import test from "node:test";

import { dailyReportPath } from "../src/application/daily-report-navigation.ts";
import {
  canPrintDailyReportState,
  visibleDailyReportModel,
  filterDailyReportModel,
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

test("Daily Report filters retain authoritative cells and do not mutate source", () => {
  const source = model();
  const first = source.groups[0].machines[0];
  const second = { ...first, machineId: "M2", displayName: "Machine 2", productionLineId: "line-b" };
  const input = { ...source, groups: [{ ...source.groups[0], machines: [first, second] }] };
  const line = filterDailyReportModel(input, "line-b", "");
  assert.deepEqual(line.groups[0].machines.map(machine => machine.machineId), ["M2"]);
  assert.deepEqual(filterDailyReportModel(input, "", "M1").groups[0].machines, [first]);
  assert.deepEqual(filterDailyReportModel(input, "line-b", "M1").groups, []);
  assert.equal(filterDailyReportModel(input, "", ""), input);
  assert.equal(input.groups[0].machines.length, 2);
});

test("shift selection preserves original shift evidence and does not show other occurrences", () => {
  const source = model();
  const machine = source.groups[0].machines[0];
  const morning = { shift: { shiftId: "Morning" }, cells: [{ state: "unavailable", reasonCode: "missing-counts" }] };
  const evening = { shift: { shiftId: "Evening" }, cells: [{ state: "calculated", value: "0.75" }] };
  const input = { ...source, groups: [{ ...source.groups[0], machines: [{ ...machine, shifts: [morning, evening] }] }] };
  const result = filterDailyReportModel(input, "", "", "Evening");
  assert.deepEqual(result.groups[0].machines[0].shifts, [evening]);
  assert.equal(input.groups[0].machines[0].shifts.length, 2);
  assert.deepEqual(filterDailyReportModel(input, "", "", "Night").groups, []);
});
