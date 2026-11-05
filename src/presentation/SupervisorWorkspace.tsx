import { useEffect, useState } from "react";
import { createDashboardApplicationRuntime, type DashboardApplicationRuntime } from "../application/application-runtime.ts";
import SupervisorDeskPreview from "./SupervisorDeskPreview.tsx";
import { SupervisorReadOnly } from "./SupervisorReadOnly.tsx";
import "./supervisor-preview.css";

// Imported only by development entry points. Sample editors never mount in real-data mode.
export default function SupervisorWorkspace({ runtime }: { readonly runtime?: DashboardApplicationRuntime }) {
  const [mode, setMode] = useState<"real" | "sample">("real");
  return <section className="supervisor-preview" aria-label="Supervisor workspace">
    <h1>Supervisor Desk · Management reporting</h1>
    <p><strong>Development demonstration only.</strong> No authentication, production writes or durable explanation service is enabled.</p>
    <nav className="supervisor-views" aria-label="Workspace operating mode">
      <button type="button" aria-pressed={mode === "real"} onClick={() => setMode("real")}>Real read-only</button>
      <button type="button" aria-pressed={mode === "sample"} onClick={() => setMode("sample")}>Sample preview</button>
    </nav>
    <p>Switching modes discards temporary sample entries and clears real selections. Samples are synthetic and never substitute for a failed factory-data request.</p>
    {mode === "sample" ? <div key="sample"><p role="status"><strong>Sample preview — synthetic data only.</strong> Entries disappear on reload or mode change.</p><SupervisorDeskPreview /></div>
      : <RuntimeReadOnly key="real" {...(runtime ? { runtime } : {})} />}
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
    <p role="status"><strong>Real read-only mode — configured reporting sources.</strong> Data provenance is shown on every completed read; this mode does not certify factory acceptance.</p>
    {error ? <><p role="alert">Reporting configuration is unavailable. No factory evidence established; no sample fallback.</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry configuration</button></>
      : loaded ? <SupervisorReadOnly runtime={loaded} /> : <p role="status">Loading reporting configuration…</p>}
  </>;
}
