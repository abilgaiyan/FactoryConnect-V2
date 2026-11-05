import { useEffect, useState } from "react";

export interface CommissioningField { path: string; value: string | null }
export interface CommissioningProcessor {
  rowId: number; machineId: string; processorId: string; streamKey: string;
  position: string | null; admissionBound: boolean; productionStartsAt: string | null;
}
export interface CommissioningSnapshot {
  readStartedAt: string;
  configuration: { status: string; authority: string; fileModifiedAt: string | null; sha256: string | null; fields: CommissioningField[] };
  durable: { status: string; authority: string; readCompletedAt: string | null; processors: CommissioningProcessor[] };
  runtimeActivation: string; transportHealth: string; checkpointFreshness: string;
}

export function admissionComparison(fields: readonly CommissioningField[], row: CommissioningProcessor): string {
  if (!row.admissionBound) return "Not bound";
  const identities = fields.filter(field => field.path.endsWith(":MachineId") &&
    field.path.startsWith("ProductionProcessing:") && field.value?.toLowerCase() === row.machineId.toLowerCase());
  if (identities.length !== 1 || row.processorId !== `production-context:${row.machineId}`) return "Unknown: no exact configured activity processor";
  const prefix = identities[0]!.path.slice(0, -"MachineId".length);
  if (fields.find(field => field.path === prefix + "ActivityStreamKey")?.value !== row.streamKey) return "Unknown: stream does not match";
  const configured = fields.find(field => field.path === prefix + "ProductionStartsAt")?.value ?? null;
  if (configured === null && row.productionStartsAt === null) return "Match: unbounded";
  const configuredTime = configured === null ? NaN : Date.parse(configured);
  const durableTime = row.productionStartsAt === null ? NaN : Date.parse(row.productionStartsAt);
  if (configured !== null && !Number.isFinite(configuredTime)) return "Unknown: invalid configured boundary";
  return configuredTime === durableTime ? "Match: boundary instant" : "Mismatch: review required; no recovery action is provided";
}

export function isCommissioningSnapshot(value: unknown): value is CommissioningSnapshot {
  if (typeof value !== "object" || value === null) return false;
  const root = value as Record<string, unknown>;
  const config = root.configuration as Record<string, unknown> | undefined;
  const durable = root.durable as Record<string, unknown> | undefined;
  const nullableString = (item: unknown) => item === null || typeof item === "string";
  return typeof root.readStartedAt === "string" && typeof root.runtimeActivation === "string" &&
    typeof root.transportHealth === "string" && typeof root.checkpointFreshness === "string" &&
    config !== null && typeof config === "object" && typeof config.status === "string" && typeof config.authority === "string" &&
    nullableString(config.fileModifiedAt) && nullableString(config.sha256) && Array.isArray(config.fields) && config.fields.length <= 2049 &&
    config.fields.every(field => field !== null && typeof field === "object" && typeof field.path === "string" && nullableString(field.value)) &&
    durable !== null && typeof durable === "object" && typeof durable.status === "string" && typeof durable.authority === "string" &&
    nullableString(durable.readCompletedAt) && Array.isArray(durable.processors) && durable.processors.length <= 128 &&
    durable.processors.every(row => row !== null && typeof row === "object" && typeof row.rowId === "number" &&
      typeof row.machineId === "string" && typeof row.processorId === "string" && typeof row.streamKey === "string" &&
      nullableString(row.position) && typeof row.admissionBound === "boolean" && nullableString(row.productionStartsAt));
}

