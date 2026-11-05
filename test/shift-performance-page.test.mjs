import assert from "node:assert/strict";
import { after, test } from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createServer } from "vite";

const vite = await createServer({
  appType: "custom",
  logLevel: "silent",
  server: { middlewareMode: true },
});
const {
  ShiftPerformancePage,
  isShiftPerformanceRefreshDisabled,
  invokeShiftPerformanceRefresh,
} = await vite.ssrLoadModule("/src/presentation/ShiftPerformancePage.tsx");

after(async () => {
  await vite.close();
});

const day = "2026-09-03";

function overview() {
  return { productionDay: day, groups: [] };
}

function render(state) {
  return renderToStaticMarkup(createElement(ShiftPerformancePage, {
    state,
    refresh: async () => {},
  }));
}

test("refresh availability is derived only from classified page state", () => {
  assert.equal(isShiftPerformanceRefreshDisabled({ kind: "loading", productionDay: day }), true);
  assert.equal(isShiftPerformanceRefreshDisabled({ kind: "success", productionDay: day, overview: overview(), isRefreshing: false }), false);
  assert.equal(isShiftPerformanceRefreshDisabled({ kind: "success", productionDay: day, overview: overview(), isRefreshing: true }), true);
  assert.equal(isShiftPerformanceRefreshDisabled({ kind: "invalid-request", productionDay: day, message: "invalid" }), false);
  assert.equal(isShiftPerformanceRefreshDisabled({
    kind: "roster-coverage-required",
    productionDay: day,
    machineId: "M1",
    siteId: "site-a",
    businessDate: day,
  }), false);
  assert.equal(isShiftPerformanceRefreshDisabled({ kind: "transport-failure", productionDay: day, message: "offline" }), false);
  assert.equal(isShiftPerformanceRefreshDisabled({
    kind: "presentation-contract-failure",
    productionDay: day,
    message: "invalid presentation",
    isRefreshing: false,
  }), false);
  assert.equal(isShiftPerformanceRefreshDisabled({
    kind: "presentation-contract-failure",
    productionDay: day,
    message: "invalid presentation",
    isRefreshing: true,
  }), true);
});

test("rendered interaction disables loading and active refresh generations", () => {
  assert.match(render({ kind: "loading", productionDay: day }), /<button type="button" disabled="">Refresh<\/button>/);
  assert.match(render({ kind: "success", productionDay: day, overview: overview(), isRefreshing: false }), /<button type="button">Refresh<\/button>/);
  assert.match(render({ kind: "success", productionDay: day, overview: overview(), isRefreshing: true }), /<button type="button" disabled="">Refresh<\/button>/);
  assert.match(render({
    kind: "presentation-contract-failure",
    productionDay: day,
    message: "invalid presentation",
    isRefreshing: true,
  }), /<button type="button" disabled="">Refresh<\/button>/);
});

test("presentation failure refresh remains visible without exposing malformed authority", () => {
  const html = render({
    kind: "presentation-contract-failure",
    productionDay: day,
    message: "classified presentation failure",
    isRefreshing: true,
  });
  assert.match(html, /role="alert"/);
  assert.match(html, /classified presentation failure/);
  assert.match(html, /role="status"/);
  assert.match(html, /Refreshing shift performance/);
  assert.doesNotMatch(html, /Machine 1/);
});

test("refresh invocation delegates exactly once and preserves rejection identity", async () => {
  let calls = 0;
  const defect = new Error("programming defect");
  const refresh = async () => {
    calls += 1;
    throw defect;
  };

  await assert.rejects(
    invokeShiftPerformanceRefresh(refresh),
    error => error === defect,
  );
  assert.equal(calls, 1);
});
