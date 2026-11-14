import { useRef, useState } from "react";

/** Explicit, audited access changes. Enrollment does not grant permissions. */
export function HumanAccessAdministration({ sessionBearer, baseAddress }: {
  readonly sessionBearer: string | null;
  readonly baseAddress: string;
}) {
  const [subject, setSubject] = useState("");
  const [issuer, setIssuer] = useState("");
  const [principalId, setPrincipalId] = useState("");
  const [epoch, setEpoch] = useState("");
  const [reason, setReason] = useState("");
  const [parentGrantId, setParentGrantId] = useState("");
  const [siteId, setSiteId] = useState("");
  const [permission, setPermission] = useState("commissioning.read");
  const [effectiveUntil, setEffectiveUntil] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const pending = useRef<{ fingerprint: string; operationId: string; grantId?: string } | null>(null);

  async function submit(kind: "enroll" | "grant") {
    if (!sessionBearer || busy) return;
    const expectedEpoch = Number(epoch);
    if (!Number.isSafeInteger(expectedEpoch) || expectedEpoch < 0 || !reason.trim()) {
      setMessage("Enter the current nonnegative security authority epoch and a reason.");
      return;
    }
    if (!principalId.trim()) { setMessage("Enter the new human principal ID."); return; }
    if (kind === "grant" && (!Number.isFinite(Date.parse(effectiveUntil)) ||
        Date.parse(effectiveUntil) <= Date.now())) {
      setMessage("Enter a future grant expiry."); return;
    }
    const path = kind === "enroll" ? "/api/admin/v2/security/humans" : "/api/admin/v2/security/permission-grants";
    const fingerprint = JSON.stringify({ kind, subject, issuer, principalId, epoch, reason,
      parentGrantId, siteId, permission, effectiveUntil });
    if (pending.current?.fingerprint !== fingerprint)
      pending.current = { fingerprint, operationId: crypto.randomUUID(), grantId: kind === "grant" ? crypto.randomUUID() : undefined };
    const operation = pending.current;
    const body = kind === "enroll"
      ? { operationId: operation.operationId, principalId, issuer, subject, expectedEpoch, reason }
      : { operationId: operation.operationId, grantId: operation.grantId, granteeId: principalId,
          parentGrantId, permission, scopeKind: siteId.trim() ? "S" : "I",
          siteId: siteId.trim() || null, machineId: null, useAllowed: true, canDelegate: false,
          effectiveFrom: new Date().toISOString(), expiresAt: new Date(effectiveUntil).toISOString(),
          expectedEpoch, reason };
    setBusy(true);
    setMessage("");
    try {
      const response = await fetch(new URL(path, baseAddress), {
        method: "POST", headers: { Authorization: "Bearer " + sessionBearer,
          "Content-Type": "application/json" }, body: JSON.stringify(body)
      });
      if (!response.ok) throw new Error("Access operation rejected (HTTP " + response.status +
        "). Check the principal, parent grant, epoch and your authority; retry unchanged inputs to preserve operation ID.");
      setMessage(kind === "enroll"
        ? "Human identity enrolled. No permissions have been granted. Refresh the security epoch before delegating."
        : "Permission grant accepted. Sign in as the receiving administrator to verify access.");
      pending.current = null;
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Access operation failed.");
    } finally { setBusy(false); }
  }

  return <section aria-labelledby="human-access-admin-title">
    <h2 id="human-access-admin-title">User and permission administration</h2>
    <p>Register a separate Keycloak user, then delegate only the required permission.
      The signed-in administrator cannot grant access to themselves. Changes are audited.
      Do not use this screen to bypass production approval requirements.</p>
    <label htmlFor="access-principal">New human principal ID (new GUID)</label>
    <input id="access-principal" value={principalId} onChange={e => setPrincipalId(e.target.value)}
      placeholder="New principal GUID" />
    <button type="button" onClick={() => setPrincipalId(crypto.randomUUID())}>Generate principal ID</button>
    <label htmlFor="access-issuer">Keycloak issuer (must match installation trust)</label>
    <input id="access-issuer" value={issuer} onChange={e => setIssuer(e.target.value)} />
    <label htmlFor="access-subject">Other Keycloak user's subject</label>
    <input id="access-subject" value={subject} onChange={e => setSubject(e.target.value)} />
    <label htmlFor="access-epoch">Current security authority epoch</label>
    <input id="access-epoch" type="number" min="0" step="1" value={epoch}
      onChange={e => setEpoch(e.target.value)} />
    <label htmlFor="access-reason">Audit reason</label>
    <input id="access-reason" value={reason} onChange={e => setReason(e.target.value)} />
    <button type="button" disabled={!sessionBearer || busy || !subject.trim() || !issuer.trim()}
      onClick={() => void submit("enroll")}>Enroll separate human</button>
    <h3>Delegate permission to enrolled human</h3>
    <p>Refresh the authority epoch after enrollment. Use a valid delegable parent grant,
      never a self-grant. The selected scope and validity must be within the parent grant.</p>
    <label htmlFor="access-parent">Delegable parent grant ID</label>
    <input id="access-parent" value={parentGrantId} onChange={e => setParentGrantId(e.target.value)} />
    <label htmlFor="access-permission">Permission</label>
    <input id="access-permission" value={permission} onChange={e => setPermission(e.target.value)} />
    <label htmlFor="access-site">Site ID (blank = installation-wide)</label>
    <input id="access-site" value={siteId} onChange={e => setSiteId(e.target.value)} />
    <label htmlFor="access-until">Grant expires at (local time)</label>
    <input id="access-until" type="datetime-local" value={effectiveUntil}
      onChange={e => setEffectiveUntil(e.target.value)} />
    <button type="button" disabled={!sessionBearer || busy || !parentGrantId.trim() || !effectiveUntil}
      onClick={() => void submit("grant")}>Delegate permission</button>
    {message && <p role="status">{message}</p>}
  </section>;
}
