import { useEffect, useState } from "react";

interface Shift {
  shiftId: string; displayName: string; startMinute: number; endMinute: number;
  durationMinutes: number; excludedBreakMinutes: number; plannedProductionMinutes: number;
}
interface ShiftBreak {
  breakId: string; displayName: string; startOffsetMinute: number;
  durationMinutes: number; excludeFromPlannedProduction: boolean;
}
function clock(minute: number): string {
  return `${String(Math.floor((minute % 1440) / 60)).padStart(2, "0")}:${String(minute % 60).padStart(2, "0")}`;
}
export default function LiveShiftCalendar() {
  const [shifts, setShifts] = useState<Shift[] | null>(null);
  const [breaks, setBreaks] = useState<ShiftBreak[] | null>(null);
  const [selected, setSelected] = useState("");
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setShifts(null); setError("");
    void fetch("/api/planning/v2/shifts", { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Shift calendar unavailable (HTTP ${response.status})`);
        const payload: { shifts: Shift[]; isSample: boolean } = await response.json();
        if (payload.isSample || !Array.isArray(payload.shifts)) throw new Error("Live shift calendar response invalid");
        setShifts(payload.shifts);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [retry]);
  useEffect(() => {
    if (!selected) { setBreaks(null); return; }
    const controller = new AbortController();
    setBreaks(null);
    void fetch(`/api/planning/v2/shifts/${encodeURIComponent(selected)}/breaks`,
      { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Shift breaks unavailable (HTTP ${response.status})`);
        const payload: { breaks: ShiftBreak[]; isSample: boolean } = await response.json();
        if (payload.isSample || !Array.isArray(payload.breaks)) throw new Error("Live shift breaks response invalid");
        setBreaks(payload.breaks);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [selected]);
  return <section aria-label="Live shift calendar">
    <h2>Shift calendar · live SQL planning</h2>
    <p>Configured shifts and excluded breaks. This is planning data, not machine evidence or calculated OEE.</p>
    {error && <><p role="alert">{error}. No sample fallback.</p><button type="button" onClick={() => { setSelected(""); setRetry(v => v + 1); }}>Retry shift calendar</button></>}
    {!error && shifts === null && <p role="status">Loading shift definitions…</p>}
    {shifts && shifts.length === 0 && <p>No active shifts configured for this site.</p>}
    {shifts && shifts.length > 0 && <>
      <label>Inspect shift <select value={selected} onChange={e => { setError(""); setSelected(e.target.value); }}>
        <option value="">Select a shift</option>
        {shifts.map(s => <option key={s.shiftId} value={s.shiftId}>{s.displayName} · {clock(s.startMinute)}–{clock(s.endMinute)}</option>)}
      </select></label>
      <table><thead><tr><th>Shift</th><th>Time</th><th>Duration</th><th>Excluded breaks</th><th>Planned production</th></tr></thead>
        <tbody>{shifts.map(s => <tr key={s.shiftId}><td>{s.displayName}</td>
          <td>{clock(s.startMinute)}–{clock(s.endMinute)}</td><td>{s.durationMinutes} min</td>
          <td>{s.excludedBreakMinutes} min</td><td>{s.plannedProductionMinutes} min</td></tr>)}</tbody></table>
      {selected && <><h3>Configured breaks</h3>
        {breaks === null ? <p>Loading breaks…</p> : breaks.length === 0 ? <p>No breaks configured.</p>
          : <ul>{breaks.map(b => <li key={b.breakId}>{b.displayName}: +{b.startOffsetMinute} min, {b.durationMinutes} min
            {b.excludeFromPlannedProduction ? " · excluded from planned production" : " · included in planned production"}</li>)}</ul>}
      </>}
    </>}
  </section>;
}
