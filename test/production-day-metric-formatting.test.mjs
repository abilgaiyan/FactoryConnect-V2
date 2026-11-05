import assert from "node:assert/strict";
import test from "node:test";

import { formatRatioAsPercentage } from "../src/application/production-day-metric-formatting.ts";

test("ordinary positive exponent formats as a percentage without numeric conversion", () => {
  assert.equal(formatRatioAsPercentage("1e2"), "10000");
});

test("ordinary negative exponent expands within the bounded threshold", () => {
  assert.equal(formatRatioAsPercentage("1e-3"), "0.1");
});

test("extremely negative exponent remains bounded scientific notation", () => {
  assert.equal(formatRatioAsPercentage("1e-999999999"), "1e-999999997");
});

test("exponent beyond safe JavaScript integer range is adjusted exactly", () => {
  assert.equal(
    formatRatioAsPercentage("1e-9007199254740993123456789"),
    "1e-9007199254740993123456787",
  );
});

test("zero with an extreme exponent short-circuits without expansion", () => {
  assert.equal(formatRatioAsPercentage("0e-999999999"), "0");
});

test("formatter output size is independent of extreme exponent magnitude", () => {
  const formatted = formatRatioAsPercentage("1e-999999999999999999999999999999999999");
  assert.equal(formatted, "1e-999999999999999999999999999999999997");
  assert.ok(formatted.length < 64);
});
