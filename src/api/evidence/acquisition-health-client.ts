import { EvidenceReadFailure } from "./retained-evidence-client.ts";

export interface AcquisitionHealthResult {
  machineId: string;
  siteId: string;
  evidenceKind: "local-acquisition-host-telemetry";
  currentConnectivity: null;
  liveMachineState: null;
  health: {
    status: string;
    operational: boolean;
    writtenAt?: string;
    ageSeconds?: number;
    reason?: string;
    snapshot?: { status: string; lastSourceResponseAt?: string | null; lastDurableCommitAt?: string | null };
  };
}
const record = (x: unknown): x is Record<string, unknown> =>
  typeof x === "object" && x !== null && !Array.isArray(x);
const guid = (x: unknown): x is string => typeof x === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(x);
const utc = (x: unknown): x is string => typeof x === "string" &&
  /(?:Z|\+00:00)$/.test(x) && Number.isFinite(Date.parse(x));
export async function readAcquisitionHealth(base: string, machineId: string, bearer: string,
  signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<AcquisitionHealthResult> {
  const url = new URL(`api/commissioning/v2/machines/${encodeURIComponent(machineId)}/acquisition-health`, base);
  if (url.protocol !== "https:" || (typeof window !== "undefined" && url.origin !== window.location.origin))
    throw new EvidenceReadFailure(403);
  const response = await fetcher(url, {
    method: "GET", headers: { Accept: "application/json", Authorization: `Bearer ${bearer}` },
    cache: "no-store", redirect: "error", ...(signal ? { signal } : {})
  });
  if (!response.ok) throw new EvidenceReadFailure(response.status);
  const body: unknown = await response.json();
  if (!record(body) || body.machineId !== machineId || !guid(body.siteId) ||
    body.evidenceKind !== "local-acquisition-host-telemetry" ||
    body.currentConnectivity !== null || body.liveMachineState !== null || !record(body.health))
    throw new EvidenceReadFailure(502);
  const h = body.health;
  if (typeof h.status !== "string" || h.status.length > 64 || typeof h.operational !== "boolean" ||
    (h.writtenAt !== undefined && !utc(h.writtenAt)) ||
    (h.ageSeconds !== undefined && (typeof h.ageSeconds !== "number" || !Number.isSafeInteger(h.ageSeconds) || h.ageSeconds < 0)) ||
    (h.reason !== undefined && (typeof h.reason !== "string" || h.reason.length > 256)) ||
    (h.snapshot !== undefined && (!record(h.snapshot) || h.snapshot.status !== h.status ||
      (h.snapshot.lastSourceResponseAt != null && !utc(h.snapshot.lastSourceResponseAt)) ||
      (h.snapshot.lastDurableCommitAt != null && !utc(h.snapshot.lastDurableCommitAt)))))
    throw new EvidenceReadFailure(502);
  return body as unknown as AcquisitionHealthResult;
}
