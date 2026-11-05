import test from 'node:test';
import assert from 'node:assert/strict';
import { overviewCounts, overviewSelection, overviewSearch, selectedMachines } from '../src/application/management-overview-model.ts';
import { validProfiles } from '../src/application/machine-capabilities.ts';
import { FleetRefresh } from '../src/application/fleet-status.ts';
const source = { machineId: '11111111-1111-1111-1111-111111111111', processorId: 'p1', siteId: 'site', productionLineId: 'line', displayName: 'Machine', groupName: null, displayOrder: 0 };
const second = { ...source, machineId: '22222222-2222-2222-2222-222222222222', productionLineId: 'line2' };
const response = (machineId, machineState = 'stopped', freshness = 'current', usability = 'current') => ({ machineId, outcome: 'evidence', coverage: 'complete', evidence: { machineState, freshness, usability } });
test('distinct population and independent counts exclude stale, behind, retained and failed states', () => {
    const sources = [source, {...source, processorId: 'p2'}, second];
    const machines = selectedMachines(overviewSelection('', sources), sources);
    assert.equal(machines.length, 2);
    for (const bad of [response(second.machineId, 'stopped', 'stale'), response(second.machineId, 'stopped', 'current', 'behind')]) {
        const counts = overviewCounts(machines, { [source.machineId]: {request: 'success', current: response(source.machineId)}, [second.machineId]: {request: 'success', current: bad} });
        assert.equal(counts.operational.stopped, 1);
        assert.equal(counts.operational['No usable current state'], 1);
        for (const group of Object.values(counts)) assert.equal(Object.values(group).reduce((a,b) => a+b), 2);
    }
    const counts = overviewCounts(machines, { [second.machineId]: {request: 'failure', retained: response(second.machineId)} });
    assert.equal(counts.operational.stopped, 0);
    assert.equal(counts.request.failure, 1);
});
test('selection round-trips and rejects invalid, duplicate or conflicting filters', () => {
    const value = {day: '2026-10-16', site: 'site', line: 'line', machine: source.machineId};
    assert.deepEqual(overviewSelection(overviewSearch(value), [source,second]), value);
    for (const search of ['?day=2026-02-30', '?line=missing', '?site=site&line=line2&machine='+source.machineId, '?machine=unknown', '?day=2026-10-16&day=2026-10-17', '?unexpected=1']) assert.throws(() => overviewSelection(search, [source,second]));
});
test('profile schema distinguishes legacy, unknown version and conflicting bindings', () => {
    assert.equal(validProfiles(undefined, undefined, [source.machineId]), true);
    assert.equal(validProfiles(2, [], [source.machineId]), false);
    const profile = { machineId: source.machineId, profileId:'digital', profileVersion:'1', integrationCategoryId:'digital', approvalReference:'approved', effectiveFrom:'2026-10-01T00:00:00Z', stoppedLabel:'Machine OFF', capabilities:[{key:'operational-state', support:'supported', qualification:'qualified'}] };
    assert.equal(validProfiles(1, [profile], [source.machineId]), true);
    assert.equal(validProfiles(1, [profile, profile], [source.machineId]), false);
    assert.equal(validProfiles(1, [profile], []), false);
    assert.equal(validProfiles(1, [{...profile, capabilities:[]}], [source.machineId]), false);
});
test('fleet refresh is bounded to four reads and stops pending work after disposal', async () => {
    const sources = Array.from({length: 10}, (_, i) => ({...source, machineId: `machine-${i}`}));
    const machines = selectedMachines(overviewSelection('', sources), sources);
    let calls=0; const completions=[];
    const refresh = new FleetRefresh(machines, {read: id => {++calls; return new Promise(resolve => completions.push(() => resolve(response(id))));}}, () => {});
    const pending=refresh.refresh();
    assert.equal(calls, 4);
    refresh.dispose(); completions.forEach(complete => complete()); await pending;
    assert.equal(calls, 4);
});
test('combined selected-day sources retain the 1000 source/day bound', () => {
    const sources = Array.from({length:1001},(_,i)=>({...source,processorId:`p${i}`}));
    assert.throws(()=>overviewSelection('?day=2026-10-16',sources),/1000/);
});
