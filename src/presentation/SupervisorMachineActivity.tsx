import { useEffect, useState } from "react";

type Machine = { machineId: string; displayName: string };
type Inventory = { machines: Machine[]; source: string; isSample: boolean };
type Activity = { status: "published-history" | "no-published-history"; series: unknown[]; evidenceKind: string };

export default function SupervisorMachineActivity() {
  const [machines, setMachines] = useState<Machine[]>([]);
  const [selected, setSelected] = useState("");
  const [activity, setActivity] = useState<Activity | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setError(""); setMachines([]); setSelected(""); setActivity(null); setLoading(true);
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
    setActivity(null); setError(""); setLoading(true);
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
    {activity && !loading && <p role="status">{activity.status === "published-history"
      ? "Published activity series: " + activity.series.length + ". Shift totals not yet calculated."
      : "No published machine activity history. Running and stopped time are unknown."}</p>}
  </section>;
}
