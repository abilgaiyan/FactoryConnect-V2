import { useEffect, useState } from "react";
import { activityDateRange, activityStates, formatActivityDuration, loadActivityReport, type ActivityReport } from "../application/machine-activity-report.ts";

export default function LiveMachineActivityCoverage({ machineId, date }: { readonly machineId: string; readonly date: string }) {
  const [open, setOpen] = useState(false);
  const [report, setReport] = useState<ActivityReport | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setReport(null); setError("");
    try {
      const range = activityDateRange(date, date);
      void loadActivityReport(machineId, range, controller.signal).then(value => {
        if (!controller.signal.aborted) setReport(value);
      }).catch(reason => {
        if (!controller.signal.aborted) setError(String(reason));
      });
    } catch (reason) { setError(String(reason)); }
    return () => controller.abort();
  }, [open, machineId, date, attempt]);
  const covered = report?.totals.reduce((sum, row) => sum + row.durationTicks, 0) ?? 0;
  const total = covered + (report?.unrepresentedTicks ?? 0);
  return <section aria-label="Recorded machine activity coverage">
    <h3>Recorded machine activity · coverage</h3>
    <p>Historical closed activity periods for the selected IST calendar date. This is not a shift OEE calculation; unrepresented time is not assumed to be stopped or running.</p>
    <button type="button" onClick={() => setOpen(value => !value)}>{open ? "Hide recorded activity" : "Load recorded activity"}</button>
    {open && <>
      {error && <p role="alert">{error}. No sample fallback. <button type="button" onClick={() => setAttempt(value => value + 1)}>Retry activity</button></p>}
      {!error && !report && <p role="status">Loading recorded activity…</p>}
      {report && <>
        <p>Evidence read as of <time dateTime={report.readAsOf}>{report.readAsOf}</time> · Stream {report.streamKey} · Through position {report.throughPosition ?? "unavailable"}</p>
        <p>Represented: {formatActivityDuration(covered)} · Unrepresented: {formatActivityDuration(report.unrepresentedTicks)}</p>
        <div role="img" aria-label="Recorded machine state coverage, including unrepresented time" style={{ display: "flex", width: "100%", height: "24px", background: "#e5e7eb" }}>
          {total > 0 && activityStates.map((state, index) => {
            const ticks = report.totals.find(row => row.state === state)?.durationTicks ?? 0;
            return ticks > 0 ? <div key={state} title={state + ": " + formatActivityDuration(ticks)} style={{ width: (100 * ticks / total) + "%", background: ["#64748b","#dc2626","#f59e0b","#16a34a","#7c3aed","#475569"][index] }} /> : null;
          })}
        </div>
        <table><thead><tr><th>State</th><th>Recorded duration</th></tr></thead><tbody>
          {activityStates.map(state => <tr key={state}><td>{state}</td><td>{formatActivityDuration(report.totals.find(row => row.state === state)?.durationTicks ?? 0)}</td></tr>)}
          <tr><td>Unrepresented coverage</td><td>{formatActivityDuration(report.unrepresentedTicks)}</td></tr>
        </tbody></table>
        <p>Recorded states and gaps are shown separately. Missing intervals do not establish zero production or downtime.</p>
      </>}
    </>}
  </section>;
}
