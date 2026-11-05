import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const clientRootUrl = new URL("../", import.meta.url);

function importsOf(sourceText) {
  return [...sourceText.matchAll(/(?:import|export)\s+(?:type\s+)?(?:[\s\S]*?\s+from\s+)?["']([^"']+)["'];/g)]
    .map(match => match[1]);
}

async function sourceImports(relativePath) {
  const content = await readFile(new URL(relativePath, clientRootUrl), "utf8");
  return importsOf(content);
}

test("Daily Report production modules keep the exact approved dependency direction", async () => {
  assert.deepEqual(
    await sourceImports("src/application/daily-report-lifecycle.ts"),
    [
      "../api/reporting/index.ts",
      "./production-day-presentation.ts",
      "./production-day-reporting.ts",
      "./production-day-shift-reporting.ts",
      "./runtime-configuration.ts",
      "../presentation/daily-report-composer.ts",
      "../presentation/daily-report-model.ts",
      "../presentation/shift-performance-model.ts",
    ],
  );

  assert.deepEqual(
    await sourceImports("src/application/use-daily-report.ts"),
    [
      "react",
      "./application-runtime.ts",
      "./daily-report-lifecycle.ts",
    ],
  );

  assert.deepEqual(
    await sourceImports("src/presentation/daily-report-composer.ts"),
    [
      "../application/production-day-presentation.ts",
      "./shift-performance-projector.ts",
      "./shift-performance-model.ts",
      "./daily-report-model.ts",
    ],
  );

  assert.deepEqual(
    await sourceImports("src/presentation/daily-report-page-policy.ts"),
    [
      "../application/daily-report-lifecycle.ts",
      "./daily-report-model.ts",
    ],
  );

  assert.deepEqual(
    await sourceImports("src/presentation/DailyReportPage.tsx"),
    [
      "./date-formatting.ts",
      "../application/daily-report-lifecycle.ts",
      "./daily-report-model.ts",
      "./daily-report-page-policy.ts",
    ],
  );

  assert.deepEqual(
    await sourceImports("src/application/daily-report-navigation.ts"),
    ["../routing/application-route.ts"],
  );
});
