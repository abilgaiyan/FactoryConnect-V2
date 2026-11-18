import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({
  root: fileURLToPath(new URL('../', import.meta.url)),
  server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent'
});
const { default: ManagementRegisteredProduction } = await vite.ssrLoadModule('/src/presentation/ManagementRegisteredProduction.tsx');
after(() => vite.close());

const id = '11111111-1111-1111-1111-111111111111';
const siteId = '22222222-2222-2222-2222-222222222222';
const inventory = machines => ({ siteId, source: 'sql-master', isSample: false, machines });
async function withInventory(reply, verify) {
  const originalFetch = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return { ok: reply.status === 200, status: reply.status, json: async () => reply.body };
  };
  let mounted;
  try {
    mounted = await mountInDom(React.createElement(ManagementRegisteredProduction, { businessDate: '2026-11-18' }));
    await verify(mounted, requests);
  } finally {
    if (mounted) await mounted.dispose();
    globalThis.fetch = originalFetch;
  }
}

test('management reads site-scoped SQL inventory with cookie, not admin bearer', async () => {
  await withInventory({ status: 200, body: inventory([{ machineId: id, displayName: 'CNC' }]) }, async (h, requests) => {
    assert.equal(requests.length, 1);
    assert.equal(requests[0].url, '/api/reporting/v2/machines');
    assert.equal(requests[0].options.credentials, 'include');
    assert.equal(requests[0].options.headers.Authorization, undefined);
    assert.match(h.document.body.textContent, /1 active registered machines/);
    assert.match(h.document.body.textContent, /Production plan vs actual/);
  });
});

test('duplicate machine identities are rejected before reporting totals', async () => {
  await withInventory({ status: 200, body: inventory([
    { machineId: id, displayName: 'CNC A' },
    { machineId: id.toUpperCase(), displayName: 'CNC B' }
  ]) }, async (h) => {
    assert.match(h.document.body.textContent, /duplicate machine identities/);
    assert.doesNotMatch(h.document.body.textContent, /Production plan vs actual/);
  });
});

test('invalid evidence contract is rejected without sample fallback', async () => {
  await withInventory({ status: 200, body: { ...inventory([{ machineId: id, displayName: 'CNC' }]), isSample: true } }, async (h) => {
    assert.match(h.document.body.textContent, /response is invalid/);
    assert.doesNotMatch(h.document.body.textContent, /Production plan vs actual/);
  });
});

test('unauthenticated inventory stays withheld', async () => {
  await withInventory({ status: 401 }, async (h) => {
    assert.match(h.document.body.textContent, /Sign in again/);
    assert.doesNotMatch(h.document.body.textContent, /Production plan vs actual/);
  });
});
