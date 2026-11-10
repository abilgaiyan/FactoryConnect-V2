import { EvidenceReadFailure } from "./retained-evidence-client.ts";

export interface ActivitySeries {
  mappingId: string;
  sourceAddress: string;
  qualificationSha256: string;
  generation: string;
  lastObservedAt: string;
  lastProcessedState: "Running" | "Idle" | "Stopped" | "Fault" | "Unknown" | null;
  packageRevisionId: string;
  acknowledgementId: string;
}
export interface ActivityPeriod {
  effectId: string;
  publicationOperationId: string;
  state: "Running" | "Idle" | "Stopped" | "Fault" | "Unknown";
  startedAt: string;
  endedAt: string;
  durationTicks: string;
  instanceId: string;
  startSequence: string;
  endSequence: string;
  startRawValue: string;
  endRawValue: string;
}
interface Scope {
  machineId: string;
  siteId: string;
  streamKey: string;
  evidenceKind: "historical-derived-activity";
  currentConnectivity: null;
  verification: "immutable-publication-receipts";
  continuity: "same-batch-only-not-physical-completeness";
}
export interface ActivitySeriesList extends Scope {
  status: "no-published-history" | "published-history";
  series: ActivitySeries[];
}
export interface ActivityHistoryPage extends Scope {
  series: ActivitySeries;
  rows: ActivityPeriod[];
  nextCursor: string | null;
}
const record = (x: unknown): x is Record<string, unknown> => typeof x === "object" && x !== null && !Array.isArray(x);
const guid = (x: unknown): x is string => typeof x === "string" && /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(x);
const uint = (x: unknown): x is string => typeof x === "string" && /^(0|[1-9]\d{0,19})$/.test(x) && BigInt(x) <= 18446744073709551615n;
const utc = (x: unknown): x is string => typeof x === "string" && /(?:Z|\+00:00)$/.test(x) && Number.isFinite(Date.parse(x));
function scope(x: unknown): x is Record<string, unknown> & Scope {
  return record(x) && guid(x.machineId) && guid(x.siteId) && typeof x.streamKey === "string"
    && x.evidenceKind === "historical-derived-activity" && x.currentConnectivity === null
    && x.verification === "immutable-publication-receipts" && x.continuity === "same-batch-only-not-physical-completeness";
}
function series(x: unknown): x is ActivitySeries {
  return record(x) && guid(x.mappingId) && typeof x.sourceAddress === "string" && x.sourceAddress.length > 0
    && typeof x.qualificationSha256 === "string" && /^[\da-f]{64}$/i.test(x.qualificationSha256)
    && uint(x.generation) && BigInt(x.generation) > 0n && utc(x.lastObservedAt)
    && (x.lastProcessedState === null || ["Running", "Idle", "Stopped", "Fault", "Unknown"].includes(String(x.lastProcessedState)))
    && guid(x.packageRevisionId) && guid(x.acknowledgementId);
}
function period(x: unknown): x is ActivityPeriod {
  return record(x) && guid(x.effectId) && guid(x.publicationOperationId)
    && ["Running", "Idle", "Stopped", "Fault", "Unknown"].includes(String(x.state))
    && utc(x.startedAt) && utc(x.endedAt) && Date.parse(x.endedAt) >= Date.parse(x.startedAt)
    && uint(x.durationTicks) && BigInt(x.durationTicks) > 0n && BigInt(x.durationTicks) <= 9223372036854775807n
    && uint(x.instanceId) && uint(x.startSequence) && uint(x.endSequence) && BigInt(x.endSequence) > BigInt(x.startSequence)
    && typeof x.startRawValue === "string" && typeof x.endRawValue === "string";
}
async function get(base: string, path: string, bearer: string, signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<unknown> {
  const url = new URL(path, base);
  if (url.protocol !== "https:" || (typeof window !== "undefined" && url.origin !== window.location.origin)) throw new EvidenceReadFailure(403);
  const response = await fetcher(url, { method: "GET", headers: { Accept: "application/json", Authorization: `Bearer ${bearer}` },
    cache: "no-store", redirect: "error", ...(signal ? { signal } : {}) });
  if (!response.ok) throw new EvidenceReadFailure(response.status);
  return response.json();
}
export async function readActivitySeries(base: string, machineId: string, bearer: string, signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<ActivitySeriesList> {
  const body = await get(base, `api/commissioning/v2/machines/${encodeURIComponent(machineId)}/activity`, bearer, signal, fetcher);
  if (!scope(body) || body.machineId !== machineId || !Array.isArray(body.series) || body.series.length > 100
    || !body.series.every(series) || new Set(body.series.map(x => x.mappingId)).size !== body.series.length
    || body.status !== (body.series.length ? "published-history" : "no-published-history")) throw new EvidenceReadFailure(502);
  return body as unknown as ActivitySeriesList;
}
export async function readActivityHistory(base: string, machineId: string, mappingId: string, bearer: string,
  cursor: string | null = null, signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<ActivityHistoryPage> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";
  const body = await get(base, `api/commissioning/v2/machines/${encodeURIComponent(machineId)}/activity/${encodeURIComponent(mappingId)}${query}`, bearer, signal, fetcher);
  if (!scope(body) || body.machineId !== machineId || !series(body.series) || body.series.mappingId !== mappingId
    || !Array.isArray(body.rows) || body.rows.length > 100 || !body.rows.every(period)
    || new Set(body.rows.map(x => x.effectId)).size !== body.rows.length
    || !(body.nextCursor === null || (typeof body.nextCursor === "string" && body.nextCursor.length > 0 && body.nextCursor.length <= 2048)))
    throw new EvidenceReadFailure(502);
  return body as unknown as ActivityHistoryPage;
}
