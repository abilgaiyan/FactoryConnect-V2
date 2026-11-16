import { useEffect, useState } from "react";

type Machine = { machineId: string; displayName: string };
type Inventory = { machines: Machine[]; source: string; isSample: boolean };
type Activity = { status: "published-history" | "no-published-history"; series: unknown[]; evidenceKind: string };
type PeriodPage = { rows: unknown[]; nextCursor: string | null; evidenceKind: string };
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function mappingId(value: unknown): string | null {
  if (!value || typeof value !== "object") return null;
  const item = value as Record<string, unknown>;
  const id = item.mappingId ?? item.MappingId;
  return typeof id === "string" && guid.test(id) ? id : null;
}

export default function SupervisorMachineActivity() {
  const [machines, setMachines] = useState<Machine[]>([]);
  const [selected, setSelected] = useState("");
  const [activity, setActivity] = useState<Activity | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [revision, setRevision] = useState(0);
  const [mapping, setMapping] = useState("");
  const [page, setPage] = useState<PeriodPage | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [periodError, setPeriodError] = useState("");
  const [periodLoading, setPeriodLoading] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    setError(""); setMachines([]); setSelected(""); setActivity(null); setMapping(""); setPage(null); setLoading(true);
    void fetch("/api/reporting/v2/machines", { credentials: "same-origin", cache: "no-store", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("SQL machine discovery unavailable (HTTP " + response.status + ").");
        const value: unknown = await response.json();
        if (!value || typeof value !== "object") throw new Error("Invalid machine inventory.");
        const data = value as Inventory;
        if (data.source !== "sql-master" || data.isSample !== false || !Array.isArray(data.machines) ||
            !data.machines.every(m => typeof m.machineId === "string" && typeof m.displayName === "string"))
          throw new Error("Unverified machine inventory.");
        if (!controller.signal.aborted) setMachines(data.machines);
      }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Machine discovery failed."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [revision]);
  useEffect(() => {
    if (!selected) { setActivity(null); return; }
    const controller = new AbortController();
    setActivity(null); setMapping(""); setPage(null); setCursor(null); setError(""); setLoading(true);
    void fetch("/api/reporting/v2/machines/" + encodeURIComponent(selected) + "/activity",
      { credentials: "same-origin", cache: "no-store", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("Published activity unavailable (HTTP " + response.status + ").");
        const value: unknown = await response.json();
        if (!value || typeof value !== "object") throw new Error("Invalid activity response.");
        const data = value as Activity;
        if (!["published-history", "no-published-history"].includes(data.status) ||
            data.evidenceKind !== "historical-derived-activity" || !Array.isArray(data.series))
          throw new Error("Unverified activity response.");
        if (!controller.signal.aborted) setActivity(data);
      }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Activity read failed."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [selected]);
  useEffect(() => {
    if (!selected || !mapping) { setPage(null); setCursor(null); return; }
    const controller = new AbortController();
    setPage(null); setCursor(null); setPeriodError(""); setPeriodLoading(true);
    void fetch("/api/reporting/v2/machines/" + encodeURIComponent(selected) + "/activity/" + encodeURIComponent(mapping) + "?take=20",
      { credentials: "same-origin", cache: "no-store", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("Activity periods unavailable (HTTP " + response.status + ").");
        const data: unknown = await response.json();
        if (!data || typeof data !== "object") throw new Error("Invalid activity page.");
        const value = data as PeriodPage;
        if (value.evidenceKind !== "historical-derived-activity" || !Array.isArray(value.rows) ||
            !(value.nextCursor === null || typeof value.nextCursor === "string"))
          throw new Error("Invalid verified activity page.");
        if (!controller.signal.aborted) { setPage(value); setCursor(value.nextCursor); }
      }).catch(e => { if (!controller.signal.aborted) setPeriodError(e instanceof Error ? e.message : "Activity read failed."); })
      .finally(() => { if (!controller.signal.aborted) setPeriodLoading(false); });
    return () => controller.abort();
  }, [selected, mapping]);
  return <section aria-label="SQL machine activity">
    <h2>Machine activity · SQL history</h2>
    <p>Historical published activity only. Live connectivity, shift duration and OEE are not inferred.</p>
    <button type="button" onClick={() => setRevision(n => n + 1)}>Refresh machines</button>
    <label htmlFor="supervisor-sql-machine">Machine</label>
    <select id="supervisor-sql-machine" value={selected} onChange={e => setSelected(e.currentTarget.value)}>
      <option value="">Select a registered machine</option>
      {machines.map(m => <option key={m.machineId} value={m.machineId}>{m.displayName}</option>)}
    </select>
    {loading && <p role="status">Reading SQL evidence…</p>}
    {error && <p role="alert">{error}</p>}
    {!loading && !error && machines.length === 0 && <p>No registered machines available for this site.</p>}
    {activity?.status === "published-history" && <div>
      <label htmlFor="supervisor-activity-mapping">Published source mapping</label>
      <select id="supervisor-activity-mapping" value={mapping} onChange={e => setMapping(e.currentTarget.value)}>
        <option value="">Select a published mapping</option>
        {activity.series.map((series, i) => {
          const id = mappingId(series);
          return id ? <option key={id} value={id}>Mapping {i + 1} · {id}</option> : null;
        })}
      </select>
      {activity.series.length > 0 && !activity.series.some(series => mappingId(series)) &&
        <p>Published mapping identifiers are unavailable; period inspection cannot proceed.</p>}
      {periodLoading && <p role="status">Reading verified periods…</p>}
      {periodError && <p role="alert">{periodError}</p>}
      {page && <p role="status">Verified historical period records on this page: {page.rows.length}.
        {cursor ? " Additional records exist; this page is not complete history." : " No continuation cursor returned."}
        {" "}Period count is not machine running time or complete shift coverage.</p>}
    </div>}
    {activity && !loading && <p role="status">{activity.status === "published-history"
      ? "Published activity series: " + activity.series.length + ". Shift totals not yet calculated."
      : "No published machine activity history. Running and stopped time are unknown."}</p>}
  </section>;
}
