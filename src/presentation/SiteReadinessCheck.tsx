import { useState } from "react";

/** A commissioning-only human may check a known site without site-inventory privileges. */
export function SiteReadinessCheck({ sessionBearer, baseAddress }: {
  readonly sessionBearer: string | null;
  readonly baseAddress: string;
}) {
  const [siteId, setSiteId] = useState("");
  const [status, setStatus] = useState("");
  const [machines, setMachines] = useState<readonly { machineId: string; status: string; reason: string }[]>([]);
  const [loading, setLoading] = useState(false);

  async function check() {
    if (!sessionBearer || loading) return;
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(siteId.trim())) {
      setStatus("Enter a valid site ID."); return;
    }
    setLoading(true);
    setMachines([]);
    setStatus("");
    try {
      const response = await fetch(new URL("/api/admin/v2/sites/" + encodeURIComponent(siteId.trim()) +
        "/activation-readiness", baseAddress), {
        headers: { Authorization: "Bearer " + sessionBearer }, cache: "no-store"
      });
      if (!response.ok) {
        setStatus("Activation readiness: HTTP " + response.status +
          (response.status === 403 ? " — commissioning.read is not authorized for this site." : "."));
        return;
      }
      const result: { machines?: { machineId: string; status: string; reason: string }[] } = await response.json();
      setMachines(result.machines ?? []);
      setStatus("Activation readiness: HTTP 200. " + (result.machines?.length ?? 0) + " machine records.");
    } catch {
      setStatus("Activation readiness request failed. Check API connectivity.");
    } finally { setLoading(false); }
  }

  return <section aria-labelledby="site-readiness-title">
    <h2 id="site-readiness-title">Check commissioning readiness by site</h2>
    <p>For commissioning users without machine inventory access. Enter an authorized site ID.
      This read-only check does not activate a machine.</p>
    <label htmlFor="commissioning-site-id">Site ID</label>
    <input id="commissioning-site-id" value={siteId} onChange={e => setSiteId(e.target.value)}
      placeholder="Site GUID" />
    <button type="button" disabled={!sessionBearer || loading} onClick={() => void check()}>
      {loading ? "Checking…" : "Check activation readiness"}
    </button>
    {status && <p role="status">{status}</p>}
    {machines.length > 0 && <table><thead><tr><th>Machine ID</th><th>Status</th><th>Reason</th></tr></thead>
      <tbody>{machines.map(m => <tr key={m.machineId}><td>{m.machineId}</td>
        <td>{m.status}</td><td>{m.reason}</td></tr>)}</tbody></table>}
  </section>;
}
