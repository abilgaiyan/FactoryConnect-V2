import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { MachineActivityReportPage } = await vite.ssrLoadModule('/src/presentation/MachineActivityReportPage.tsx');
after(() => vite.close());
const machineId = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
function fixture(range) {
  const states = ['unknown','stopped','idle','running','fault','offline'];
  return { machineId, stateProcessorId: 'machine-state-activity', streamKey: 'activity', throughPosition: '10', ...range,
    readAsOf: '2026-10-11T00:00:00Z', unrepresentedTicks: 858000000000,
    totals: states.map(state => ({state, durationTicks: state === 'running' ? 6000000000 : 0})),
    intervals: [{position: '1', state: 'running', startedAtUtc: '2026-10-06T01:00:00Z', endedAtUtc: '2026-10-06T01:10:00Z',
      sourceStartedAtUtc: '2026-10-06T01:00:00Z', sourceEndedAtUtc: '2026-10-06T01:10:00Z', durationTicks: 6000000000, instanceId: '1', sequence: '2'}] };
}
test('machine activity selection renders a timeline and exact accessible interval details; failure returns no report', async t => {
  const original = globalThis.fetch; t.after(() => {globalThis.fetch = original;});
  let failed = false; let sent;
  globalThis.fetch = async (_, options) => {sent = JSON.parse(options.body); return failed ? new Response('', {status: 503}) : Response.json(fixture(sent));};
  const h = await mountInDom(React.createElement(MachineActivityReportPage, {machineId, timeoutMilliseconds: 1000}));
  t.after(() => h.dispose());
  const inputs = h.document.querySelectorAll('input[type=date]');
  assert.match(inputs[0].value, /^\d{4}-\d{2}-\d{2}$/);
  assert.equal(inputs[0].value, inputs[1].value);
  assert.equal(h.document.querySelector('svg'), null);
  await h.changeInput(inputs[0], '2026-10-06'); await h.changeInput(inputs[1], '2026-10-06'); await h.submit(inputs[0].form);
  assert.equal(sent.fromInclusive, '2026-10-05T18:30:00.000Z');
  assert.ok(h.document.querySelector('svg'));
  assert.equal(h.document.querySelectorAll('tbody tr').length, 1);
  assert.equal(h.document.querySelector('[aria-label="Exact stored activity intervals"]').tabIndex, 0);
  assert.match(h.document.body.textContent, /06\/10\/2026 06:30:00 IST/);
  assert.match(h.document.body.textContent, /6000000000 ticks/);
  assert.match(h.document.body.textContent, /No stored interval/);
  failed = true; await h.submit(inputs[0].form);
  assert.match(h.document.querySelector('[role=alert]').textContent, /503/);
  assert.equal(h.document.querySelector('svg'), null); assert.equal(h.document.querySelector('table'), null);
});

function manyIntervals(range, count = 121) {
  const result = fixture(range);
  result.throughPosition = String(Math.max(1, count));
  result.intervals = Array.from({length: count}, (_, index) => {
    const startedAtUtc = new Date(Date.parse('2026-10-06T01:00:00Z') + index * 1000).toISOString();
    const endedAtUtc = new Date(Date.parse(startedAtUtc) + 1000).toISOString();
    return {position: String(index + 1), state: 'running', startedAtUtc, endedAtUtc,
      sourceStartedAtUtc: startedAtUtc, sourceEndedAtUtc: endedAtUtc, durationTicks: 10000000, instanceId: '1', sequence: String(index + 1)};
  });
  result.totals = result.totals.map(t => ({...t, durationTicks: t.state === 'running' ? count * 10000000 : 0}));
  result.unrepresentedTicks = 864000000000 - count * 10000000;
  return result;
}
test('table pagination keeps full-range totals and timeline, supports every size and resets on a successful load', async t => {
  const original = globalThis.fetch; t.after(() => {globalThis.fetch = original;});
  let calls = 0; let count = 121;
  globalThis.fetch = async (_, options) => {calls++; return Response.json(manyIntervals(JSON.parse(options.body), count));};
  const h = await mountInDom(React.createElement(MachineActivityReportPage, {machineId, timeoutMilliseconds: 1000}));
  t.after(() => h.dispose());
  const inputs = h.document.querySelectorAll('input[type=date]');
  await h.changeInput(inputs[0], '2026-10-06'); await h.changeInput(inputs[1], '2026-10-06'); await h.submit(inputs[0].form);
  const pager = () => h.document.querySelector('.activity-pagination [role=status]').textContent;
  const button = text => [...h.document.querySelectorAll('.activity-pagination button')].find(b => b.textContent === text);
  const summaries = h.document.querySelector('.activity-totals').textContent;
  const timeline = h.document.querySelector('svg').innerHTML;
  assert.equal(h.document.querySelectorAll('tbody tr').length, 50);
  assert.match(pager(), /Rows 1–50 of 121 · Page 1 of 3/);
  assert.equal(button('Previous').disabled, true);
  await h.click(button('Next')); assert.match(pager(), /Rows 51–100 of 121/);
  await h.click(button('Next')); assert.equal(h.document.querySelectorAll('tbody tr').length, 21);
  assert.match(pager(), /Rows 101–121 of 121 · Page 3 of 3/);
  assert.equal(button('Next').disabled, true);
  await h.click(button('Previous')); assert.match(pager(), /Page 2 of 3/);
  assert.equal(h.document.querySelector('.activity-totals').textContent, summaries);
  assert.equal(h.document.querySelector('svg').innerHTML, timeline);
  assert.equal(calls, 1);
  const select = h.document.querySelector('.activity-pagination select');
  for (const size of [25, 100, 50]) {
    await React.act(async () => { select.value = String(size); select.dispatchEvent(new h.window.Event('change', {bubbles: true})); });
    assert.equal(h.document.querySelectorAll('tbody tr').length, size);
    assert.match(pager(), /Page 1 of/);
  }
  await h.click(button('Next')); await h.submit(inputs[0].form);
  assert.match(pager(), /Rows 1–50 of 121 · Page 1 of 3/);
  assert.equal(calls, 2);
  count = 0; await h.submit(inputs[0].form);
  assert.match(pager(), /Rows 0–0 of 0 · Page 1 of 1/);
  assert.equal(button('Previous').disabled, true); assert.equal(button('Next').disabled, true);
  assert.equal(h.document.querySelectorAll('tbody tr').length, 0);
});
