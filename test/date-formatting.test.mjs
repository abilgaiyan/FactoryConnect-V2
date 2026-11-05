import assert from 'node:assert/strict';
import test from 'node:test';
import { formatReportDate, formatReportTimestamp } from '../src/presentation/date-formatting.ts';
test('civil report date formats without changing its calendar date', () => {
    assert.equal(formatReportDate('2026-10-05'), '05/10/2026');
    assert.equal(formatReportDate('invalid'), 'invalid');
});
test('IST display crosses UTC day boundary explicitly without changing the input', () => {
    const timestamp = '2026-10-05T22:00:00.1234567Z';
    assert.equal(formatReportTimestamp(timestamp), '06/10/2026 03:30:00 IST');
    assert.equal(timestamp, '2026-10-05T22:00:00.1234567Z');
    assert.equal(formatReportTimestamp('invalid'), 'invalid');
});
