import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { App } = await vite.ssrLoadModule('/src/App.tsx');
after(() => vite.close());
const source = { machineId: '11111111-1111-1111-1111-111111111111', processorId: 'p1', siteId: 'site', productionLineId: 'line', displayName: 'Machine A', groupName: null, displayOrder: 0 };
const evidence = { machineId: source.machineId, outcome: 'evidence', coverage: 'complete', evidence: { machineState: 'idle', freshness: 'current', usability: 'current', readAsOf: '2026-10-09T00:00:00Z' } };
const item = { scope: 'production-day', processorId: 'p1', machineId: source.machineId, shift: null, productionDay: { siteId: 'site', businessDate: '2026-10-09' }, context: { partId: null, operatorId: null, productionOrderId: null, operationId: null }, metricKey: 'availability', definitionVersion: '1.0', status: 'calculated', value: '0.50', unit: 'ratio', reasonCode: null, reasonOperandName: null, sourceRevision: { processorId: 'aggregation', machineId: source.machineId, streamKey: 'stream', position: '18446744073709551615' } };
const runtime = (sources = [source]) => ({ configuration: { sources }, currentStateClient: { read: async () => evidence }, reportingClient: { queryProductionDayMetrics: async (req) => ({ items: req.fromInclusive === '2026-10-09' ? [item] : [], continuationToken: null }) } });
test('fleet route exposes metadata conflict and keeps read-as-of evidence after failed refresh', async (t) => {
    const r = runtime([source, { ...source, processorId: 'p2', displayName: 'Conflicting name' }]);
    let failed = false;
    r.currentStateClient.read = async () => { if (failed)
        throw new Error('offline'); return evidence; };
    const h = await mountInDom(React.createElement(App, { runtime: r }), 'http://factory-dashboard/fleet');
    t.after(() => h.dispose());
    assert.equal(h.document.querySelectorAll('tbody tr').length, 1);
    assert.match(h.document.body.textContent, /Configuration conflict/);
    assert.match(h.document.body.textContent, /Read as of/);
    assert.match(h.document.body.textContent, /idle/);
    failed = true;
    await h.click([...h.document.querySelectorAll('button')].find(x => x.textContent === 'Refresh fleet'));
    assert.match(h.document.body.textContent, /State unavailable/);
    assert.match(h.document.body.textContent, /Retained evidence/);
    assert.match(h.document.body.textContent, /09\/10\/2026 05:30:00 IST/);
    assert.equal(h.document.querySelector("aside time").getAttribute("datetime"), evidence.evidence.readAsOf);
});
test('range route renders separate sources, exact decimal/revision, missing days, gaps and withheld totals', async (t) => {
    const r = runtime([source, { ...source, processorId: 'p2' }]);
    r.reportingClient.queryProductionDayMetrics = async (req) => ({ items: req.fromInclusive === '2026-10-09' ? [{ ...item, processorId: req.sources[0].processorId }] : [], continuationToken: null });
    const h = await mountInDom(React.createElement(App, { runtime: r }), 'http://factory-dashboard/reports/range');
    t.after(() => h.dispose());
    const inputs = h.document.querySelectorAll('input[type=date]');
    await h.changeInput(inputs[0], '2026-10-09');
    await h.changeInput(inputs[1], '2026-10-11');
    await h.submit(inputs[0].form);
    const text = h.document.body.textContent;
    assert.match(text, /processor p1/);
    assert.match(text, /processor p2/);
    assert.match(text, /0.50/);
    assert.match(text, /18446744073709551615/);
    assert.match(text, /No reporting result/);
    assert.match(text, /Undetermined/);
    assert.match(text, /ratios withheld/);
    assert.equal(h.document.querySelectorAll('circle').length, 2);
    assert.ok([...h.document.querySelectorAll('polyline')].every(x => x.getAttribute('points').split(' ').length === 1));
});
test('failed range renders request failure and no absence rows or graph', async (t) => {
    const r = runtime();
    r.reportingClient.queryProductionDayMetrics = async () => { throw new Error('offline'); };
    const h = await mountInDom(React.createElement(App, { runtime: r }), 'http://factory-dashboard/reports/range');
    t.after(() => h.dispose());
    const inputs = h.document.querySelectorAll('input[type=date]');
    await h.changeInput(inputs[0], '2026-10-09');
    await h.changeInput(inputs[1], '2026-10-09');
    await h.submit(inputs[0].form);
    assert.match(h.document.querySelector('[role=alert]').textContent, /Request failed/);
    assert.equal(h.document.querySelector('table'), null);
    assert.equal(h.document.querySelector('svg'), null);
});

test('mixed daily checkpoints produce an explicit error with no table or trend', async t => {
    const r = runtime();
    r.reportingClient.queryProductionDayMetrics = async () => ({items: [item, {...item, metricKey: 'utilization.elr', sourceRevision: {...item.sourceRevision, position: '2'}}], continuationToken: null});
    const h = await mountInDom(React.createElement(App, {runtime: r}), 'http://factory-dashboard/reports/range');
    t.after(() => h.dispose());
    const inputs = h.document.querySelectorAll('input[type=date]');
    await h.changeInput(inputs[0], '2026-10-09'); await h.changeInput(inputs[1], '2026-10-09'); await h.submit(inputs[0].form);
    assert.match(h.document.querySelector('[role=alert]').textContent, /Mixed aggregation checkpoints/);
    assert.equal(h.document.querySelector('table'), null); assert.equal(h.document.querySelector('svg'), null);
});
test('UI explains selection bounds and rejects oversized range without a request', async t => {
    const r = runtime(); let calls = 0;
    r.reportingClient.queryProductionDayMetrics = async () => { ++calls; return {items: [], continuationToken: null}; };
    const h = await mountInDom(React.createElement(App, {runtime: r}), 'http://factory-dashboard/reports/range');
    t.after(() => h.dispose());
    assert.match(h.document.body.textContent, /366 inclusive days and 1000 reporting-source\/day combinations/);
    const inputs = h.document.querySelectorAll('input[type=date]');
    await h.changeInput(inputs[0], '0001-01-01'); await h.changeInput(inputs[1], '9999-12-30'); await h.submit(inputs[0].form);
    assert.match(h.document.querySelector('[role=alert]').textContent, /at most 366/);
    assert.equal(calls, 0);
});

test('range formatting shows percentages with exact API values and keeps missing metrics in date order', async t => {
    const h = await mountInDom(React.createElement(App, {runtime: runtime()}), 'http://factory-dashboard/reports/range');
    t.after(() => h.dispose());
    const inputs = h.document.querySelectorAll('input[type=date]');
    await h.changeInput(inputs[0], '2026-10-09'); await h.changeInput(inputs[1], '2026-10-10'); await h.submit(inputs[0].form);
    assert.match(h.document.body.textContent, /calculated: 50%/);
    assert.match(h.document.body.textContent, /API value: 0.50 ratio/);
    const dates = [...h.document.querySelectorAll('tbody tr')].map(row => row.cells[0].textContent);
    assert.deepEqual(dates, [...dates].sort());
    assert.equal(h.document.querySelector('[aria-label="Daily reporting results"]').tabIndex, 0);
    assert.equal(h.document.querySelector('svg').getAttribute('viewBox'), '0 0 600 210');
});
