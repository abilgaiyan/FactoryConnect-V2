import { useEffect, useState } from "react";
import { readActivityHistory, readActivitySeries, type ActivityHistoryPage, type ActivitySeriesList } from "../api/evidence/activity-history-client.ts";
import { EvidenceReadFailure } from "../api/evidence/retained-evidence-client.ts";

type Failure = "denied" | "unavailable" | "failed";
function failure(error: unknown): Failure {
  return error instanceof EvidenceReadFailure && [401, 403].includes(error.status) ? "denied"
    : error instanceof EvidenceReadFailure && error.status === 503 ? "unavailable" : "failed";
}
function ErrorState({ kind }: { kind: Failure }) {
  return <p role="alert">{kind === "denied" ? "Activity history access is not authorized."
    : kind === "unavailable" ? "Activity publication authority is unavailable. No machine state is assumed."
    : "Unable to read published activity history."}</p>;
}
const duration = (ticks: string) => {
  const n = BigInt(ticks), whole = n / 10000000n, fraction = (n % 10000000n).toString().padStart(7, "0").replace(/0+$/, "");
  return `${whole}${fraction ? `.${fraction}` : ""} s`;
};
export function PublishedMachineActivity({ machineId, baseAddress, sessionBearer }: {
  machineId: string; baseAddress: string; sessionBearer: string | null;
}) {
  const [state, setState] = useState<{ kind: "loading" | Failure } | { kind: "ready"; data: ActivitySeriesList; bearer: string }>({ kind: "loading" });
  const [mapping, setMapping] = useState("");
  useEffect(() => {
    const abort = new AbortController(); setState({ kind: "loading" }); setMapping("");
    if (!sessionBearer) { setState({ kind: "denied" }); return () => abort.abort(); }
    void readActivitySeries(baseAddress, machineId, sessionBearer, abort.signal).then(data => {
      if (!abort.signal.aborted) { setState({ kind: "ready", data, bearer: sessionBearer }); setMapping(data.series.length === 1 ? data.series[0]!.mappingId : ""); }
    }).catch(error => { if (!abort.signal.aborted) setState({ kind: failure(error) }); });
    return () => abort.abort();
  }, [machineId, baseAddress, sessionBearer]);
  const view = state.kind === "ready" && (state.data.machineId !== machineId || state.bearer !== sessionBearer)
    ? { kind: "loading" as const } : state;
  const selected = view.kind === "ready" ? view.data.series.find(x => x.mappingId === mapping) : undefined;
  return <section aria-label="Published machine activity">
    <h2>Published activity history</h2>
    <p>Closed periods derived from accepted machine observations. This history does not establish live connectivity, physical observation completeness or OEE.</p>
    {view.kind === "loading" && <p role="status">Checking published activity…</p>}
    {(view.kind === "denied" || view.kind === "unavailable" || view.kind === "failed") && <ErrorState kind={view.kind} />}
    {view.kind === "ready" && (view.data.series.length === 0
      ? <p>No published activity history for this machine and stream. Qualified processing must publish periods before they can be shown.</p>
      : <>
        {view.data.series.length > 1 && <><label htmlFor="activity-series">Activity history source</label>
          <select id="activity-series" value={selected?.mappingId ?? ""} onChange={event => setMapping(event.currentTarget.value)}>
            <option value="">Select history source</option>
            {view.data.series.map(x => <option key={x.mappingId} value={x.mappingId}>{x.sourceAddress} ({x.mappingId})</option>)}
          </select></>}
        {selected && <ActivityPeriods key={`${machineId}:${selected.mappingId}:${view.bearer}`} machineId={machineId} mappingId={selected.mappingId}
          baseAddress={baseAddress} bearer={view.bearer} />}
      </>)}
  </section>;
}
function ActivityPeriods({ machineId, mappingId, baseAddress, bearer }: {
  machineId: string; mappingId: string; baseAddress: string; bearer: string;
}) {
  const [cursor, setCursor] = useState<string | null>(null), [refresh, setRefresh] = useState(0);
  const [state, setState] = useState<{ kind: "loading" | Failure } | { kind: "ready"; data: ActivityHistoryPage; cursor: string | null; refresh: number }>({ kind: "loading" });
  useEffect(() => {
    const abort = new AbortController(); setState({ kind: "loading" });
    void readActivityHistory(baseAddress, machineId, mappingId, bearer, cursor, abort.signal)
      .then(data => { if (!abort.signal.aborted) setState({ kind: "ready", data, cursor, refresh }); })
      .catch(error => { if (!abort.signal.aborted) setState({ kind: failure(error) }); });
    return () => abort.abort();
  }, [machineId, mappingId, baseAddress, bearer, cursor, refresh]);
  const view = state.kind === "ready" && (state.cursor !== cursor || state.refresh !== refresh) ? { kind: "loading" as const } : state;
  return <div>
    <button type="button" onClick={() => { setCursor(null); setRefresh(x => x + 1); }}>Refresh latest activity</button>
    {view.kind === "loading" && <p role="status">Loading closed activity periods…</p>}
    {(view.kind === "denied" || view.kind === "unavailable" || view.kind === "failed") && <ErrorState kind={view.kind} />}
    {view.kind === "ready" && <>
      <p>Newest periods first. Times use your browser timezone. This read does not yet verify the publication profile or cross-batch ancestry. Closed periods are historical derived evidence, not proof of physical observation completeness.</p>
      {view.data.rows.length === 0 ? <p>No closed periods have been published for this history source.</p>
        : <div style={{ overflowX: "auto" }} tabIndex={0} role="region" aria-label="Published activity period table"><table style={{ minWidth: "62rem" }}>
          <thead><tr><th>Recorded interpretation</th><th>Started</th><th>Ended</th><th style={{ whiteSpace: "nowrap" }}>Duration</th><th>Source values</th><th>Evidence</th></tr></thead>
          <tbody>{view.data.rows.map(row => <tr key={row.effectId}>
            <td>{row.state}</td><td><time dateTime={row.startedAt}>{new Date(row.startedAt).toLocaleString()}</time></td>
            <td><time dateTime={row.endedAt}>{new Date(row.endedAt).toLocaleString()}</time></td><td>{duration(row.durationTicks)}</td>
            <td><code style={{ whiteSpace: "pre-wrap" }}>{row.startRawValue}</code> → <code style={{ whiteSpace: "pre-wrap" }}>{row.endRawValue}</code></td>
            <td><details><summary>Publication provenance</summary><dl>
              <dt>Instance / source sequences</dt><dd>{row.instanceId} / {row.startSequence} → {row.endSequence}</dd>
              <dt>Publication operation</dt><dd><code>{row.publicationOperationId}</code></dd>
              <dt>Mapping</dt><dd><code>{view.data.series.mappingId}</code></dd>
              <dt>Qualification SHA-256</dt><dd><code style={{ overflowWrap: "anywhere" }}>{view.data.series.qualificationSha256}</code></dd>
              <dt>Approved package revision</dt><dd><code>{view.data.series.packageRevisionId}</code></dd>
              <dt>Runtime acknowledgement</dt><dd><code>{view.data.series.acknowledgementId}</code></dd>
              <dt>Exact duration ticks</dt><dd>{row.durationTicks}</dd>
            </dl></details></td>
          </tr>)}</tbody></table></div>}
      {view.data.nextCursor && <button type="button" onClick={() => setCursor(view.data.nextCursor)}>Older activity periods</button>}
    </>}
  </div>;
}
