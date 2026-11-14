import { useEffect, useState } from "react";

interface Target {
  shiftId: string; targetRevisionId: string; revision: number; targetQuantity: number;
  quantityUnit: string; productionOrder: string | null; partNumber: string | null;
  operation: string | null; revisionReason: string; recordedAtUtc: string;
}
export default function LiveMachineShiftTargets({ machineId, businessDate }: {
  readonly machineId: string; readonly businessDate: string;
}) {
  const [open, setOpen] = useState(false);
  const [targets, setTargets] = useState<Target[] | null>(null);
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  const [selectedShift, setSelectedShift] = useState("");
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setTargets(null); setError("");
    void fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets?businessDate=${encodeURIComponent(businessDate)}`,
      { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Machine shift targets unavailable (HTTP ${response.status})`);
        const data: { targets: Target[]; isSample: boolean; machineId: string; businessDate: string } = await response.json();
        if (data.isSample || data.machineId.toLowerCase() !== machineId.toLowerCase()
            || data.businessDate !== businessDate || !Array.isArray(data.targets))
          throw new Error("Target response does not match selected machine and date");
        setTargets(data.targets);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [open, machineId, businessDate, retry]);
  return <section aria-label="Live machine shift targets">
    <h3>Machine shift targets · SQL planning</h3>
    <button type="button" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      {open ? "Hide targets" : "Load machine shift targets"}
    </button>
    {open && (error ? <><p role="alert">{error}. No sample fallback.</p>
      <button type="button" onClick={() => setRetry(value => value + 1)}>Retry targets</button></>
      : targets === null ? <p role="status">Loading recorded targets…</p>
      : targets.length === 0 ? <p>No targets recorded for this machine and business date. This is not a zero target.</p>
      : <><label>Inspect target revision <select value={selectedShift} onChange={e => setSelectedShift(e.target.value)}><option value="">Select a recorded shift</option>{targets.map(t => <option key={t.shiftId} value={t.shiftId}>Shift {t.shiftId} · revision {t.revision}</option>)}</select></label>
      {selectedShift && (() => { const target = targets.find(t => t.shiftId === selectedShift); return target ? <p>Recorded at {target.recordedAtUtc} · revision {target.revision} · target {target.targetQuantity} {target.quantityUnit}. Revision reason: {target.revisionReason}</p> : null; })()}
      <table><thead><tr><th>Shift</th><th>Target</th><th>Job / part / operation</th><th>Revision</th><th>Reason</th></tr></thead>
        <tbody>{targets.map(target => <tr key={target.targetRevisionId}>
          <td>{target.shiftId}</td><td>{target.targetQuantity} {target.quantityUnit}</td>
          <td>{[target.productionOrder, target.partNumber, target.operation].filter(Boolean).join(" · ") || "Not specified"}</td>
          <td>{target.revision}</td><td>{target.revisionReason}</td>
        </tr>)}</tbody></table></>)}
    <p>Targets are administrative plans, not measured production or OEE.</p>
  </section>;
}
