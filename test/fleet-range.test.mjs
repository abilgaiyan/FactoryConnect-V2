import test from 'node:test';
import assert from 'node:assert/strict';
import { fleetMachines, fleetCounts, FleetRefresh } from '../src/application/fleet-status.ts';
import { selectedDays, loadRange, trendSegments, plottedValue } from '../src/application/date-range-reporting.ts';
const source = { machineId: '11111111-1111-1111-1111-111111111111', processorId: 'p1', siteId: 'site', productionLineId: 'line', displayName: 'Machine', groupName: null, displayOrder: 0 };
const response = { machineId: source.machineId, outcome: 'evidence', coverage: 'behind', evidence: { machineState: 'running', freshness: 'stale', usability: 'behind', readAsOf: '2026-10-09T00:00:00Z' } };
const item = { scope: 'production-day', processorId: 'p1', machineId: source.machineId, productionDay: { siteId: 'site', businessDate: '2026-10-09' }, shift: null, context: { productionOrderId: null, operationId: null, partId: null, operatorId: null }, metricKey: 'availability', definitionVersion: '1.0', status: 'calculated', value: 0.5, unit: 'ratio', reasonCode: null, reasonOperandName: null, sourceRevision: { processorId: 'a', machineId: source.machineId, streamKey: 'stream', position: 1 } };
const deferred = () => { let resolve; let reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
test('duplicate reporting sources yield one machine and preserve conflicting metadata', () => {
    const machines = fleetMachines([source, { ...source, processorId: 'p2', displayName: 'Other' }]);
    assert.equal(machines.length, 1);
    assert.equal(machines[0].conflict, true);
    assert.equal(machines[0].sources.length, 2);
});
test('each count group reconciles independently; failed retained Running is unavailable for this attempt', () => {
    const machines = fleetMachines([source, { ...source, machineId: '22222222-2222-2222-2222-222222222222' }]);
    const counts = fleetCounts(machines, { [source.machineId]: { request: 'success', current: response }, [machines[1].machineId]: { request: 'failure', retained: response } });
    for (const group of Object.values(counts))
        assert.equal(Object.values(group).reduce((a, b) => a + b, 0), 2);
    assert.equal(counts.state.running, 1);
    assert.equal(counts.state['State unavailable'], 1);
    assert.equal(counts.usability.behind, 1);
    assert.equal(counts.freshness.stale, 1);
});
test('failed refresh preserves original read timestamp and no current evidence', async () => {
    let fail = false;
    let latest;
    const controller = new FleetRefresh(fleetMachines([source]), { read: async () => { if (fail)
            throw new Error('failed'); return response; } }, x => latest = x);
    await controller.refresh();
    fail = true;
    await controller.refresh();
    assert.equal(latest[source.machineId].request, 'failure');
    assert.equal(latest[source.machineId].retained.evidence.readAsOf, response.evidence.readAsOf);
    assert.equal(latest[source.machineId].current, undefined);
});
test('superseded completion cannot replace newer evidence', async () => {
    const first = deferred(), second = deferred();
    let calls = 0, latest;
    const controller = new FleetRefresh(fleetMachines([source]), { read: () => (++calls === 1 ? first : second).promise }, x => latest = x);
    const a = controller.refresh(), b = controller.refresh();
    second.resolve({ ...response, evidence: { ...response.evidence, machineState: 'fault' } });
    await b;
    first.resolve(response);
    await a;
    assert.equal(latest[source.machineId].current.evidence.machineState, 'fault');
    controller.dispose();
});
test('response machine mismatch fails rather than attributing evidence to wrong machine', async () => {
    let latest;
    const controller = new FleetRefresh(fleetMachines([source]), { read: async () => ({ ...response, machineId: 'wrong' }) }, x => latest = x);
    await controller.refresh();
    assert.equal(latest[source.machineId].request, 'failure');
});
test('inclusive dates cross leap/month boundaries and reject invalid or unqueryable dates', () => {
    assert.deepEqual(selectedDays('2024-02-28', '2024-03-01'), ['2024-02-28', '2024-02-29', '2024-03-01']);
    assert.throws(() => selectedDays('2026-02-29', '2026-03-01'));
    assert.throws(() => selectedDays('9999-12-31', '9999-12-31'));
});
test('range consumes every page and retains separate processor series and successful absence', async () => {
    let calls = 0;
    const result = await loadRange('2026-10-09', '2026-10-09', [source, { ...source, processorId: 'p2' }], { queryProductionDayMetrics: async (req) => { calls++; if (req.sources[0].processorId === 'p2')
            return { items: [], continuationToken: null }; if (req.continuationToken === null)
            return { items: [], continuationToken: 'next' }; return { items: [item], continuationToken: null }; } }, new AbortController().signal);
    assert.equal(calls, 3);
    assert.equal(result.length, 2);
    assert.equal(result[0].items.length, 1);
    assert.equal(result[1].items.length, 0);
});
test('later page failure cannot return absence or a partial completed selection', async () => {
    await assert.rejects(loadRange('2026-10-09', '2026-10-09', [source], { queryProductionDayMetrics: async (req) => { if (req.continuationToken)
            throw new Error('offline'); return { items: [item], continuationToken: 'next' }; } }, new AbortController().signal), /offline/);
});
test('same exact identity with differing value or source revision fails explicitly', async () => {
    for (const conflict of [{ ...item, value: 0.7 }, { ...item, sourceRevision: { ...item.sourceRevision, position: 2 } }])
        await assert.rejects(loadRange('2026-10-09', '2026-10-09', [source], { queryProductionDayMetrics: async () => ({ items: [item, conflict], continuationToken: null }) }, new AbortController().signal), /Conflicting/);
});
test('identical duplicate results reconcile, wrong site or context fails', async () => {
    const result = await loadRange('2026-10-09', '2026-10-09', [source], { queryProductionDayMetrics: async () => ({ items: [item, { ...item }], continuationToken: null }) }, new AbortController().signal);
    assert.equal(result[0].items.length, 1);
    for (const wrong of [{ ...item, productionDay: { ...item.productionDay, siteId: 'other' } }, { ...item, context: { ...item.context, partId: 'part' } }])
        await assert.rejects(loadRange('2026-10-09', '2026-10-09', [source], { queryProductionDayMetrics: async () => ({ items: [wrong], continuationToken: null }) }, new AbortController().signal), /identity/);
});
test('graph segments never bridge missing or nonfinite values; genuine zero remains plotted', () => {
    assert.deepEqual(trendSegments([0, 0.5, null, 0.7, NaN, 0.9]), [[{ x: 0, y: 0 }, { x: 1, y: 0.5 }], [{ x: 3, y: 0.7 }], [{ x: 5, y: 0.9 }]]);
});
test('successful no-evidence remains a successful request and no-evidence state bucket', () => {
    const counts = fleetCounts(fleetMachines([source]), { [source.machineId]: { request: 'success', current: { ...response, outcome: 'no-evidence', evidence: null } } });
    assert.equal(counts.state['No evidence'], 1);
    assert.equal(counts.request.success, 1);
    assert.equal(counts.freshness.Unavailable, 1);
});
test('all selected dates are date-only queries with checked exclusive next-day boundaries', async () => {
    const requests = [];
    await loadRange('2024-02-28', '2024-03-01', [source], { queryProductionDayMetrics: async (req) => { requests.push(req); return { items: [], continuationToken: null }; } }, new AbortController().signal);
    assert.deepEqual(requests.map(r => [r.fromInclusive, r.toExclusive]), [['2024-02-28', '2024-02-29'], ['2024-02-29', '2024-03-01'], ['2024-03-01', '2024-03-02']]);
});
test('cancellation propagates without producing a completed range', async () => {
    const c = new AbortController();
    c.abort();
    await assert.rejects(loadRange('2026-10-09', '2026-10-09', [source], {}, c.signal), { name: 'AbortError' });
});

test('decimal strings plot approximately without replacing exact values; nonzero underflow remains a gap', () => {
    const decimal = { ...item, value: '0.5000000000000000000000000001' };
    assert.equal(plottedValue(decimal), 0.5);
    assert.equal(decimal.value, '0.5000000000000000000000000001');
    assert.equal(plottedValue({ ...item, value: '1e-9999' }), null);
    assert.equal(plottedValue({ ...item, value: '0e9999' }), 0);
    assert.equal(plottedValue({ ...item, status: 'insufficient-evidence', value: null }), null);
});

test('distinct metrics in one group must share every checkpoint field, including across pages', async () => {
    for (const changed of [{position: 2}, {processorId: 'different-aggregation'}, {streamKey: 'different-stream'}]) {
        let calls = 0;
        await assert.rejects(loadRange('2026-10-09', '2026-10-09', [source], {
            queryProductionDayMetrics: async () => ++calls === 1
                ? {items: [item], continuationToken: 'page2'}
                : {items: [{...item, metricKey: 'utilization.elr', sourceRevision: {...item.sourceRevision, ...changed}}], continuationToken: null},
        }, new AbortController().signal), /Mixed aggregation checkpoints/);
        assert.equal(calls, 2);
    }
});
test('same group accepts distinct metrics at equivalent exact checkpoints; different days and processors remain independent', async () => {
    const result = await loadRange('2026-10-09', '2026-10-10', [source, {...source, processorId: 'p2'}], {
        queryProductionDayMetrics: async request => {
            const changed = {...item, processorId: request.sources[0].processorId,
                productionDay: {...item.productionDay, businessDate: request.fromInclusive},
                sourceRevision: {...item.sourceRevision, position: request.fromInclusive === '2026-10-09' ? 1 : 2,
                    processorId: request.sources[0].processorId === 'p1' ? 'a' : 'b'}};
            return {items: [changed, {...changed, metricKey: 'utilization.elr', sourceRevision: {...changed.sourceRevision, position: String(changed.sourceRevision.position)}}], continuationToken: null};
        },
    }, new AbortController().signal);
    assert.equal(result.length, 4);
    assert.ok(result.every(row => row.items.length === 2));
});
test('oversized date or source/day selections fail before any request', async () => {
    let calls = 0;
    const client = {queryProductionDayMetrics: async () => { ++calls; return {items: [], continuationToken: null}; }};
    for (const [from, through, sources] of [
        ['0001-01-01', '9999-12-30', [source]],
        ['2024-01-01', '2025-01-01', [source]],
        ['2024-01-01', '2024-12-31', [source, {...source, processorId: 'p2'}, {...source, processorId: 'p3'}]],
    ]) await assert.rejects(loadRange(from, through, sources, client, new AbortController().signal), /at most/);
    assert.equal(calls, 0);
    assert.throws(() => selectedDays('0001-01-01', '9999-12-30'), /at most/);
    assert.equal(selectedDays('2024-01-01', '2024-12-31').length, 366);
});
test('selection at source/day limit is accepted; one combination beyond it is rejected', async () => {
    let calls = 0;
    const client = {queryProductionDayMetrics: async () => { ++calls; return {items: [], continuationToken: null}; }};
    const sources = Array.from({length: 10}, (_, index) => ({...source, processorId: `p${index}`}));
    const result = await loadRange('2026-01-01', '2026-04-10', sources, client, new AbortController().signal);
    assert.equal(result.length, 1000);
    assert.equal(calls, 1000);
    await assert.rejects(loadRange('2026-01-01', '2026-04-11', sources, client, new AbortController().signal), /at most/);
    assert.equal(calls, 1000);
});
