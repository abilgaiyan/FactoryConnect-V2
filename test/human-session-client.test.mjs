import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const vite = await createServer({ root: fileURLToPath(new URL("../", import.meta.url)), server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { issueHumanSession, HumanSessionFailure } = await vite.ssrLoadModule("/src/api/security/human-session-client.ts");
after(() => vite.close());

test("refuses plaintext, malformed assertions and never invokes transport", async () => {
  const noNetwork = () => { throw new Error("unexpected network call"); };
  await assert.rejects(issueHumanSession("http://localhost/", "proof", undefined, noNetwork), /HTTPS/);
  await assert.rejects(issueHumanSession("https://localhost/", "", undefined, noNetwork), /length/);
});
test("passes opaque one-use assertion without using URL or persistent storage", async () => {
  let request;
  const fetchStub = async (url, options) => {
    request = { url: String(url), options };
    return Response.json({ bearer: Buffer.alloc(32, 7).toString("base64"),
      principalId: "11111111-1111-1111-1111-111111111111", expiresAt: "2026-11-07T10:00:00Z" });
  };
  const issued = await issueHumanSession("https://localhost/", "opaque-proof", undefined, fetchStub);
  assert.equal(request.url, "https://localhost/api/security/v2/human-sessions");
  assert.equal(request.options.body, "opaque-proof");
  assert.equal(request.options.cache, "no-store");
  assert.equal(request.options.redirect, "error");
  assert.equal(issued.bearer, Buffer.alloc(32, 7).toString("base64"));
});
test("missing verifier returns unavailable, never an issued session", async () => {
  await assert.rejects(
    issueHumanSession("https://localhost/", "opaque-proof", undefined, async () => new Response("", { status: 503 })),
    error => error instanceof HumanSessionFailure && error.status === 503,
  );
});
test("rejects malformed success responses", async () => {
  await assert.rejects(
    issueHumanSession("https://localhost/", "opaque-proof", undefined, async () => Response.json({ bearer: "not-a-token", principalId: "x", expiresAt: "x" })),
    error => error instanceof HumanSessionFailure && error.status === 502,
  );
});
