export interface OperatorEvidenceMachine {
  readonly siteId: string;
  readonly machineId: string;
  readonly displayName: string;
}
export class OperatorMachineReadFailure extends Error {
  constructor(readonly status: number) { super(status === 401 ? "Operator session denied." : "Operator machine selection unavailable."); }
}
export async function readOperatorEvidenceMachinePage(
  baseAddress: string, bearer: string, signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch, cursor?: string,
): Promise<{ readonly machines: readonly OperatorEvidenceMachine[]; readonly isComplete: boolean; readonly nextCursor: string | null }> {
  const endpoint = new URL("api/commissioning/v2/evidence-machines", baseAddress);
  if (cursor) endpoint.searchParams.set("cursor", cursor);
  if (endpoint.protocol !== "https:" || (typeof window !== "undefined" && endpoint.origin !== window.location.origin))
    throw new OperatorMachineReadFailure(403);
  const response = await fetchImplementation(endpoint, {
    method: "GET", headers: { Accept: "application/json", Authorization: `Bearer ${bearer}` },
    cache: "no-store", redirect: "error", ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new OperatorMachineReadFailure(response.status);
  const payload: unknown = await response.json();
  if (payload === null || typeof payload !== "object" || !("machines" in payload)
    || !Array.isArray(payload.machines) || payload.machines.length > 100
    || !payload.machines.every((m: unknown) => m !== null && typeof m === "object"
      && "machineId" in m && typeof m.machineId === "string"
      && "siteId" in m && typeof m.siteId === "string"
      && "displayName" in m && typeof m.displayName === "string"))
    throw new OperatorMachineReadFailure(502);
  if (!("isComplete" in payload) || typeof payload.isComplete !== "boolean"
    || !("continuationSupported" in payload) || typeof payload.continuationSupported !== "boolean"
    || !("maximumReturned" in payload) || payload.maximumReturned !== 100
    || response.headers.get("X-Result-Complete") !== String(payload.isComplete))
    throw new OperatorMachineReadFailure(502);
  const nextCursor = "nextCursor" in payload ? payload.nextCursor : null;
  if ((nextCursor !== null && (typeof nextCursor !== "string" || nextCursor.length === 0 || nextCursor.length > 1024))
    || (payload.isComplete && nextCursor !== null)
    || (!payload.isComplete && payload.continuationSupported && nextCursor === null)
    || (!payload.isComplete && nextCursor !== null && payload.machines.length === 0)
    || (cursor && !payload.continuationSupported))
    throw new OperatorMachineReadFailure(502);
  return { machines: payload.machines as readonly OperatorEvidenceMachine[],
    isComplete: payload.isComplete, nextCursor: nextCursor as string | null };
}

/** Compatibility reader; callers needing fleet completeness must use the page API. */
export async function readOperatorEvidenceMachines(
  baseAddress: string, bearer: string, signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch,
): Promise<readonly OperatorEvidenceMachine[]> {
  return (await readOperatorEvidenceMachinePage(baseAddress, bearer, signal, fetchImplementation)).machines;
}

/** Collect signed pages without ever treating a partial fleet as complete.
 * Revocation or inconsistent pagination fails the entire read closed. */
export async function readAllOperatorEvidenceMachines(
  baseAddress: string, bearer: string, signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch,
): Promise<{ readonly machines: readonly OperatorEvidenceMachine[]; readonly isComplete: boolean }> {
  const machines: OperatorEvidenceMachine[] = [];
  const ids = new Set<string>();
  const cursors = new Set<string>();
  let cursor: string | undefined;
  for (let pageNumber = 0; pageNumber < 100; pageNumber++) {
    const page = await readOperatorEvidenceMachinePage(baseAddress, bearer, signal, fetchImplementation, cursor);
    for (const machine of page.machines) {
      if (ids.has(machine.machineId)) throw new OperatorMachineReadFailure(502);
      ids.add(machine.machineId);
      machines.push(machine);
    }
    if (page.isComplete) return { machines, isComplete: true };
    if (page.nextCursor === null) return { machines, isComplete: false };
    if (cursors.has(page.nextCursor)) throw new OperatorMachineReadFailure(502);
    cursors.add(page.nextCursor);
    cursor = page.nextCursor;
  }
  throw new OperatorMachineReadFailure(502);
}
