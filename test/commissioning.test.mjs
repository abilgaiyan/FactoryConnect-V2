import assert from "node:assert/strict";
import { register } from "node:module";
import test from "node:test";
register("./tsx-test-loader.mjs", import.meta.url);
const { admissionComparison, isCommissioningSnapshot } = await import("../src/presentation/CommissioningPage.tsx");

const machineId = "22222222-2222-2222-2222-222222222222";
const fields = [
  { path: "ProductionProcessing:Machines:1:MachineId", value: machineId },
  { path: "ProductionProcessing:Machines:1:ActivityStreamKey", value: "moxa" },
  { path: "ProductionProcessing:Machines:1:ProductionStartsAt", value: "2026-10-25T07:30:00+05:30" },
];
const row = { rowId: 2, machineId, processorId: `production-context:${machineId}`, streamKey: "moxa",
  position: null, admissionBound: true, productionStartsAt: "2026-10-25T02:00:00Z" };
test("admission compares instants only for exact machine, activity processor and stream", () => {
  assert.equal(admissionComparison(fields, row), "Match: boundary instant");
  assert.match(admissionComparison(fields, { ...row, streamKey: "gg003" }), /^Unknown/);
  assert.match(admissionComparison(fields, { ...row, processorId: `production-quantity:${machineId}` }), /^Unknown/);
  assert.match(admissionComparison(fields, { ...row, machineId: "different" }), /^Unknown/);
  assert.match(admissionComparison(fields, { ...row, productionStartsAt: null }), /^Mismatch/);
  assert.equal(admissionComparison(fields, { ...row, admissionBound: false }), "Not bound");
});
test("missing config and duplicate identity never imply applied or matching", () => {
  assert.match(admissionComparison([], row), /^Unknown/);
  assert.match(admissionComparison([...fields, fields[0]], row), /^Unknown/);
});
test("null checkpoint is preserved by contract and missing checkpoint rejected", () => {
  const snapshot = { readStartedAt: "2026-10-28T00:00:00Z", configuration: { status: "unavailable", authority: "capture", fileModifiedAt: null, sha256: null, fields: [] },
    durable: { status: "read", authority: "heads", readCompletedAt: "2026-10-28T00:00:01Z", processors: [row] },
    runtimeActivation: "unknown/unverified", transportHealth: "unknown", checkpointFreshness: "unknown" };
  assert.equal(isCommissioningSnapshot(snapshot), true);
  assert.equal(snapshot.durable.processors[0].position, null);
  assert.equal(isCommissioningSnapshot({ ...snapshot, durable: { ...snapshot.durable, processors: [{ ...row, position: undefined }] } }), false);
  assert.equal(isCommissioningSnapshot({ ...snapshot, durable: null }), false);
});
