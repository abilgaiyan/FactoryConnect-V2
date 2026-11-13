import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const vite = await createServer({
  root: fileURLToPath(new URL("../", import.meta.url)),
  server: { middlewareMode: true }, appType: "custom", logLevel: "silent"
});
after(() => vite.close());
const { activationNextStep } = await vite.ssrLoadModule("/src/presentation/MachineAdministrationPage.tsx");

test("missing runtime assignment is not misrepresented as agent connectivity", () => {
  assert.match(activationNextStep("NoRuntimeAssignment"), /runtime identity/i);
  assert.doesNotMatch(activationNextStep("NoRuntimeAssignment"), /connected|active acquisition/i);
});

test("acknowledged activation is not represented as proven connectivity", () => {
  assert.match(activationNextStep("RequestedActivationAcknowledgedNotConnectivity"), /Verify acquisition health/);
});

test("revoked or expired authority does not suggest bypassing activation", () => {
  assert.match(activationNextStep("RequestOrApprovalRevoked"), /Review revoked/);
  assert.match(activationNextStep("RequestExpired"), /current activation request/);
});

test("unknown readiness reason is treated as unverified", () => {
  assert.match(activationNextStep(undefined), /Refresh activation readiness/);
  assert.match(activationNextStep("UnexpectedAuthorityReason"), /Refresh activation readiness/);
});
