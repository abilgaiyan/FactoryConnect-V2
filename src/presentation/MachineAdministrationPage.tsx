import { useEffect, useMemo, useRef, useState } from "react";
import { readAllOperatorEvidenceMachines, type OperatorEvidenceMachine } from "../api/evidence/operator-evidence-machines-client.ts";

/**
 * Administrative inventory and registration require a current authorized session.
 * Evidence discovery is independently permission-scoped and may be incomplete.
 */
export function MachineAdministrationPage({ sessionBearer, baseAddress }: {
  readonly sessionBearer: string | null;
  readonly baseAddress: string;
}) {
  const [probeDevices, setProbeDevices] = useState<readonly { id: string; name: string; uuid: string; dataItems: number }[]>([]);
  const [probeError, setProbeError] = useState("");
  async function previewProbeFile(file: File | undefined) {
    setProbeDevices([]);
    setProbeError("");
    if (!file) return;
    if (file.size > 4_000_000) { setProbeError("Devices XML must be smaller than 4 MB."); return; }
    try {
      const source = await file.text();
      if (/<!DOCTYPE|<!ENTITY/i.test(source))
        throw new Error("XML DTD and entity declarations are not allowed.");
      const xml = new DOMParser().parseFromString(source, "application/xml");
      if (xml.querySelector("parsererror") || xml.documentElement.localName !== "MTConnectDevices" ||
          xml.doctype)
        throw new Error("Invalid MTConnect Devices XML.");
      const devices = Array.from(xml.getElementsByTagName("*")).filter(node => node.localName === "Device");
      if (devices.length > 500) throw new Error("Too many devices.");
      const ids = new Set<string>();
      const uuids = new Set<string>();
      const found = devices.map(device => {
        const id = device.getAttribute("id")?.trim() ?? "";
        const name = device.getAttribute("name")?.trim() ?? "";
        const uuid = device.getAttribute("uuid")?.trim() ?? "";
        if (!id || !name || ids.has(id) || (uuid && uuids.has(uuid)))
          throw new Error("Missing or duplicate device identity.");
        ids.add(id);
        if (uuid) uuids.add(uuid);
        const dataItems = Array.from(device.getElementsByTagName("*")).filter(node => node.localName === "DataItem");
        if (dataItems.length > 5000) throw new Error("Too many data items.");
        const dataItemIds = new Set<string>();
        for (const item of dataItems) {
          const dataItemId = item.getAttribute("id")?.trim() ?? "";
          if (!dataItemId || dataItemIds.has(dataItemId))
            throw new Error("Missing or duplicate MTConnect data item ID.");
          dataItemIds.add(dataItemId);
        }
        return { id, name, uuid, dataItems: dataItems.length };
      });
      setProbeDevices(found);
    } catch (error) {
      setProbeError(error instanceof Error ? error.message : "Unable to read MTConnect Devices XML.");
    }
  }
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
  function exportMachineInventory() {
    if (!inventory) return;
    const quote = (value: string) => '"' + value.replaceAll('"', '""') + '"';
    const rows = [
      ["Machine ID", "Machine", "Site ID", "Site", "Status"],
      ...visibleInventory.map(machine => [
        machine.machineId, machine.displayName, machine.siteId,
        sites.find(site => site.siteId === machine.siteId)?.displayName ?? "",
        machine.isRetired ? "Retired" : "Registered"
      ])
    ];
    const csv = "\uFEFF" + rows.map(row => row.map(quote).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "factoryconnect-machine-inventory.csv";
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }
  const [inventorySearch, setInventorySearch] = useState("");
  const [inventorySite, setInventorySite] = useState("");
  const [includeRetired, setIncludeRetired] = useState(false);
  const visibleInventory = useMemo(() => (inventory ?? []).filter(machine => {
    if (!includeRetired && machine.isRetired) return false;
    if (inventorySite && machine.siteId !== inventorySite) return false;
    const term = inventorySearch.trim().toLocaleLowerCase();
    return !term || machine.displayName.toLocaleLowerCase().includes(term) ||
      machine.machineId.toLocaleLowerCase().includes(term) ||
      (sites.find(site => site.siteId === machine.siteId)?.displayName ?? "").toLocaleLowerCase().includes(term);
  }), [inventory, inventorySearch, inventorySite, includeRetired, sites]);
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

  const [lineSiteFilter, setLineSiteFilter] = useState("");
  const [showRetiredLines, setShowRetiredLines] = useState(false);
  const [lines, setLines] = useState<readonly { productionLineId: string; siteId: string; displayName: string; isRetired: boolean }[] | null>(null);
  const visibleLines = useMemo(() => (lines ?? []).filter(line =>
    (showRetiredLines || !line.isRetired) && (!lineSiteFilter || line.siteId === lineSiteFilter)
  ), [lines, lineSiteFilter, showRetiredLines]);
  useEffect(() => {
    if (!sessionBearer) { setLines(null); return; }
    const controller = new AbortController();
    void fetch(new URL("/api/admin/v2/lines", baseAddress), {
      headers: { Authorization: "Bearer " + sessionBearer }, signal: controller.signal
    }).then(async response => {
      if (!response.ok) throw new Error("Production-line inventory unavailable.");
      const data: { lines: { productionLineId: string; siteId: string; displayName: string; isRetired: boolean }[] } = await response.json();
      if (!controller.signal.aborted) setLines(data.lines);
    }).catch(() => { if (!controller.signal.aborted) setLines(null); });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);

  const [name, setName] = useState("");
  const [registrationMessage, setRegistrationMessage] = useState("");
  const [saving, setSaving] = useState(false);
  const pendingRegistration = useRef<{ fingerprint: string; operationId: string } | null>(null);
  async function register(event: { preventDefault(): void }) {
    event.preventDefault();
    if (!sessionBearer || saving) return;
    const fingerprint = JSON.stringify({ sessionBearer, kind,
      siteId: kind === "site" ? null : siteId.trim(), displayName: name.trim() });
    if (pendingRegistration.current?.fingerprint !== fingerprint)
      pendingRegistration.current = { fingerprint, operationId: crypto.randomUUID() };
    const operationId = pendingRegistration.current.operationId;
    setSaving(true);
    setRegistrationMessage("");
    try {
      const response = await fetch(new URL("/api/admin/v2/master-registration", baseAddress), {
        method: "POST",
        headers: { Authorization: "Bearer " + sessionBearer, "Content-Type": "application/json" },
        body: JSON.stringify({ operationId, kind,
          siteId: kind === "site" ? null : siteId.trim(), displayName: name.trim() })
      });
      if (!response.ok) {
        if (response.status === 409) {
          let detail = "";
          try {
            const body: { error?: string } = await response.json();
            detail = body.error ?? "";
          } catch { /* The status alone is sufficient. */ }
          if (detail === "Operation ID conflict")
            throw new Error("This registration retry conflicts with a previous request. Change the details to start a new registration.");
          throw new Error("A record with this name already exists, or its selected site is no longer active. Review the inventory before retrying.");
        }
        if (response.status === 401 || response.status === 403)
          throw new Error("Your session cannot register master data. Sign in with an administrator account that has master-data management access.");
        if (response.status === 400)
          throw new Error("Invalid registration details. Check the name and selected site.");
        throw new Error("Registration could not be confirmed (HTTP " + response.status + "). Retry the unchanged request to avoid duplicates.");
      }
      const result: { recordId: string } = await response.json();
      setRegistrationMessage("Registered " + kind + ": " + result.recordId + ". Acquisition remains inactive.");
      pendingRegistration.current = null;
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
        <label htmlFor="admin-site-id">Site</label>
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
      <button type="submit" disabled={saving || !name.trim() || (kind !== "site" && (!siteId || !!sitesError))}>{saving ? "Registering…" : "Register"}</button>
      {registrationMessage && <p role="status">{registrationMessage}</p>}
    </form>}



    <h2>Registered sites</h2>
    {sitesError && <p role="alert">{sitesError}</p>}
    {!sitesError && <div role="region" aria-label="Registered sites" tabIndex={0}>
      <table><thead><tr><th>Site</th><th>Site ID</th><th>Status</th></tr></thead>
      <tbody>{sites.map(site => <tr key={site.siteId}>
        <td>{site.displayName}</td><td><code>{site.siteId}</code></td>
        <td>{site.isRetired ? "Retired" : "Registered"}</td>
      </tr>)}</tbody></table>
      {sites.length === 0 && <p>No sites available to this administrator.</p>}
    </div>}
    <h2>Registered production lines</h2>
    <label htmlFor="admin-line-site-filter">Production-line site</label>
    <select id="admin-line-site-filter" value={lineSiteFilter} onChange={event => setLineSiteFilter(event.currentTarget.value)}>
      <option value="">All sites</option>
      {sites.map(site => <option key={site.siteId} value={site.siteId}>{site.displayName}</option>)}
    </select>
    <label htmlFor="admin-retired-lines">
      <input id="admin-retired-lines" type="checkbox" checked={showRetiredLines}
        onChange={event => setShowRetiredLines(event.currentTarget.checked)} /> Include retired lines
    </label>
    {lines && <div role="region" aria-label="Registered production lines" tabIndex={0}>
      <table><thead><tr><th>Production line</th><th>Site</th><th>Status</th></tr></thead>
      <tbody>{visibleLines.map(line => <tr key={line.productionLineId}>
        <td>{line.displayName}</td>
        <td>{sites.find(site => site.siteId === line.siteId)?.displayName ?? line.siteId}</td>
        <td>{line.isRetired ? "Retired" : "Registered"}</td>
      </tr>)}</tbody></table>
      {lines.length === 0 && <p>No production lines registered yet.</p>}
      {lines.length > 0 && visibleLines.length === 0 && <p>No production lines match the current filters.</p>}
    </div>}
    <h2>Preview MTConnect devices.xml</h2>
    <p>Read-only commissioning preview: choose a Devices XML file exported from an existing MTConnect Agent /probe endpoint. This does not save URLs, register machines or activate acquisition.</p>
    <label htmlFor="admin-probe-file">MTConnect Devices XML</label>
    <input id="admin-probe-file" type="file" accept=".xml,text/xml,application/xml"
      onChange={event => void previewProbeFile(event.currentTarget.files?.[0])} />
    {probeError && <p role="alert">{probeError}</p>}
    {probeDevices.length > 0 && <div className="commissioning-table-scroll" role="region" aria-label="MTConnect probe preview" tabIndex={0}>
      <table><thead><tr><th>Device</th><th>Device ID</th><th>UUID</th><th>Data items</th><th>Inventory comparison</th><th>Action</th></tr></thead>
      <tbody>{probeDevices.map(device => <tr key={device.id}>
        <td>{device.name}</td><td><code>{device.id}</code></td><td><code>{device.uuid || "Not supplied"}</code></td>
        <td>{device.dataItems}</td>
        <td>{(inventory ?? []).some(machine => machine.displayName.toLocaleLowerCase() === device.name.toLocaleLowerCase())
          ? "Name already in inventory — verify identity manually"
          : "Not matched by name"}</td>
        <td><button type="button" disabled={!sessionBearer || !inventory || sites.filter(site => !site.isRetired).length === 0}
          onClick={() => { setKind("machine"); setName(device.name); setRegistrationMessage(
            "Device name copied into machine registration. Choose a site and review identity before submitting. Device ID/UUID is not yet linked."); }}>
          Prepare registration</button></td>
      </tr>)}</tbody></table>
    </div>}
    <h2>Registered machine inventory</h2>
    <button type="button" disabled={!inventory || visibleInventory.length === 0}
      onClick={exportMachineInventory}>Export filtered machines (CSV)</button>
    <p>This administrator inventory includes registered machines whether or not they have published evidence. Registration does not establish an MTConnect connection, prove acquisition is running, or activate a machine. Acquisition status is not yet verified by this screen.</p>
    <label htmlFor="admin-machine-search">Search registered machines</label>
    <input id="admin-machine-search" value={inventorySearch} onChange={event => setInventorySearch(event.currentTarget.value)}
      placeholder="Machine name or ID" />
    <label htmlFor="admin-machine-site-filter">Filter by site</label>
    <select id="admin-machine-site-filter" value={inventorySite} onChange={event => setInventorySite(event.currentTarget.value)}>
      <option value="">All sites</option>
      {sites.map(site => <option key={site.siteId} value={site.siteId}>{site.displayName}</option>)}
    </select>
    <label htmlFor="admin-include-retired">
      <input id="admin-include-retired" type="checkbox" checked={includeRetired}
        onChange={event => setIncludeRetired(event.currentTarget.checked)} /> Include retired machines
    </label>
    {inventoryError && <p role="status">{inventoryError}</p>}
    {inventory && <div className="commissioning-table-scroll" role="region" aria-label="Registered machine inventory" tabIndex={0}>
      <table><thead><tr><th>Machine</th><th>Machine ID</th><th>Site</th><th>Registration</th><th>Acquisition</th></tr></thead>
      <tbody>{visibleInventory.map(machine => <tr key={machine.machineId}>
        <td>{machine.displayName}</td><td><code>{machine.machineId}</code></td>
        <td>{sites.find(site => site.siteId === machine.siteId)?.displayName ?? machine.siteId}</td><td>{machine.isRetired ? "Retired" : "Registered"}</td><td>Not verified — configure connection and activate separately</td>
      </tr>)}</tbody></table>
      {inventory.length === 0 && <p>No machines registered yet.</p>}
      {inventory.length > 0 && visibleInventory.length === 0 && <p>No machines match the current filters.</p>}
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
