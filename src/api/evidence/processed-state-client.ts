import { EvidenceReadFailure } from "./retained-evidence-client.ts";

export interface ProcessedState {
  mappingId: string;
  sourceAddress: string;
  lastProcessedState: "Running" | "Idle" | "Stopped" | "Fault" | "Unknown" | null;
  lastObservedAt: string;
  generation: string;
  packageRevisionId: string;
  acknowledgementId: string;
}
export interface ProcessedStateResult {
  machineId: string;
  siteId: string;
  streamKey: string;
  evidenceKind: "verified-processed-state";
  currentConnectivity: null;
  freshness: "not-established";
  coverage: "not-established";
  readAsOf: string;
  status: "no-processed-state" | "processed-state";
  states: ProcessedState[];
}
const record = (x: unknown): x is Record<string, unknown> => typeof x === "object" && x !== null && !Array.isArray(x);
const guid = (x: unknown): x is string => typeof x === "string" && /^[\\da-f]{8}-[\\da-f]{4}-[\\da-f]{4}-[\\da-f]{4}-[\\da-f]{12}$/i.test(x);
const uint = (x: unknown): x is string => typeof x === "string" && /^(0|[1-9]\\d{0,19})$/.test(x) && BigInt(x) <= 18446744073709551615n;
const utc = (x: unknown): x is string => typeof x === "string" && /(?:Z|\\+00:00)$/.test(x) && Number.isFinite(Date.parse(x));
const validState = (x: unknown): x is ProcessedState => record(x) && guid(x.mappingId)
  && typeof x.sourceAddress === "string" && x.sourceAddress.length > 0
  && (x.lastProcessedState === null || ["Running","Idle","Stopped","Fault","Unknown"].includes(String(x.lastProcessedState)))
  && utc(x.lastObservedAt) && uint(x.generation) && BigInt(x.generation) > 0n
  && guid(x.packageRevisionId) && guid(x.acknowledgementId);

export async function readProcessedState(base: string, machineId: string, bearer: string,
  signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<ProcessedStateResult> {
  const url = new URL(`api/commissioning/v2/machines/${encodeURIComponent(machineId)}/processed-state`, base);
  if (url.protocol !== "https:" || (typeof window !== "undefined" && url.origin !== window.location.origin))
    throw new EvidenceReadFailure(403);
  const response = await fetcher(url, { method: "GET", headers: { Accept: "application/json", Authorization: `Bearer ${bearer}` },
    cache: "no-store", redirect: "error", ...(signal ? { signal } : {}) });
  if (!response.ok) throw new EvidenceReadFailure(response.status);
  const body: unknown = await response.json();
  if (!record(body) || body.machineId !== machineId || !guid(body.siteId) || typeof body.streamKey !== "string"
    || body.evidenceKind !== "verified-processed-state" || body.currentConnectivity !== null
    || body.freshness !== "not-established" || body.coverage !== "not-established" || !utc(body.readAsOf)
    || !Array.isArray(body.states) || body.states.length > 100 || !body.states.every(validState)
    || new Set(body.states.map(x => x.mappingId)).size !== body.states.length
    || body.status !== (body.states.length ? "processed-state" : "no-processed-state"))
    throw new EvidenceReadFailure(502);
  return body as unknown as ProcessedStateResult;
}
