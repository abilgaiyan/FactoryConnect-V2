import { useEffect, useState } from "react";

interface Shift {
  shiftId: string;
  displayName: string;
  startMinute: number;
  endMinute: number;
  durationMinutes: number;
  excludedBreakMinutes: number;
  plannedProductionMinutes: number;
}
interface ShiftResponse { shifts: Shift[]; isSample: boolean }
function validShift(s: Shift): boolean {
  return typeof s.shiftId === "string" && typeof s.displayName === "string" &&
    Number.isInteger(s.startMinute) && s.startMinute >= 0 && s.startMinute < 1440 &&
    Number.isInteger(s.endMinute) && s.endMinute >= 0 && s.endMinute < 1440 &&
    Number.isInteger(s.durationMinutes) && s.durationMinutes > 0 && s.durationMinutes <= 1440 &&
    Number.isInteger(s.excludedBreakMinutes) && s.excludedBreakMinutes >= 0 &&
    Number.isInteger(s.plannedProductionMinutes) && s.plannedProductionMinutes >= 0 &&
    s.excludedBreakMinutes + s.plannedProductionMinutes === s.durationMinutes;
}
function clock(minute: number): string {
  return String(Math.floor(minute / 60)).padStart(2, "0") + ":" + String(minute % 60).padStart(2, "0");
}
export default function SupervisorShiftTime() {
  const [open, setOpen] = useState(false);
  const [revision, setRevision] = useState(0);
  const [shifts, setShifts] = useState<Shift[] | null>(null);
  const [selected, setSelected] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setShifts(null); setSelected(""); setError("");
    void fetch("/api/planning/v2/shifts", { credentials: "same-origin", cache: "no-store", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("Shift planning unavailable (HTTP " + response.status + ").");
        const value: unknown = await response.json();
        if (!value || typeof value !== "object") throw new Error("Invalid shift planning response.");
        const data = value as ShiftResponse;
        if (data.isSample !== false || !Array.isArray(data.shifts) || !data.shifts.every(validShift))
          throw new Error("Shift planning evidence is invalid.");
        if (!controller.signal.aborted) setShifts(data.shifts);
      }).catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Shift planning unavailable."); });
    return () => controller.abort();
  }, [open, revision]);
  const shift = shifts?.find(s => s.shiftId === selected);
  return <section aria-label="Shift planned production time">
    <h2>Shift production time · SQL planning</h2>
    <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}>
      {open ? "Close shift time" : "Inspect shift time"}
    </button>
    {open && <>
      <p>Planned time excludes configured breaks. Machine running, stopped and unknown time require verified interval coverage and are not calculated here.</p>
      <button type="button" onClick={() => setRevision(v => v + 1)}>Refresh shift planning</button>
      {error && <p role="alert">{error} No sample fallback.</p>}
      {!error && shifts === null && <p role="status">Loading SQL shifts…</p>}
      {shifts?.length === 0 && <p>No site shifts configured.</p>}
      {shifts && shifts.length > 0 && <>
        <label htmlFor="supervisor-shift-time">Shift</label>
        <select id="supervisor-shift-time" value={selected} onChange={e => setSelected(e.currentTarget.value)}>
          <option value="">Select a shift</option>
          {shifts.map(s => <option key={s.shiftId} value={s.shiftId}>{s.displayName} · {clock(s.startMinute)}–{clock(s.endMinute)}</option>)}
        </select>
        {shift && <table><caption>{shift.displayName} · configured site schedule</caption>
          <thead><tr><th>Shift duration</th><th>Excluded breaks</th><th>Planned production</th></tr></thead>
          <tbody><tr><td>{shift.durationMinutes} min</td><td>{shift.excludedBreakMinutes} min</td><td>{shift.plannedProductionMinutes} min</td></tr></tbody>
        </table>}
      </>}
    </>}
  </section>;
}
