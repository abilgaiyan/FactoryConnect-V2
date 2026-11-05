import assert from 'node:assert/strict';
import test from 'node:test';
import { addProductionDetails, emptyProductionDetails, productionDetailsAt } from '../src/application/production-details-preview.ts';
const now = '2026-10-29T06:00:00Z';
const input = { ...emptyProductionDetails, order: ' JOB-01 ', part: 'PART-01', operation: '20', localStart: '2026-10-28T07:30' };
test('declarations retain authors, effective timezone and prior entries across shifts', () => {
  const first = addProductionDetails([], input, 'cnc', 'operator', now, true);
  assert.equal(first.error, undefined);
  assert.equal(first.entries[0].effectiveAt, '2026-10-28T02:00:00.000Z');
  assert.equal(first.entries[0].order, 'JOB-01');
  const before = JSON.stringify(first.entries);
  const second = addProductionDetails(first.entries, { ...input, part: 'PART-02', localStart: '2026-10-29T07:30' }, 'cnc', 'supervisor', now, true);
  assert.equal(second.error, undefined);
  assert.equal(JSON.stringify(first.entries), before);
  assert.equal(second.entries[1].author, 'supervisor');
  assert.equal(second.entries[1].recordedAt, now);
  assert.equal(productionDetailsAt(second.entries, 'cnc', '2026-10-28T18:30:00Z').part, 'PART-01');
  assert.equal(productionDetailsAt(second.entries, 'cnc', '2026-10-29T02:00:00Z').part, 'PART-02');
  assert.equal(productionDetailsAt(second.entries, 'moxa', now), undefined);
});
test('past start requires acknowledgement; duplicate and earlier job changes preserve history', () => {
  assert.match(addProductionDetails([], input, 'cnc', 'operator', now, false).error, /Backdated/);
  const initial = addProductionDetails([], input, 'cnc', 'operator', now, true).entries;
  for (const localStart of ['2026-10-28T07:30', '2026-10-27T07:30']) {
    const result = addProductionDetails(initial, { ...input, localStart }, 'cnc', 'operator', now, true);
    assert.match(result.error, /Conflicting/); assert.equal(result.entries, initial);
  }
  assert.equal(addProductionDetails(initial, input, 'moxa', 'supervisor', now, true).error, undefined);
});
test('partial declarations are permitted; missing dates and impossible calendars are rejected', () => {
  assert.equal(addProductionDetails([], { ...emptyProductionDetails, localStart: input.localStart }, 'cnc', 'operator', now, true).entries[0].part, '');
  for (const localStart of ['', '2026-02-30T07:30']) assert.ok(addProductionDetails([], { ...input, localStart }, 'cnc', 'operator', now, true).error);
  assert.ok(addProductionDetails([], { ...input, offset: '+14:30' }, 'cnc', 'operator', now, true).error);
  assert.ok(addProductionDetails([], input, '', 'operator', now, true).error);
});
