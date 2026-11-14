import { useEffect, useState } from "react";

type Reason = { stopReasonId: string; reasonCode: string; displayName: string; category: string; isPlanned: boolean };
export default function LiveStopReasonPicker({ machineId }: { readonly machineId: string }) {
  const [open, setOpen] = useState(false);
  const [reasons, setReasons] = useState<Reason[] | null>(null);
  const [selected, setSelected] = useState("");
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setReasons(null); setError("");
    void fetch("/api/planning/v2/stop-reasons", { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Stop reasons unavailable (HTTP ${response.status})`);
        const data: { reasons: Reason[]; isSample: boolean } = await response.json();
        if (data.isSample || !Array.isArray(data.reasons)) throw new Error("Invalid stop reason response");
        setReasons(data.reasons);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, retry]);
  return <section aria-label="Supervisor stop reason selection">
    <h3>Downtime reason · machine {machineId}</h3>
    <button type="button" aria-expanded={open} onClick={() => { setOpen(value => !value); setSelected(""); }}>
      {open ? "Hide stop reasons" : "View configured stop reasons"}
    </button>
    {open && <>
      {error ? <><p role="alert">{error}. No sample fallback.</p>
        <button type="button" onClick={() => setRetry(value => value + 1)}>Retry reasons</button></>
        : reasons === null ? <p role="status">Loading site reasons…</p>
        : reasons.length === 0 ? <p>No active stop reasons configured for this site.</p>
        : <label>Reason catalogue <select value={selected} onChange={e => setSelected(e.target.value)}>
          <option value="">Select a reason to inspect</option>
          {reasons.map(reason => <option key={reason.stopReasonId} value={reason.stopReasonId}>
            {reason.reasonCode} · {reason.displayName} ({reason.category})
          </option>)}
        </select></label>}
      {selected && reasons && <p>{reasons.find(reason => reason.stopReasonId === selected)?.isPlanned
        ? "Configured as planned stoppage." : "Configured as unplanned stoppage."}</p>}
      <p>Catalogue inspection only. No reason is assigned to an activity event until the API exposes a verified exact-stop reference and authorized assignment write.</p>
    </>}
  </section>;
}
