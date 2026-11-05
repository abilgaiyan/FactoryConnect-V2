import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { deriveProductionDayOverviewViewState } from "../src/application/production-day-overview-state.ts";
import { ProductionDayOverviewSurface } from "../src/application/ProductionDayOverviewSurface.ts";
import { queryAuthoritativeProductionDay } from "../src/application/production-day-reporting.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

const productionDay = "2026-08-31";
const configuredSource = {
  machineId: "11111111-1111-1111-1111-111111111111",
  processorId: "operational-metrics",
  displayName: "Machine 1",
  groupName: "Line 1",
  displayOrder: 0,
};

function calculatedOee(value = "0.37") {
  return {
    scope: "production-day",
    processorId: configuredSource.processorId,
    machineId: configuredSource.machineId,
    context: {
      productionOrderId: null,
      operationId: null,
      partId: null,
      operatorId: null,
    },
    metricKey: "oee",
    definitionVersion: "1.0",
    status: "calculated",
    value,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: configuredSource.processorId,
      machineId: configuredSource.machineId,
      streamKey: "stream:machine-1",
      position: "1",
    },
    shift: null,
    productionDay: {
      siteId: "factory",
      businessDate: productionDay,
    },
  };
}

function renderOverview(state) {
  return renderToStaticMarkup(React.createElement(ProductionDayOverviewSurface, {
    productionDay,
    overview: {
      state,
      lastSuccessfulRetrieval: null,
      async refresh() {},
    },
    onProductionDayChange() {},
  }));
}

async function executeThroughLifecycle(reportingClient) {
  const controller = createQueryLifecycleController({
    query: (signal) => queryAuthoritativeProductionDay(
      productionDay,
      [configuredSource],
      reportingClient,
      { signal },
    ),
    isEmpty: (result) => result.items.length === 0,
  });

  try {
    const queryState = await controller.execute();
    const overviewState = deriveProductionDayOverviewViewState(
      queryState,
      productionDay,
      [configuredSource],
    );
    return { queryState, overviewState, html: renderOverview(overviewState) };
  } finally {
    controller.dispose();
  }
}

function assertContainedReportingFailure(outcome) {
  assert.equal(outcome.queryState.kind, "failed");
  assert.equal(outcome.overviewState.kind, "reporting-failed");
  assert.match(outcome.html, /role="alert"/);
  assert.match(outcome.html, /Production-day reporting is unavailable/);
  assert.doesNotMatch(outcome.html, /37%/);
  assert.doesNotMatch(outcome.html, /Machine 1<\/th>/);
}

test("later-page network failure is contained through lifecycle overview state and visible alert without partial results", async () => {
  let requestCount = 0;
  const outcome = await executeThroughLifecycle({
    async queryProductionDayMetrics() {
      requestCount++;
      if (requestCount === 1) {
        return {
          items: [calculatedOee()],
          continuationToken: "next-page",
        };
      }

      throw new ReportingNetworkFailure(new Error("offline"));
    },
  });

  assert.equal(requestCount, 2);
  assertContainedReportingFailure(outcome);
});

test("continuation cycle is contained through lifecycle overview state and visible alert", async () => {
  let requestCount = 0;
  const outcome = await executeThroughLifecycle({
    async queryProductionDayMetrics() {
      requestCount++;
      return {
        items: requestCount === 1 ? [calculatedOee()] : [],
        continuationToken: "cycle-token",
      };
    },
  });

  assert.equal(requestCount, 2);
  assertContainedReportingFailure(outcome);
});

test("page-limit protocol failure is contained through lifecycle overview state and visible alert", async () => {
  let requestCount = 0;
  const outcome = await executeThroughLifecycle({
    async queryProductionDayMetrics() {
      requestCount++;
      return {
        items: requestCount === 1 ? [calculatedOee()] : [],
        continuationToken: `opaque-${requestCount}`,
      };
    },
  });

  assert.equal(requestCount, 100);
  assertContainedReportingFailure(outcome);
});

test("production-day overview modules use only the exact approved dependency set", () => {
  const modules = [
    "src/application/production-day-reporting.ts",
    "src/application/production-day-presentation.ts",
    "src/application/production-day-overview-state.ts",
    "src/application/use-production-day-reporting.ts",
    "src/application/use-production-day-overview.ts",
    "src/application/production-day-metric-formatting.ts",
    "src/application/production-day-navigation.ts",
    "src/application/ProductionDayOverviewSurface.ts",
    "src/application/ProductionDayOverviewMatrix.ts",
  ];

  const allowedDependencies = new Set([
    "../presentation/date-formatting.ts",
    "react",
    "../api/reporting/index.ts",
    "../query/query-state.ts",
    "../query/query-lifecycle-controller.ts",
    "../query/use-query-lifecycle-controller.ts",
    "./application-runtime.ts",
    "./runtime-configuration.ts",
    "./production-day-reporting.ts",
    "./production-day-presentation.ts",
    "./production-day-overview-state.ts",
    "./use-production-day-reporting.ts",
    "./use-production-day-overview.ts",
    "./production-day-metric-formatting.ts",
    "./ProductionDayOverviewMatrix.ts",
  ]);

  for (const modulePath of modules) {
    const source = fs.readFileSync(new URL(`../${modulePath}`, import.meta.url), "utf8");
    const imports = [...source.matchAll(/from\s+["']([^"']+)["']/g)].map((match) => match[1]);

    for (const dependency of imports) {
      assert.ok(
        allowedDependencies.has(dependency),
        `${modulePath} imports disallowed production-day overview dependency ${dependency}`,
      );
    }
  }
});
