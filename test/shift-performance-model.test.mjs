import assert from "node:assert/strict";
import test from "node:test";

import {
  ShiftPresentationContractFailure,
} from "../src/presentation/shift-performance-model.ts";

test("presentation contract failure retains its typed reason", () => {
  const failure = new ShiftPresentationContractFailure(
    "unexpected-production-line",
    "Authoritative production line does not match configured source.",
  );

  assert.equal(failure.name, "ShiftPresentationContractFailure");
  assert.equal(failure.reason, "unexpected-production-line");
  assert.equal(
    failure.message,
    "Authoritative production line does not match configured source.",
  );
});
