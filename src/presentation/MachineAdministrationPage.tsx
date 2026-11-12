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
  const [probeDevices, setProbeDevices] = useState<readonly { id: string; name: string; uuid: string; dataItems: readonly { id: string; name: string; type: string; subType: string }[] }[]>([]);
  const [probeError, setProbeError] = useState("");

  const [edgeConfigured, setEdgeConfigured] = useState<readonly { machineId: string; deviceKey: string; baseUri: string }[]>([]);
  const [edgeConfigError, setEdgeConfigError] = useState("");

  const [liveDiscoveryMachineId, setLiveDiscoveryMachineId] = useState("");
  const [liveDiscoveryBusy, setLiveDiscoveryBusy] = useState(false);
  const [liveDiscoveryMessage, setLiveDiscoveryMessage] = useState("");
  const [discoverySourceMachineId, setDiscoverySourceMachineId] = useState("");
  const [discoveryIdentityStatus, setDiscoveryIdentityStatus] = useState("");
  const [discoveryMatchedIds, setDiscoveryMatchedIds] = useState<readonly string[]>([]);
  const [commissioningLineId, setCommissioningLineId] = useState("");
  const [commissioningDeviceId, setCommissioningDeviceId] = useState("");
  function exportCommissioningReview() {
    const machine = inventory?.find(item => item.machineId === discoverySourceMachineId);
    const device = probeDevices.find(item => item.id === commissioningDeviceId);
    const line = lines?.find(item => item.productionLineId === commissioningLineId);
    if (!machine || !device || !line || line.siteId !== machine.siteId ||
        machine.isRetired || line.isRetired) return;
    const review = {
      format: "factoryconnect-v2-mtconnect-commissioning-review-v1",
      status: "review-only-not-approved",
      acquisitionActivated: false,
      machineId: machine.machineId,
      machineName: machine.displayName,
      siteId: machine.siteId,
      productionLineId: line.productionLineId,
      productionLineName: line.displayName,
      device: { id: device.id, name: device.name, uuid: device.uuid },
      configuredIdentityStatus: discoveryIdentityStatus,
      configuredIdentityMatches: discoveryMatchedIds,
      dataItems: device.dataItems
    };
    const url = URL.createObjectURL(new Blob([JSON.stringify(review, null, 2)], {
      type: "application/json"
    }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "factoryconnect-commissioning-review-" + machine.machineId + ".json";
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  async function discoverConfiguredMachine() {
    if (!sessionBearer || !liveDiscoveryMachineId || liveDiscoveryBusy) return;
    setLiveDiscoveryBusy(true);
    setLiveDiscoveryMessage("");
    try {
      const response = await fetch(new URL("/api/admin/v2/mtconnect/discover/" +
        encodeURIComponent(liveDiscoveryMachineId), baseAddress), {
        headers: { Authorization: "Bearer " + sessionBearer }
      });
      if (!response.ok) {
        if (response.status === 404)
          throw new Error("This machine is not configured in the API host's existing Edge MTConnect settings.");
        if (response.status === 401 || response.status === 403)
          throw new Error("Administrator authorization is required for live discovery.");
        throw new Error("Agent discovery failed (HTTP " + response.status + ").");
      }
      const payload: { identityStatus: string; matchingDeviceIds: readonly string[]; devices: readonly {
        id: string; name: string; uuid?: string | null;
        dataItems: readonly { id: string; name?: string | null; type?: string | null; subType?: string | null }[]
      }[] } = await response.json();
      const discovered = payload.devices.map(device => ({
        id: device.id, name: device.name, uuid: device.uuid ?? "",
        dataItems: device.dataItems.map(item => ({
          id: item.id, name: item.name ?? "", type: item.type ?? "", subType: item.subType ?? ""
        }))
      }));
      setProbeDevices(discovered);
      setDiscoverySourceMachineId(liveDiscoveryMachineId);
      setDiscoveryIdentityStatus(payload.identityStatus);
      setDiscoveryMatchedIds(payload.matchingDeviceIds);
      setCommissioningDeviceId(payload.matchingDeviceIds.length === 1 ? (payload.matchingDeviceIds[0] ?? "") : "");
      setCommissioningLineId("");
      setSelectedProbeIds([]);
      setExpandedProbeId(null);
      setProbeError("");
      setLiveDiscoveryMessage("Read-only Agent probe completed: " + discovered.length +
        " devices. Configured DeviceKey identity: " + payload.identityStatus +
        " (" + payload.matchingDeviceIds.length + " matches). " +
        (payload.identityStatus === "potential-match"
          ? "Verify the matching device before commissioning."
          : "Identity requires administrator review; no device mapping was saved."));
    } catch (error) {
      setLiveDiscoveryMessage(error instanceof Error ? error.message : "Agent discovery unavailable.");
    } finally { setLiveDiscoveryBusy(false); }
  }



  async function previewEdgeConfig(file: File | undefined) {
    setEdgeConfigured([]);
    setEdgeConfigError("");
    if (!file) return;
    if (file.size > 2_000_000) { setEdgeConfigError("Edge configuration exceeds 2 MB."); return; }
    try {
      const parsed: unknown = JSON.parse(await file.text());
      if (!parsed || typeof parsed !== "object") throw new Error("Invalid Edge configuration.");
      const root = parsed as Record<string, unknown>;
      const mtconnect = root.MTConnect;
      if (!mtconnect || typeof mtconnect !== "object") throw new Error("MTConnect configuration missing.");
      const machines = (mtconnect as Record<string, unknown>).Machines;
      if (!Array.isArray(machines) || machines.length > 500) throw new Error("Invalid MTConnect machines list.");
      const identities = new Set<string>();
      const configured = machines.map((entry: unknown) => {
        if (!entry || typeof entry !== "object") throw new Error("Invalid configured machine.");
        const value = entry as Record<string, unknown>;
        const machineId = value.MachineId;
        const deviceKey = value.DeviceKey;
        const baseUri = value.BaseUri;
        if (typeof machineId !== "string" || typeof deviceKey !== "string" ||
            typeof baseUri !== "string" || !machineId.trim() || !deviceKey.trim() ||
            !/^https?:\/\//i.test(baseUri))
          throw new Error("Configured machine is missing MachineId, DeviceKey or BaseUri.");
        const identity = machineId.toLowerCase();
        if (identities.has(identity)) throw new Error("Duplicate configured MachineId.");
        identities.add(identity);
        return { machineId, deviceKey, baseUri };
      });
      setEdgeConfigured(configured);
    } catch (error) {
      setEdgeConfigError(error instanceof Error ? error.message : "Cannot read Edge configuration.");
    }
  }

  const [expandedProbeId, setExpandedProbeId] = useState<string | null>(null);
  const [selectedProbeIds, setSelectedProbeIds] = useState<readonly string[]>([]);
  function exportProbeSelection() {
    const selected = probeDevices.filter(device => selectedProbeIds.includes(device.id));
    if (!selected.length) return;
    const quote = (value: string) => '"' + value.replaceAll('"', '""') + '"';
    const rows = [
      ["MTConnect Device ID", "MTConnect Device UUID", "Device Name", "Data Item Count", "Commissioning Status"],
      ...selected.map(device => [device.id, device.uuid, device.name, String(device.dataItems.length), "Review required"])
    ];
    const csv = "\uFEFF" + rows.map(row => row.map(quote).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "factoryconnect-mtconnect-discovered-devices.csv";
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  async function previewProbeFile(file: File | undefined) {
    setProbeDevices([]);
    setDiscoverySourceMachineId("");
    setDiscoveryIdentityStatus("");
    setDiscoveryMatchedIds([]);
    setCommissioningDeviceId("");
    setCommissioningLineId("");
    setSelectedProbeIds([]);
    setExpandedProbeId(null);
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
        return { id, name, uuid, dataItems: dataItems.map(item => ({
          id: item.getAttribute("id") ?? "", name: item.getAttribute("name") ?? "",
          type: item.getAttribute("type") ?? "", subType: item.getAttribute("subType") ?? ""
        })) };
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
  const [configuredMachines, setConfiguredMachines] = useState<readonly {
    machineId: string; deviceKey: string; registered: boolean
  }[] | null>(null);
  const [configuredError, setConfiguredError] = useState("");
  useEffect(() => {
    if (!sessionBearer) { setConfiguredMachines(null); return; }
    const controller = new AbortController();
    void fetch(new URL("/api/admin/v2/mtconnect/configured-machines", baseAddress), {
      headers: { Authorization: "Bearer " + sessionBearer }, signal: controller.signal
    }).then(async response => {
      if (!response.ok) throw new Error("Configured Edge machine inventory unavailable.");
      const data: { configured: { machineId: string; deviceKey: string; registered: boolean }[] } =
        await response.json();
      if (!controller.signal.aborted) { setConfiguredMachines(data.configured); setConfiguredError(""); }
    }).catch(() => {
      if (!controller.signal.aborted) {
        setConfiguredMachines(null);
        setConfiguredError("Edge configuration status unavailable.");
      }
    });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);

  const probeEdgeComparison = useMemo(() => probeDevices.map(device => {
    const matches = edgeConfigured.filter(configured =>
      configured.deviceKey === device.name || configured.deviceKey === device.id ||
      (device.uuid !== "" && configured.deviceKey === device.uuid));
    const configuredIds = new Set(matches.map(match => match.machineId.toLowerCase()));
    const duplicateDeviceKey = matches.length > 1;
    const registered = matches.length === 1 && inventory?.find(machine =>
      machine.machineId.toLowerCase() === matches[0]?.machineId.toLowerCase());
    return { device, matches, duplicateDeviceKey, registered,
      identityConflict: configuredIds.size > 1 || duplicateDeviceKey };
  }), [probeDevices, edgeConfigured, inventory]);

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
    <h2>Existing Edge configuration readiness</h2>
    {configuredError && <p role="status">{configuredError}</p>}
    {configuredMachines !== null && <p role="status">
      {configuredMachines.length} Edge machine entries loaded by API;
      {" "}{configuredMachines.filter(machine => machine.registered).length} registered and active in V2.
    </p>}
    {configuredMachines !== null && configuredMachines.length > 0 && <ul>
      {configuredMachines.map(machine => <li key={machine.machineId}>
        {machine.deviceKey} — {machine.registered ? "Registered in V2" : "Registration required"}
        {" "}({machine.machineId})
        {machine.registered && <button type="button"
          disabled={liveDiscoveryBusy || !sessionBearer}
          onClick={() => {
            setLiveDiscoveryMachineId(machine.machineId);
            setLiveDiscoveryMessage("Selected " + machine.deviceKey +
              ". Use Discover Agent devices to inspect its configured Agent.");
          }}>Select for discovery</button>}
      </li>)}
    </ul>}
    <h2>Discover devices from a configured MTConnect Agent</h2>
    <p>Read-only server-side /probe request. The API host must already have the trusted Edge MTConnect configuration loaded. Discovery does not register or activate machines.</p>
    <label htmlFor="admin-live-machine">Configured machine</label>
    <select id="admin-live-machine" value={liveDiscoveryMachineId}
      onChange={event => setLiveDiscoveryMachineId(event.currentTarget.value)}>
      <option value="">Choose an existing registered machine</option>
      {(inventory ?? []).filter(machine => !machine.isRetired).map(machine =>
        <option key={machine.machineId} value={machine.machineId}>{machine.displayName}</option>)}
    </select>
    <button type="button" disabled={!sessionBearer || !liveDiscoveryMachineId || liveDiscoveryBusy}
      onClick={() => void discoverConfiguredMachine()}>
      {liveDiscoveryBusy ? "Discovering…" : "Discover Agent devices"}
    </button>
    {liveDiscoveryMessage && <p role="status">{liveDiscoveryMessage}</p>}
    {discoverySourceMachineId && probeDevices.length > 0 && <section>
      <h3>Prepare commissioning review</h3>
      <p>Select the exact MTConnect device and a production line in the registered machine's site.
        This exports a review document only; it does not save or activate configuration.</p>
      <label htmlFor="commissioning-device">Discovered device</label>
      <select id="commissioning-device" value={commissioningDeviceId}
        onChange={event => setCommissioningDeviceId(event.currentTarget.value)}>
        <option value="">Select a device</option>
        {probeDevices.map(device => <option key={device.id} value={device.id}>
          {device.name} ({device.id})</option>)}
      </select>
      <label htmlFor="commissioning-line">Production line</label>
      <select id="commissioning-line" value={commissioningLineId}
        onChange={event => setCommissioningLineId(event.currentTarget.value)}>
        <option value="">Select a production line</option>
        {(lines ?? []).filter(line => !line.isRetired && line.siteId ===
          inventory?.find(machine => machine.machineId === discoverySourceMachineId)?.siteId)
          .map(line => <option key={line.productionLineId} value={line.productionLineId}>
            {line.displayName}</option>)}
      </select>
      <button type="button" disabled={!commissioningDeviceId || !commissioningLineId}
        onClick={exportCommissioningReview}>Export commissioning review JSON</button>
    </section>}
    <h2>Review existing Edge MTConnect configuration</h2>
    <p>Choose an existing Edge JSON configuration file locally to compare configured Agent URLs and machine identities. The file stays in your browser; no configuration is uploaded, saved or activated.</p>
    <label htmlFor="admin-edge-config">Existing Edge configuration JSON</label>
    <input id="admin-edge-config" type="file" accept=".json,application/json"
      onChange={event => void previewEdgeConfig(event.currentTarget.files?.[0])} />
    {edgeConfigError && <p role="alert">{edgeConfigError}</p>}
    {edgeConfigured.length > 0 && <div className="commissioning-table-scroll" role="region" aria-label="Existing Edge machine configuration" tabIndex={0}>
      <table><thead><tr><th>Device key</th><th>Machine ID</th><th>Agent URL</th><th>Registered machine</th></tr></thead>
      <tbody>{edgeConfigured.map(machine => <tr key={machine.machineId}>
        <td>{machine.deviceKey}</td><td><code>{machine.machineId}</code></td>
        <td><code>{machine.baseUri}</code></td>
        <td>{inventory === null ? "Inventory unavailable" :
          inventory.some(item => item.machineId.toLowerCase() === machine.machineId.toLowerCase())
            ? "Existing MachineId found" : "MachineId not registered"}</td>
      </tr>)}</tbody></table>
    </div>}
    <h2>Preview MTConnect devices.xml</h2>
    <p>Read-only commissioning preview: choose a Devices XML file exported from an existing MTConnect Agent /probe endpoint. This does not save URLs, register machines or activate acquisition.</p>
    <label htmlFor="admin-probe-file">MTConnect Devices XML</label>
    <input id="admin-probe-file" type="file" accept=".xml,text/xml,application/xml"
      onChange={event => void previewProbeFile(event.currentTarget.files?.[0])} />
    {probeError && <p role="alert">{probeError}</p>}
    {probeDevices.length > 0 && <div className="commissioning-table-scroll" role="region" aria-label="MTConnect probe preview" tabIndex={0}>
      <button type="button" disabled={selectedProbeIds.length === 0} onClick={exportProbeSelection}>
        Export selected devices ({selectedProbeIds.length})</button>
      <table><thead><tr><th>Select</th><th>Device</th><th>Device ID</th><th>UUID</th><th>Data items</th><th>Inventory comparison</th><th>Action</th></tr></thead>
      <tbody>{probeDevices.map(device => <tr key={device.id}>
        <td><input type="checkbox" aria-label={"Select " + device.name}
          checked={selectedProbeIds.includes(device.id)}
          onChange={event => setSelectedProbeIds(current => event.currentTarget.checked
            ? [...current, device.id] : current.filter(id => id !== device.id))} /></td>
        <td>{device.name}</td><td><code>{device.id}</code></td><td><code>{device.uuid || "Not supplied"}</code></td>
        <td>{device.dataItems.length}</td>
        <td>{(inventory ?? []).some(machine => machine.displayName.toLocaleLowerCase() === device.name.toLocaleLowerCase())
          ? "Name already in inventory — verify identity manually"
          : "Not matched by name"}</td>
        <td><button type="button" disabled={!sessionBearer || !inventory || sites.filter(site => !site.isRetired).length === 0}
          onClick={() => { setKind("machine"); setName(device.name); setRegistrationMessage(
            "Device name copied into machine registration. Choose a site and review identity before submitting. Device ID/UUID is not yet linked."); }}>
          Prepare registration</button>
          <button type="button" onClick={() => setExpandedProbeId(current => current === device.id ? null : device.id)}>
            {expandedProbeId === device.id ? "Hide signals" : "Inspect signals"}
          </button></td>
      </tr>)}</tbody></table>
      {probeDevices.filter(device => device.id === expandedProbeId).map(device => <div key={device.id}>
        <h3>Signals: {device.name}</h3>
        <p>Read-only MTConnect DataItem metadata. Signal roles and mappings are not yet approved or saved.</p>
        <table><thead><tr><th>DataItem ID</th><th>Name</th><th>Type</th><th>Sub-type</th></tr></thead>
          <tbody>{device.dataItems.map(item => <tr key={item.id}>
            <td><code>{item.id}</code></td><td>{item.name || "—"}</td>
            <td>{item.type || "—"}</td><td>{item.subType || "—"}</td>
          </tr>)}</tbody>
        </table>
      </div>)}
    </div>}
    {probeDevices.length > 0 && edgeConfigured.length > 0 && <div>
      <h2>MTConnect / Edge identity comparison</h2>
      <p>Comparison uses exact DeviceKey against discovered device name, ID or UUID. Multiple matching Edge entries block automatic reconciliation. Existing MachineIds must be reused; names are not authoritative identities. No mapping is approved or saved.</p>
      <div className="commissioning-table-scroll" role="region" aria-label="MTConnect and Edge identity comparison" tabIndex={0}>
        <table><thead><tr><th>Discovered device</th><th>Configured MachineId</th><th>Result</th></tr></thead>
        <tbody>{probeEdgeComparison.map(({device, matches, registered, identityConflict}) => <tr key={device.id}>
          <td>{device.name} (<code>{device.id}</code>)</td>
          <td>{matches.length === 1 && matches[0] ? <code>{matches[0].machineId}</code> : "—"}</td>
          <td>{identityConflict ? "BLOCKED: multiple Edge entries match this device" :
            matches.length === 0 ? "No matching Edge DeviceKey" :
            registered ? "Configured MachineId exists in V2 — verify identity" :
            "Configured MachineId absent from V2 — review before registration"}</td>
        </tr>)}</tbody></table>
      </div>
      {edgeConfigured.filter(configured => !probeEdgeComparison.some(row =>
        row.matches.some(match => match.machineId === configured.machineId))).length > 0 &&
        <p role="status">Some configured Edge machines were not found in the selected Devices XML. Check Agent selection and device identities.</p>}
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
