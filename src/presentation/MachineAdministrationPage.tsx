import { useEffect, useMemo, useState } from "react";
import { readAllOperatorEvidenceMachines, type OperatorEvidenceMachine } from "../api/evidence/operator-evidence-machines-client.ts";

/**
 * Administrative inventory and registration require a current authorized session.
 * Evidence discovery is independently permission-scoped and may be incomplete.
 */
export function MachineAdministrationPage({ sessionBearer, baseAddress }: {
  readonly sessionBearer: string | null;
  readonly baseAddress: string;
}) {
  const [revision, setRevision] = useState(0);
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState<"site" | "line" | "machine">("site");
  const [siteId, setSiteId] = useState("");
  const [sites, setSites] = useState<readonly { siteId: string; displayName: string; isRetired: boolean }[]>([]);
  const [sitesError, setSitesError] = useState("");
  useEffect(() => {
    if (!sessionBearer) { setSites([]); return; }
    const controller = new AbortController();
    void fetch(new URL("/api/admin/v2/sites", baseAddress), {
      headers: { Authorization: "Bearer " + sessionBearer }, signal: controller.signal
    }).then(async response => {
      if (!response.ok) throw new Error("Site inventory unavailable.");
      const data: { sites: { siteId: string; displayName: string; isRetired: boolean }[] } = await response.json();
      if (!controller.signal.aborted) { setSites(data.sites); setSitesError(""); }
    }).catch(() => {
      if (!controller.signal.aborted) { setSites([]); setSitesError("Site selection unavailable for this account."); }
    });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);


  const [inventory, setInventory] = useState<readonly { machineId: string; siteId: string; displayName: string; isRetired: boolean }[] | null>(null);
  const [inventoryError, setInventoryError] = useState("");
  useEffect(() => {
    if (!sessionBearer) { setInventory(null); return; }
    const controller = new AbortController();
    void fetch(new URL("/api/admin/v2/machines", baseAddress), {
      headers: { Authorization: "Bearer " + sessionBearer }, signal: controller.signal
    }).then(async response => {
      if (!response.ok) throw new Error("Administrator machine inventory unavailable.");
      const data: { machines: { machineId: string; siteId: string; displayName: string; isRetired: boolean }[] } = await response.json();
      if (!controller.signal.aborted) { setInventory(data.machines); setInventoryError(""); }
    }).catch(() => {
      if (!controller.signal.aborted) { setInventory(null); setInventoryError("Full machine inventory requires administrator access."); }
    });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);
  const [name, setName] = useState("");
  const [registrationMessage, setRegistrationMessage] = useState("");
  const [saving, setSaving] = useState(false);
  async function register(event: { preventDefault(): void }) {
    event.preventDefault();
    if (!sessionBearer || saving) return;
    setSaving(true);
    setRegistrationMessage("");
    try {
      const response = await fetch(new URL("/api/admin/v2/master-registration", baseAddress), {
        method: "POST",
        headers: { Authorization: "Bearer " + sessionBearer, "Content-Type": "application/json" },
        body: JSON.stringify({ operationId: crypto.randomUUID(), kind,
          siteId: kind === "site" ? null : siteId.trim(), displayName: name.trim() })
      });
      if (!response.ok) throw new Error("Registration failed (HTTP " + response.status + ").");
      const result: { recordId: string } = await response.json();
      setRegistrationMessage("Registered " + kind + ": " + result.recordId + ". Acquisition remains inactive.");
      setName("");
      setRevision(value => value + 1);
    } catch (error) {
      setRegistrationMessage(error instanceof Error ? error.message : "Registration failed.");
    } finally { setSaving(false); }
  }

  const [state, setState] = useState<
    { kind: "loading" | "signIn" | "unavailable" } |
    { kind: "ready"; bearer: string; machines: readonly OperatorEvidenceMachine[]; complete: boolean }
  >({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    if (!sessionBearer) {
      setState({ kind: "signIn" });
      return () => controller.abort();
    }
    void readAllOperatorEvidenceMachines(baseAddress, sessionBearer, controller.signal)
      .then(result => {
        if (!controller.signal.aborted)
          setState({ kind: "ready", bearer: sessionBearer, machines: result.machines, complete: result.isComplete });
      })
      .catch(() => { if (!controller.signal.aborted) setState({ kind: "unavailable" }); });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);

  const current = state.kind === "ready" && state.bearer !== sessionBearer
    ? { kind: "signIn" as const } : state;
  const matches = useMemo(() => current.kind === "ready" ? current.machines.filter(machine =>
    machine.displayName.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()) ||
    machine.machineId.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()) ||
    machine.siteId.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) : [],
    [current, search]);

  return <section aria-labelledby="machine-administration-title">
    <h1 id="machine-administration-title">Machine administration</h1>
    <p>Authorized machine discovery — read only. This list includes only machines for which your session has
      commissioning evidence access. It is not the complete registered machine inventory or proof of live connectivity.</p>

    <p>Register master data below. Registration does not activate MTConnect acquisition.</p>
    {sessionBearer && <form onSubmit={event => { void register(event); }}>
      <h2>Register master record</h2>
      <label htmlFor="admin-record-kind">Record type</label>
      <select id="admin-record-kind" value={kind}
        onChange={event => setKind(event.currentTarget.value as "site" | "line" | "machine")}>
        <option value="site">Site</option><option value="line">Production line</option>
        <option value="machine">Machine</option>
      </select>
      {kind !== "site" && <>
        <label htmlFor="admin-site-id">Existing site ID</label>
        <select id="admin-site-id" required value={siteId}
          onChange={event => setSiteId(event.currentTarget.value)}>
          <option value="">Select an active site</option>
          {sites.filter(site => !site.isRetired).map(site =>
            <option key={site.siteId} value={site.siteId}>{site.displayName}</option>)}
        </select>
        {sitesError && <p role="alert">{sitesError}</p>}
      </>}
      <label htmlFor="admin-display-name">Display name</label>
      <input id="admin-display-name" required maxLength={200} value={name}
        onChange={event => setName(event.currentTarget.value)} />
      <button type="submit" disabled={saving || !name.trim()}>{saving ? "Registering…" : "Register"}</button>
      {registrationMessage && <p role="status">{registrationMessage}</p>}
    </form>}


    <h2>Registered machine inventory</h2>
    <p>This administrator inventory includes registered machines whether or not they have published evidence.</p>
    {inventoryError && <p role="status">{inventoryError}</p>}
    {inventory && <div className="commissioning-table-scroll" role="region" aria-label="Registered machine inventory" tabIndex={0}>
      <table><thead><tr><th>Machine</th><th>Machine ID</th><th>Site</th><th>Status</th></tr></thead>
      <tbody>{inventory.map(machine => <tr key={machine.machineId}>
        <td>{machine.displayName}</td><td><code>{machine.machineId}</code></td>
        <td><code>{machine.siteId}</code></td><td>{machine.isRetired ? "Retired" : "Registered"}</td>
      </tr>)}</tbody></table>
      {inventory.length === 0 && <p>No machines registered yet.</p>}
    </div>}
    <button type="button" disabled={!sessionBearer || current.kind === "loading"}
      onClick={() => setRevision(value => value + 1)}>Refresh authorized machines</button>
    {current.kind === "loading" && <p role="status">Checking authorized machines…</p>}
    {current.kind === "signIn" && <p role="alert">Sign in to view authorized machines. <a href="/api/security/v2/login">Sign in to FactoryConnect</a></p>}
    {current.kind === "unavailable" && <p role="alert">Machine discovery is unavailable. No cached inventory is shown.</p>}
    {current.kind === "ready" && <>
      {!current.complete && <p role="alert">The authorized machine list is incomplete. Counts below are partial.</p>}
      <label htmlFor="admin-machine-search">Search by machine, site or ID</label>
      <input id="admin-machine-search" type="search" value={search}
        onChange={event => setSearch(event.currentTarget.value)} />
      <p role="status">{matches.length} matching machines from {current.machines.length} returned.</p>
      <div className="commissioning-table-scroll" role="region" aria-label="Authorized machine directory" tabIndex={0}>
        <table><thead><tr><th>Machine name</th><th>Machine ID</th><th>Site ID</th><th>Evidence</th></tr></thead>
          <tbody>{matches.map(machine => <tr key={machine.machineId}>
            <td>{machine.displayName}</td><td><code>{machine.machineId}</code></td>
            <td><code>{machine.siteId}</code></td>
            <td><a href={`/operator-evidence?machineId=${encodeURIComponent(machine.machineId)}`}>View evidence</a></td>
          </tr>)}</tbody>
        </table>
      </div>
      {matches.length === 0 && <p>{current.machines.length === 0 ? "No machines are available to this account. This does not mean the factory has no registered machines." : "No authorized machines match the current search."}</p>}
    </>}
  </section>;
}
