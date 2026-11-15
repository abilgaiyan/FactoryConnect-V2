import { useEffect, useState } from "react";
import { createDashboardApplicationRuntime, type DashboardApplicationRuntime } from "../application/application-runtime.ts";
import { SupervisorReadOnly } from "./SupervisorReadOnly.tsx";
import "./supervisor-preview.css";
import LiveShiftCalendar from "./LiveShiftCalendar.tsx";
import SupervisorTargetEntry from "./SupervisorTargetEntry.tsx";
import SupervisorMachineActivity from "./SupervisorMachineActivity.tsx";

// Imported only by development entry points. Sample editors never mount in real-data mode.
export default function SupervisorWorkspace({ runtime }: { readonly runtime?: DashboardApplicationRuntime }) {
  return <section className="supervisor-preview" aria-label="Supervisor workspace">
    <h1>Supervisor Desk · Management reporting</h1>
    <p>Live site shift planning and target revisions are saved to SQL Server. Machine activity reporting remains evidence-based and read-only; downtime reason assignment is not yet enabled.</p>
    <LiveShiftCalendar />
    {(!runtime || runtime.configuration.sources.length === 0) && <SupervisorMachineActivity />}
    <RuntimeReadOnly {...(runtime ? { runtime } : {})} />
    <SupervisorTargetEntry />
  </section>;
}

function RuntimeReadOnly({ runtime }: { readonly runtime?: DashboardApplicationRuntime }) {
  const [loaded, setLoaded] = useState<DashboardApplicationRuntime | null>(runtime ?? null);
  const [error, setError] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (runtime) { setLoaded(runtime); return; }
    let current = true;
    setLoaded(null); setError(false);
    void createDashboardApplicationRuntime(window.location.origin).then(result => {
      if (current) setLoaded(result);
    }, () => { if (current) setError(true); });
    return () => { current = false; };
  }, [runtime, attempt]);
  return <>

    {error ? <><p role="alert">Reporting configuration is unavailable. No factory evidence established; no sample fallback.</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry configuration</button></>
      : loaded ? <SupervisorReadOnly runtime={loaded} /> : <p role="status">Loading reporting configuration…</p>}
  </>;
}
