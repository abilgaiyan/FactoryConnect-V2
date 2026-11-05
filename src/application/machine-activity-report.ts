export const activityStates = ['unknown', 'stopped', 'idle', 'running', 'fault', 'offline'] as const;
export type ActivityState = typeof activityStates[number];
export interface ActivityInterval {
  readonly position: string; readonly state: ActivityState;
  readonly startedAtUtc: string; readonly endedAtUtc: string;
  readonly sourceStartedAtUtc: string; readonly sourceEndedAtUtc: string;
  readonly durationTicks: number; readonly instanceId: string; readonly sequence: string;
}
export interface ActivityReport {
  readonly machineId: string; readonly stateProcessorId: string; readonly streamKey: string;
  readonly throughPosition: string | null; readonly fromInclusive: string; readonly toExclusive: string;
  readonly readAsOf: string; readonly intervals: readonly ActivityInterval[];
  readonly totals: readonly { readonly state: ActivityState; readonly durationTicks: number }[];
  readonly unrepresentedTicks: number;
}
/** IST civil dates, not a declaration of the site's production calendar. */
export function activityDateRange(from: string, through: string) {
  const date = (value: string) => {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new Error('Select valid dates.');
    const parsed = new Date(`${value}T00:00:00.000Z`);
    if (!Number.isFinite(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== value)
      throw new Error('Select valid dates.');
    return parsed.getTime();
  };
  const start = date(from); const end = date(through) + 86400000;
  if (end <= start || end - start > 31 * 86400000) throw new Error('Select 1–31 inclusive calendar days.');
  return { fromInclusive: new Date(start - 19800000).toISOString(),
    toExclusive: new Date(end - 19800000).toISOString() };
}
const uint64 = (v: unknown, positive = false): v is string => typeof v === 'string' && /^(0|[1-9]\d*)$/.test(v) &&
  BigInt(v) <= 18446744073709551615n && (!positive || BigInt(v) > 0n);
const ticks = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0;
const state = (v: unknown): v is ActivityState => activityStates.includes(v as ActivityState);
function instant(v: unknown): bigint {
  if (typeof v !== 'string') throw new Error('Invalid timestamp.');
  const m = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(v);
  if (!m) throw new Error('Invalid UTC timestamp.');
  const seconds = Date.parse(`${m[1]}Z`);
  if (!Number.isFinite(seconds) || new Date(seconds).toISOString().slice(0, 19) !== m[1]) throw new Error('Invalid timestamp.');
  return BigInt(seconds) * 10000n + BigInt((m[2] ?? '').padEnd(7, '0'));
}
export function decodeActivityReport(value: unknown, machineId: string, range: ReturnType<typeof activityDateRange>): ActivityReport {
  const fail = () => { throw new Error('Invalid or mismatched machine activity response.'); };
  if (!value || typeof value !== 'object') return fail();
  const r = value as ActivityReport;
  if (typeof r.machineId !== 'string' || r.machineId.toLowerCase() !== machineId.toLowerCase() ||
      r.stateProcessorId !== 'machine-state-activity' || typeof r.streamKey !== 'string' || !r.streamKey.trim() ||
      (r.throughPosition !== null && !uint64(r.throughPosition, true)) ||
      instant(r.fromInclusive) !== instant(range.fromInclusive) || instant(r.toExclusive) !== instant(range.toExclusive) ||
      !Array.isArray(r.intervals) || !Array.isArray(r.totals) || r.intervals.length > 5000 || !ticks(r.unrepresentedTicks)) return fail();
  instant(r.readAsOf);
  const from = instant(r.fromInclusive); const to = instant(r.toExclusive);
  const sums = new Map<ActivityState, number>(activityStates.map(s => [s, 0]));
  const positions = new Set<string>(); let cursor = from; let represented = 0;
  for (const i of r.intervals) {
    if (!i || !state(i.state) || !ticks(i.durationTicks) || !uint64(i.position, true) || !uint64(i.instanceId) ||
        !uint64(i.sequence) || r.throughPosition === null || BigInt(i.position) > BigInt(r.throughPosition) || positions.has(i.position)) return fail();
    positions.add(i.position);
    const start = instant(i.startedAtUtc); const end = instant(i.endedAtUtc);
    const sourceStart = instant(i.sourceStartedAtUtc); const sourceEnd = instant(i.sourceEndedAtUtc);
    if (start < cursor || end <= start || end > to || sourceStart > start || sourceEnd < end ||
        start !== (sourceStart > from ? sourceStart : from) || end !== (sourceEnd < to ? sourceEnd : to) ||
        end - start !== BigInt(i.durationTicks)) return fail();
    cursor = end; represented += i.durationTicks; sums.set(i.state, sums.get(i.state)! + i.durationTicks);
  }
  if (r.totals.length !== activityStates.length || new Set(r.totals.map(t => t.state)).size !== activityStates.length ||
      r.totals.some(t => !state(t.state) || !ticks(t.durationTicks) || t.durationTicks !== sums.get(t.state)) ||
      BigInt(represented + r.unrepresentedTicks) !== to - from) return fail();
  return r;
}
export async function loadActivityReport(machineId: string, range: ReturnType<typeof activityDateRange>, signal: AbortSignal,
  fetchImplementation: typeof fetch = globalThis.fetch): Promise<ActivityReport> {
  signal.throwIfAborted();
  const response = await fetchImplementation(`/api/machines/v1/${encodeURIComponent(machineId)}/activity/query`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify(range), signal,
  });
  signal.throwIfAborted();
  if (!response.ok) throw new Error(`Activity request failed (${response.status}); no report established.`);
  const body: unknown = await response.json();
  signal.throwIfAborted();
  return decodeActivityReport(body, machineId, range);
}
export function formatActivityDuration(ticks: number): string {
  const seconds = ticks / 10000000;
  if (seconds < 60) return `${seconds.toFixed(2)} seconds (${seconds.toFixed(7)} s exact)`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(2)} minutes (${seconds.toFixed(7)} s exact)`;
  return `${(seconds / 3600).toFixed(2)} hours (${seconds.toFixed(7)} s exact)`;
}
