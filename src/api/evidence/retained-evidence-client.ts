export interface RetainedObservation {
  sequence: string;
  instanceId: string;
  mappingId: string;
  address: string;
  rawValue: string;
  quality: "source-reported" | "unavailable";
  sourceTimestamp: string;
  observedAt: string;
  receivedAt: string;
  batchId: string;
  acknowledgementId: string;
  documentHash: string;
}

export interface RetainedEvidencePage {
  machineId: string;
  siteId: string;
  streamKey: string;
  evidenceKind: "historical-retained-evidence";
  currentConnectivity: null;
  rows: RetainedObservation[];
}

export class EvidenceReadFailure extends Error {
  constructor(readonly status: number) {
    super(status === 401 ? "Commissioning read access denied."
      : status === 503 ? "Evidence authority unavailable."
      : "Evidence request failed.");
  }
}

/** The bearer is an existing human-session credential; this client never creates one. */
export async function readRetainedEvidence(
  baseAddress: string,
  machineId: string,
  bearer: string,
  signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch,
): Promise<RetainedEvidencePage> {
  const endpoint = new URL(`api/commissioning/v2/machines/${encodeURIComponent(machineId)}/evidence`, baseAddress);
  if (endpoint.protocol !== "https:" || (typeof window !== "undefined" && endpoint.origin !== window.location.origin))
    throw new EvidenceReadFailure(403);
  const response = await fetchImplementation(
    endpoint,
    { method: "GET", headers: { Accept: "application/json", Authorization: `Bearer ${bearer}` },
      ...(signal ? { signal } : {}), cache: "no-store", redirect: "error" },
  );
  if (!response.ok) throw new EvidenceReadFailure(response.status);
  const body: unknown = await response.json();
  if (!isEvidencePage(body) || body.machineId !== machineId) throw new EvidenceReadFailure(502);
  return body;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isEvidencePage(value: unknown): value is RetainedEvidencePage {
  if (!isRecord(value) || value.evidenceKind !== "historical-retained-evidence"
    || value.currentConnectivity !== null || typeof value.machineId !== "string"
    || typeof value.siteId !== "string" || typeof value.streamKey !== "string"
    || !Array.isArray(value.rows) || value.rows.length > 100) return false;
  return value.rows.every(row => isRecord(row)
    && typeof row.sequence === "string" && /^\d+$/.test(row.sequence)
    && typeof row.instanceId === "string" && /^\d+$/.test(row.instanceId)
    && typeof row.address === "string" && typeof row.rawValue === "string"
    && (row.quality === "source-reported" || row.quality === "unavailable")
    && typeof row.sourceTimestamp === "string"
    && typeof row.observedAt === "string" && typeof row.receivedAt === "string"
    && typeof row.mappingId === "string" && typeof row.batchId === "string"
    && typeof row.acknowledgementId === "string" && typeof row.documentHash === "string");
}
