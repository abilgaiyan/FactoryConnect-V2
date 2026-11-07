export interface HumanSession {
  readonly bearer: string;
  readonly principalId: string;
  readonly expiresAt: string;
}

export class HumanSessionFailure extends Error {
  constructor(readonly status: number) {
    super(status === 401 ? "Identity proof rejected."
      : status === 503 ? "Verified identity service not configured or unavailable."
      : "Human session issuance failed.");
  }
}

/** Accepts an opaque, one-use external identity assertion from the trusted login adapter.
 * Never stores the assertion or bearer in persistent browser storage. */
export async function issueHumanSession(
  baseAddress: string,
  assertion: string,
  signal?: AbortSignal,
  fetchImplementation: typeof fetch = fetch,
): Promise<HumanSession> {
  if (!baseAddress.startsWith("https://")) throw new Error("Human session issuance requires HTTPS.");
  if (assertion.length < 1 || assertion.length > 65536) throw new Error("Invalid assertion length.");
  const response = await fetchImplementation(
    new URL("api/security/v2/human-sessions", baseAddress),
    { method: "POST", headers: { "Content-Type": "text/plain; charset=utf-8", Accept: "application/json" },
      body: assertion, cache: "no-store", redirect: "error", ...(signal ? { signal } : {}) },
  );
  if (!response.ok) throw new HumanSessionFailure(response.status);
  const body: unknown = await response.json();
  if (!isRecord(body) || typeof body.bearer !== "string"
    || typeof body.principalId !== "string" || typeof body.expiresAt !== "string"
    || !isBase64Token(body.bearer)) throw new HumanSessionFailure(502);
  return { bearer: body.bearer, principalId: body.principalId, expiresAt: body.expiresAt };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isBase64Token(value: string): boolean {
  return /^[A-Za-z0-9+/]{43}=$/.test(value);
}
