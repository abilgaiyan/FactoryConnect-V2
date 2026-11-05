import { activityDateRange } from './machine-activity-report.ts';

export const counterFlags = ['ContinuityNotEstablished', 'AttributionNotEstablished', 'MissingValue', 'NonGoodEvidence',
  'NotUsableAsCounter', 'BaselineOnly', 'InstanceChanged', 'NonIncreasingTimestamp', 'PreviousEvidenceUnusable', 'CounterDecreased', 'SignalTypeChanged'] as const;
export interface CounterSample {
  readonly position: string; readonly instanceId: string; readonly sequence: string;
  readonly observedAtUtc: string; readonly signalType: string; readonly quality: string; readonly rawValue: string | null;
  readonly previousPosition: string | null; readonly arithmeticDifference: string | null; readonly flags: readonly string[];
}
export interface CounterReport {
  readonly machineId: string; readonly streamKey: string; readonly throughPosition: string; readonly acquisitionRevision: string;
  readonly fromInclusive: string; readonly toExclusive: string; readonly readAsOf: string;
  readonly source: string; readonly address: string;
  readonly availableSignals: readonly { readonly source: string; readonly address: string }[];
  readonly samples: readonly CounterSample[];
}
export interface CounterSelection extends ReturnType<typeof activityDateRange> { readonly source: string; readonly address: string }
const uint = (v: unknown, positive = false): v is string => typeof v === 'string' && /^(0|[1-9]\d*)$/.test(v) &&
  BigInt(v) <= 18446744073709551615n && (!positive || BigInt(v) > 0n);
const text = (v: unknown): v is string => typeof v === 'string' && v.trim().length > 0;
function instant(v: unknown): bigint {
  if (typeof v !== 'string') throw new Error('Invalid UTC timestamp.');
  const m = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(v);
  if (!m) throw new Error('Invalid UTC timestamp.');
  const ms = Date.parse(`${m[1]}Z`);
  if (!Number.isFinite(ms) || new Date(ms).toISOString().slice(0, 19) !== m[1]) throw new Error('Invalid timestamp.');
  return BigInt(ms) * 10000n + BigInt((m[2] ?? '').padEnd(7, '0'));
}
export function decodeCounterReport(value: unknown, machineId: string, selection: CounterSelection): CounterReport {
  const fail = () => { throw new Error('Invalid or mismatched raw counter response.'); };
  if (!value || typeof value !== 'object') return fail();
  const r = value as CounterReport;
  if (typeof r.machineId !== 'string' || r.machineId.toLowerCase() !== machineId.toLowerCase() || !text(r.streamKey) ||
      !uint(r.throughPosition, true) || !uint(r.acquisitionRevision) || r.source !== selection.source || r.address !== selection.address ||
      instant(r.fromInclusive) !== instant(selection.fromInclusive) || instant(r.toExclusive) !== instant(selection.toExclusive) ||
      !Array.isArray(r.samples) || r.samples.length > 5000 || !Array.isArray(r.availableSignals)) return fail();
  instant(r.readAsOf);
  const from = instant(r.fromInclusive); const to = instant(r.toExclusive);
  let previous = 0n;
  for (const s of r.samples) {
    if (!s || !uint(s.position, true) || BigInt(s.position) <= previous || BigInt(s.position) > BigInt(r.throughPosition) ||
        !uint(s.instanceId) || !uint(s.sequence) || !['Digital', 'Analog', 'Counter', 'WholeNumber', 'Numeric', 'Text', 'Enumeration', 'Timestamp'].includes(s.signalType) ||
        !['Good', 'Uncertain', 'Bad'].includes(s.quality) || (s.rawValue !== null && typeof s.rawValue !== 'string') ||
        (s.previousPosition !== null && (!uint(s.previousPosition, true) || BigInt(s.previousPosition) >= BigInt(s.position))) ||
        (s.arithmeticDifference !== null && (typeof s.arithmeticDifference !== 'string' || !/^(0|[1-9]\d*)$/.test(s.arithmeticDifference))) ||
        !Array.isArray(s.flags) || new Set(s.flags).size !== s.flags.length || s.flags.some((f: unknown) => !counterFlags.includes(f as typeof counterFlags[number])) ||
        !s.flags.includes('ContinuityNotEstablished') || !s.flags.includes('AttributionNotEstablished') ||
        instant(s.observedAtUtc) < from || instant(s.observedAtUtc) >= to) return fail();
    if (s.arithmeticDifference !== null && (!['Numeric', 'Enumeration'].includes(s.signalType) || s.quality !== 'Good' ||
        s.previousPosition === null || typeof s.rawValue !== 'string' || !/^[0-9]+$/.test(s.rawValue) ||
        s.flags.some((f: unknown) => !['ContinuityNotEstablished', 'AttributionNotEstablished'].includes(f as string)))) return fail();
    previous = BigInt(s.position);
  }
  const channels = new Set<string>();
  for (const s of r.availableSignals) {
    if (!s || !text(s.source) || !text(s.address)) return fail();
    const key = JSON.stringify([s.source, s.address]); if (channels.has(key)) return fail(); channels.add(key);
  }
  return r;
}
export async function loadCounterReport(machineId: string, selection: CounterSelection, signal: AbortSignal,
  fetchImplementation: typeof fetch = globalThis.fetch): Promise<CounterReport> {
  if (!text(selection.source) || !text(selection.address)) throw new Error('Enter the exact source and address.');
  signal.throwIfAborted();
  const response = await fetchImplementation(`/api/machines/v1/${encodeURIComponent(machineId)}/counter/query`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify(selection), signal,
  });
  signal.throwIfAborted();
  if (!response.ok) throw new Error(`Counter request failed (${response.status}); no report established.`);
  const body: unknown = await response.json(); signal.throwIfAborted();
  return decodeCounterReport(body, machineId, selection);
}