export function CommissioningPage() {
  const [revision, setRevision] = useState(0);
  const [state, setState] = useState<{ status: "loading" | "failed" | "read"; snapshot?: CommissioningSnapshot }>({ status: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    const timer = setTimeout(() => controller.abort(), 10_000);
    setState({ status: "loading" });
    void (async () => {
      try {
        const response = await fetch("/api/commissioning/v1/snapshot", { signal: controller.signal, cache: "no-store", redirect: "error" });
        if (!response.ok) throw new Error("Commissioning unavailable");
        const payload: unknown = await response.json();
        if (!isCommissioningSnapshot(payload)) throw new Error("Invalid commissioning response");
        if (active) setState({ status: "read", snapshot: payload });
      } catch {
        if (active) setState({ status: "failed" });
      } finally { clearTimeout(timer); }
    })();
    return () => { active = false; clearTimeout(timer); controller.abort(); };
  }, [revision]);
  const snapshot = state.snapshot;
  return <section className="commissioning-page" aria-labelledby="commissioning-title">
    <h1 id="commissioning-title">Commissioning — read only</h1>
    <p>Captured configuration and durable progress are separate evidence. No runtime activation acknowledgement is available. Checkpoints do not prove historical correctness or live transport health.</p>
    <button type="button" disabled={state.status === "loading"} onClick={() => setRevision(value => value + 1)}>Refresh snapshot</button>
    {state.status === "loading" && <p role="status">Reading a new snapshot. Previous values are not displayed.</p>}
    {state.status === "failed" && <p role="alert">Commissioning diagnostics unavailable. This feature is disabled by default and restricted to local review. No previous snapshot is displayed.</p>}
    {snapshot && <>
      <p>Read started: <time>{snapshot.readStartedAt}</time>. This snapshot does not refresh automatically; refresh before operational decisions.</p>
      <h2>Configuration capture</h2>
      <p>{snapshot.configuration.status} · Source: {snapshot.configuration.authority}</p>
      <p>File modified: {snapshot.configuration.fileModifiedAt ?? "unknown"}. This is file metadata, not runtime capture or activation time.</p>
      <p>Captured file SHA256: <code className="commissioning-hash">{snapshot.configuration.sha256 ?? "unknown"}</code>. Configuration revision: unknown. Runtime applied: {snapshot.runtimeActivation}.</p>
      <p>Adapter endpoints and raw configuration are withheld. Empty sections mean unavailable or unconfigured; schedule coverage is not inferred.</p>
      <div className="commissioning-table-scroll" role="region" aria-labelledby="commissioning-config-caption" tabIndex={0}>
      <table className="commissioning-config-table"><caption id="commissioning-config-caption">Configured inventory, mappings, schedules and admission (paths preserve assignment scope)</caption>
        <thead><tr><th>Configuration path</th><th>Configured value</th></tr></thead>
        <tbody>{snapshot.configuration.fields.map(field => <tr key={field.path}><td>{field.path}</td><td>{field.value ?? "null (explicit)"}</td></tr>)}</tbody>
      </table>
      </div>
      <h2>Durable processor heads</h2>
      <p>{snapshot.durable.status} · Source: {snapshot.durable.authority}. Read completed: {snapshot.durable.readCompletedAt ?? "unknown"}.</p>
      <p>Only production-context/quantity processor heads are included. Acquisition, mapping, activity and metric processor checkpoints are unavailable on this surface.</p>
      <p>Checkpoint freshness: {snapshot.checkpointFreshness}. Transport: {snapshot.transportHealth}.</p>
      {snapshot.durable.status === "read" && snapshot.durable.processors.length === 0 && <p>No production processor bindings were returned; progress is not established.</p>}
      <div className="commissioning-table-scroll" role="region" aria-labelledby="commissioning-processors-caption" tabIndex={0}>
      <table className="commissioning-processors-table"><caption id="commissioning-processors-caption">Exact durable ownership; independent of the captured file</caption>
        <thead><tr><th>Machine / row</th><th>Processor / stream</th><th>Checkpoint</th><th>Durable admission</th><th>Configuration comparison</th></tr></thead>
        <tbody>{snapshot.durable.processors.map(row => <tr key={row.rowId}>
          <td>{row.machineId}<br />Row {row.rowId}</td><td>{row.processorId}<br />{row.streamKey}</td>
          <td>{row.position ?? "Not established"}</td>
          <td>{row.admissionBound ? row.productionStartsAt ?? "Bound, unbounded (null)" : "Not bound"}</td>
          <td>{snapshot.configuration.status === "captured-file-only" ? admissionComparison(snapshot.configuration.fields, row) : "Unknown: configuration unavailable"}</td>
        </tr>)}</tbody>
      </table>
      </div>
    </>}
  </section>;
}
