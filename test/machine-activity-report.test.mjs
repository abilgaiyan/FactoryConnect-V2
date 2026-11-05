import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activityDateRange, activityStates, decodeActivityReport, loadActivityReport, formatActivityDuration } from '../src/application/machine-activity-report.ts';
const id = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
const range = activityDateRange('2026-10-06', '2026-10-06');
function fixture() {
  return { machineId: id, stateProcessorId: 'machine-state-activity', streamKey: 'mtconnect', throughPosition: '9007199254740993',
    ...range, readAsOf: '2026-10-11T00:00:00Z', intervals: [], totals: activityStates.map(state => ({ state, durationTicks: 0 })), unrepresentedTicks: 864000000000 };
}
test('IST inclusive dates convert explicitly to UTC half-open bounds', () => {
  assert.deepEqual(range, { fromInclusive: '2026-10-05T18:30:00.000Z', toExclusive: '2026-10-06T18:30:00.000Z' });
  for (const pair of [['2026-02-30','2026-03-01'], ['2026-10-07','2026-10-06'], ['2026-01-01','2026-02-01']]) assert.throws(() => activityDateRange(...pair));
});
test('empty stored intervals represent no stored interval, not stopped or unknown time', () => {
  const r = decodeActivityReport(fixture(), id, range);
  assert.equal(r.unrepresentedTicks, 864000000000); assert.ok(r.totals.every(t => t.durationTicks === 0));
});
test('exact ticks, recorded unknown and large string checkpoint survive decoding', () => {
  const f = fixture(); const n = 1234567;
  f.intervals.push({ position: '9007199254740992', state: 'unknown', startedAtUtc: '2026-10-05T18:30:00Z', endedAtUtc: '2026-10-05T18:30:00.1234567Z',
    sourceStartedAtUtc: '2026-10-05T18:30:00Z', sourceEndedAtUtc: '2026-10-05T18:30:00.1234567Z', durationTicks: n, instanceId: '1', sequence: '2' });
  f.totals[0].durationTicks = n; f.unrepresentedTicks -= n;
  assert.equal(decodeActivityReport(f, id, range).intervals[0].durationTicks, n);
  for (const mutate of [r => r.intervals[0].position = '9007199254740994', r => r.totals[0].durationTicks++, r => r.intervals.push({...r.intervals[0]}), r => r.intervals[0].durationTicks++]) {
    const bad = structuredClone(f); mutate(bad); assert.throws(() => decodeActivityReport(bad, id, range));
  }
});
test('wrong machine and corrupt summary never produce a report', () => {
  assert.throws(() => decodeActivityReport(fixture(), 'different', range));
  const bad = fixture(); bad.unrepresentedTicks = 0; assert.throws(() => decodeActivityReport(bad, id, range));
});
test('failed retrieval remains failure and request carries exact interval', async () => {
  const controller = new AbortController(); let request;
  await assert.rejects(loadActivityReport(id, range, controller.signal, async (url, options) => {
    request = { url, options }; return new Response('', { status: 503 });
  }), /503/);
  assert.deepEqual(JSON.parse(request.options.body), range); assert.equal(request.options.signal, controller.signal);
});

test('cancellation propagates and a response arriving after cancellation cannot become a report', async () => {
  const controller = new AbortController(); let finish;
  const pending = loadActivityReport(id, range, controller.signal, () => new Promise(resolve => {finish = resolve;}));
  controller.abort(); finish(Response.json(fixture()));
  await assert.rejects(pending, error => error.name === 'AbortError');
  let called = false;
  await assert.rejects(loadActivityReport(id, range, controller.signal, async () => {called = true; return Response.json(fixture());}));
  assert.equal(called, false);
});

test('durations use readable units while retaining exact seconds', () => {
  assert.equal(formatActivityDuration(24537280), '2.45 seconds (2.4537280 s exact)');
  assert.equal(formatActivityDuration(6000000000), '10.00 minutes (600.0000000 s exact)');
  assert.equal(formatActivityDuration(36000000000), '1.00 hours (3600.0000000 s exact)');
});
