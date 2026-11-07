export interface OperatorEvidenceMachine {
  readonly siteId: string;
  readonly machineId: string;
  readonly displayName: string;
}
export class OperatorMachineReadFailure extends Error {
  constructor(readonly status: number) { super(status === 401 ? "Operator session denied." : "Operator machine selection unavailable."); }
}
export async function readOperatorEvidenceMachines(
  baseAddress: string, bearer: string, signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch,
): Promise<readonly OperatorEvidenceMachine[]> {
  const endpoint = new URL("api/commissioning/v2/evidence-machines", baseAddress);
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
  return payload.machines as readonly OperatorEvidenceMachine[];
}
