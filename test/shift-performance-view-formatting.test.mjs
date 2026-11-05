import assert from "node:assert/strict";
import test from "node:test";

import { formatPresentedMetric } from "../src/presentation/shift-performance-view-formatting.ts";

function identity(metricKey) {
  return { metricKey, version: "1.0" };
}

test("calculated ratios preserve decimal precision while formatting as percentages", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("oee"), state: "calculated", value: "0.3700", unit: "Ratio" }),
    { primary: "37%", evidence: null },
  );
  assert.deepEqual(
    formatPresentedMetric({ ...identity("availability"), state: "calculated", value: "0.8000", unit: "Ratio" }),
    { primary: "80%", evidence: null },
  );
});

test("calculated ratio zero remains zero percent", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("quality"), state: "calculated", value: "0.0000", unit: "Ratio" }),
    { primary: "0%", evidence: null },
  );
});

test("calculated ratio exponent strings use precision-preserving percentage formatting", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("performance"), state: "calculated", value: "3.7e-1", unit: "Ratio" }),
    { primary: "37%", evidence: null },
  );
});

test("calculated non-ratio values remain authoritative text plus unit", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("availability"), state: "calculated", value: "12.3400", unit: "Seconds" }),
    { primary: "12.3400 Seconds", evidence: null },
  );
});

test("unavailable state exposes reason evidence", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("utilization.elr"), state: "unavailable", reasonCode: "missing-power-on", reasonOperandName: "PowerOn" }),
    { primary: "Unavailable", evidence: "missing-power-on / PowerOn" },
  );
});

test("insufficient-evidence state remains distinct and exposes reason evidence", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("performance"), state: "insufficient-evidence", reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" }),
    { primary: "Insufficient evidence", evidence: "missing-reference-time / ReferenceTime" },
  );
});

test("missing state renders only the compact presentation marker", () => {
  assert.deepEqual(
    formatPresentedMetric({ ...identity("quality"), state: "missing" }),
    { primary: "—", evidence: null },
  );
});

test("the inconsistent OEE fixture is formatted independently rather than recomputed", () => {
  const availability = formatPresentedMetric({ ...identity("availability"), state: "calculated", value: "0.80", unit: "Ratio" });
  const performance = formatPresentedMetric({ ...identity("performance"), state: "calculated", value: "0.50", unit: "Ratio" });
  const quality = formatPresentedMetric({ ...identity("quality"), state: "calculated", value: "0.90", unit: "Ratio" });
  const oee = formatPresentedMetric({ ...identity("oee"), state: "calculated", value: "0.37", unit: "Ratio" });

  assert.equal(availability.primary, "80%");
  assert.equal(performance.primary, "50%");
  assert.equal(quality.primary, "90%");
  assert.equal(oee.primary, "37%");
});
