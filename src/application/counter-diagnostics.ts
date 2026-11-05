import { counterFlags, type CounterSample } from './machine-counter-report.ts';

const universalFlags = new Set(['ContinuityNotEstablished', 'AttributionNotEstablished']);
export function hasCounterComparisonIssue(sample: CounterSample): boolean {
  return sample.flags.some(flag => !universalFlags.has(flag));
}

/** Counts retained observations and diagnostic comparisons, never produced units. */
export function summarizeCounterDiagnostics(samples: readonly CounterSample[]) {
  let increases = 0; let unchanged = 0; let notComparable = 0; let issueRows = 0;
  const flags = counterFlags.filter(flag => !universalFlags.has(flag))
    .map(flag => ({ flag, count: 0 }));
  for (const sample of samples) {
    if (sample.arithmeticDifference === null) notComparable++;
    else if (BigInt(sample.arithmeticDifference) === 0n) unchanged++;
    else increases++;
    if (hasCounterComparisonIssue(sample)) issueRows++;
    for (const entry of flags) if (sample.flags.includes(entry.flag)) entry.count++;
  }
  return { sampleCount: samples.length, increases, unchanged, notComparable, issueRows, flags };
}
