import assert from "node:assert/strict";
import test from "node:test";
import {
  createCurrentStateClient,
  CurrentStateAuthorityUnavailableFailure,
} from "../src/api/current-state/current-state-client.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

test("current-state client issues exact GET and decodes evidence", async () => {
  let observed;
  const client = createCurrentStateClient({
    baseAddress: "https://factory.example/",
    timeoutMilliseconds: 30_000,
    fetch: async (url, init) => {
      observed = { url: String(url), init };
      return new Response(JSON.stringify({
        machineId,
        outcome: "evidence",
        coverage: "complete",
        evidence: {
          machineState: "running",
          freshness: "current",
          usability: "current",
          readAsOf: "2026-09-21T12:00:00Z",
        },
      }), { status: 200, headers: { "Content-Type": "application/json" } });
    },
  });

  const result = await client.read(machineId);
  assert.equal(observed.init.method, "GET");
  assert.equal(observed.url, `https://factory.example/api/machines/v1/${machineId}/current-state`);
  assert.equal(result.evidence.machineState, "running");
});

test("no-evidence remains a successful authoritative response", async () => {
  const client = createCurrentStateClient({
    baseAddress: "https://factory.example/",
    timeoutMilliseconds: 30_000,
    fetch: async () => new Response(JSON.stringify({
      machineId,
      outcome: "no-evidence",
      coverage: "indeterminate",
      evidence: null,
    }), { status: 200, headers: { "Content-Type": "application/json" } }),
  });
  const result = await client.read(machineId);
  assert.equal(result.outcome, "no-evidence");
  assert.equal(result.coverage, "indeterminate");
  assert.equal(result.evidence, null);
});

test("503 remains authority-unavailable Problem Details", async () => {
  const client = createCurrentStateClient({
    baseAddress: "https://factory.example/",
    timeoutMilliseconds: 30_000,
    fetch: async () => new Response(JSON.stringify({
      type: "urn:factoryconnect:problem:machines:current-state-authority-failure",
      title: "Current machine-state authority unavailable",
      status: 503,
    }), { status: 503, headers: { "Content-Type": "application/problem+json" } }),
  });
  await assert.rejects(client.read(machineId), CurrentStateAuthorityUnavailableFailure);
});
