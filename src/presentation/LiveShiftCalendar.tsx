import LiveStopReasonMaster from "./LiveStopReasonMaster.tsx";
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
  const [open, setOpen] = useState(false);
  const [shifts, setShifts] = useState<Shift[] | null>(null);
  const [breaks, setBreaks] = useState<ShiftBreak[] | null>(null);
  const [selected, setSelected] = useState("");
  const [error, setError] = useState("");
  const [retry, setRetry] = useState(0);
  const [name, setName] = useState("");
  const [start, setStart] = useState("07:30");
  const [end, setEnd] = useState("16:00");
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState("");
  const [breakName, setBreakName] = useState("Planned break");
  const [breakStart, setBreakStart] = useState("11:30");
  const [breakDuration, setBreakDuration] = useState("30");
  const [excludeBreak, setExcludeBreak] = useState(true);
  const [breakReload, setBreakReload] = useState(0);
  const [reasonOpen, setReasonOpen] = useState(false);
  async function createBreak(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); setNotice("");
    const shift = shifts?.find(s => s.shiftId === selected);
    if (!shift) { setError("Select a configured shift."); return; }
    const offset = (minute(breakStart) - shift.startMinute + 1440) % 1440;
    const duration = Number(breakDuration);
    if (!breakName.trim() || minute(breakStart) < 0 || !Number.isInteger(duration) || duration < 1 || duration > 480 || offset + duration > shift.durationMinutes) {
      setError("Break must have a name, valid start time and duration within its shift."); return;
    }
    setSaving(true);
    try {
      const response = await fetch(`/api/planning/v2/shifts/${encodeURIComponent(selected)}/breaks`, { method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayName: breakName.trim(), startOffsetMinute: offset, durationMinute: duration, excludeFromPlannedProduction: excludeBreak }) });
      if (!response.ok) throw new Error(`Break save failed (HTTP ${response.status}).`);
      setNotice("Planned break saved to SQL."); setBreakReload(v => v + 1); setRetry(v => v + 1);
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }
  function minute(value: string): number { const parts = /^(\d{2}):(\d{2})$/.exec(value); return parts ? Number(parts[1]) * 60 + Number(parts[2]) : -1; }
  async function createShift(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); setNotice("");
    if (!name.trim() || minute(start) < 0 || minute(end) < 0 || start === end) { setError("Provide a name and distinct valid start/end times."); return; }
    setSaving(true);
    try {
      const response = await fetch("/api/planning/v2/shifts", { method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayName: name.trim(), startMinute: minute(start), endMinute: minute(end) }) });
      if (!response.ok) throw new Error(`Shift save failed (HTTP ${response.status}). Confirm administrator access and HTTPS origin.`);
      setName(""); setNotice("Shift saved to SQL."); setRetry(v => v + 1);
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }
  useEffect(() => {
    if (!open) return;
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
  }, [retry, open]);
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
  }, [selected, breakReload]);
  return <section aria-label="Live shift calendar">
    <h2>Shift calendar · live SQL planning</h2>
    <button type="button" aria-expanded={open} onClick={() => { setOpen(v => !v); setSelected(""); setError(""); }}> {open ? "Close shift calendar" : "Open shift calendar"} </button>
    {open && <>
    <form onSubmit={createShift}>
      <fieldset disabled={saving}><legend>Create site shift</legend>
        <label>Shift name <input required maxLength={100} value={name} onChange={e => setName(e.target.value)} /></label>
        <label>Start <input type="time" required value={start} onChange={e => setStart(e.target.value)} /></label>
        <label>End <input type="time" required value={end} onChange={e => setEnd(e.target.value)} /></label>
        <button type="submit">{saving ? "Saving…" : "Save shift"}</button>
      </fieldset>
    </form>
    {notice && <p role="status">{notice}</p>}
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
        <form onSubmit={createBreak}><fieldset disabled={saving}><legend>Add planned break</legend>
          <label>Break name <input required maxLength={100} value={breakName} onChange={e => setBreakName(e.target.value)} /></label>
          <label>Break starts at <input type="time" required value={breakStart} onChange={e => setBreakStart(e.target.value)} /></label>
          <label>Duration (minutes) <input type="number" required min={1} max={480} step={1} value={breakDuration} onChange={e => setBreakDuration(e.target.value)} /></label>
          <label><input type="checkbox" checked={excludeBreak} onChange={e => setExcludeBreak(e.target.checked)} /> Exclude from planned production time</label>
          <button type="submit">{saving ? "Saving…" : "Save planned break"}</button>
        </fieldset></form>
        {breaks === null ? <p>Loading breaks…</p> : breaks.length === 0 ? <p>No breaks configured.</p>
          : <ul>{breaks.map(b => <li key={b.breakId}>{b.displayName}: +{b.startOffsetMinute} min, {b.durationMinutes} min
            {b.excludeFromPlannedProduction ? " · excluded from planned production" : " · included in planned production"}</li>)}</ul>}
      </>}
    </>}
    </>}
    <section aria-label="Stop reason administration">
      <h2>Stop Reason Master</h2>
      <button type="button" aria-expanded={reasonOpen} onClick={() => setReasonOpen(v => !v)}>{reasonOpen ? "Close reason master" : "Open reason master"}</button>
      {reasonOpen && <LiveStopReasonMaster />}
    </section>
  </section>;
}
