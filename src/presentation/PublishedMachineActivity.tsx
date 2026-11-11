import { useEffect, useState } from "react";
import { readActivityHistory, readActivitySeries, type ActivityHistoryPage, type ActivitySeriesList } from "../api/evidence/activity-history-client.ts";
import { readProcessedState, type ProcessedStateResult } from "../api/evidence/processed-state-client.ts";
import { readAcquisitionHealth, type AcquisitionHealthResult } from "../api/evidence/acquisition-health-client.ts";
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
  const [processed, setProcessed] = useState<{ kind: "loading" | Failure } | { kind: "ready"; data: ProcessedStateResult; bearer: string }>({ kind: "loading" });
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [mapping, setMapping] = useState("");
  const [seriesRefresh, setSeriesRefresh] = useState(0);
  useEffect(() => {
    const abort = new AbortController(); setState({ kind: "loading" }); setProcessed({ kind: "loading" }); setMapping("");
    if (!sessionBearer) { setState({ kind: "denied" }); setProcessed({ kind: "denied" }); return () => abort.abort(); }
    void readProcessedState(baseAddress, machineId, sessionBearer, abort.signal)
      .then(data => { if (!abort.signal.aborted) setProcessed({ kind: "ready", data, bearer: sessionBearer }); })
      .catch(error => { if (!abort.signal.aborted) setProcessed({ kind: failure(error) }); });
    void readActivitySeries(baseAddress, machineId, sessionBearer, abort.signal).then(data => {
      if (!abort.signal.aborted) { setState({ kind: "ready", data, bearer: sessionBearer }); setMapping(data.series.length === 1 ? data.series[0]!.mappingId : ""); }
    }).catch(error => { if (!abort.signal.aborted) setState({ kind: failure(error) }); });
    return () => abort.abort();
  }, [machineId, baseAddress, sessionBearer, seriesRefresh]);
  useEffect(() => {
    if (!autoRefresh || !sessionBearer) return;
    let active = true;
    let pending = false;
    const abort = new AbortController();
    const timer = setInterval(() => {
      if (pending || document.visibilityState === "hidden") return;
      pending = true;
      void readProcessedState(baseAddress, machineId, sessionBearer, abort.signal)
        .then(data => { if (active) setProcessed({ kind: "ready", data, bearer: sessionBearer }); })
        .catch(error => {
          if (!active) return;
          const kind = failure(error);
          setProcessed({ kind });
          if (kind === "denied") setAutoRefresh(false);
        })
        .finally(() => { pending = false; });
    }, 30000);
    return () => { active = false; abort.abort(); clearInterval(timer); };
  }, [autoRefresh, machineId, baseAddress, sessionBearer]);
  const view = state.kind === "ready" && (state.data.machineId !== machineId || state.bearer !== sessionBearer)
    ? { kind: "loading" as const } : state;
  const processedView = processed.kind === "ready" && (processed.data.machineId !== machineId || processed.bearer !== sessionBearer)
    ? { kind: "loading" as const } : processed;
  const selected = view.kind === "ready" ? view.data.series.find(x => x.mappingId === mapping) : undefined;
  return <section aria-label="Published machine activity">
    <h2>Published activity history</h2>
    <AcquisitionHealthPanel machineId={machineId} baseAddress={baseAddress} bearer={sessionBearer} />
    <section aria-label="Verified processed machine state">
      <h3>Verified processed machine state</h3>
      <label><input type="checkbox" checked={autoRefresh} disabled={!sessionBearer}
        onChange={event => setAutoRefresh(event.currentTarget.checked)} /> Refresh processed state every 30 seconds while this page is visible</label>
      <p>Historical checkpoint interpretation only. Live connectivity, freshness and processing coverage are not established.</p>
      {processedView.kind === "loading" && <p role="status">Reading verified processed state…</p>}
      {(processedView.kind === "denied" || processedView.kind === "unavailable" || processedView.kind === "failed") && <ErrorState kind={processedView.kind} />}
      {processedView.kind === "ready" && (processedView.data.states.length === 0
        ? <p>No verified processed state is published.</p>
        : <ul>{processedView.data.states.map(x => <li key={x.mappingId}>
          <strong>{x.sourceAddress}: {x.lastProcessedState ?? "Not qualified"}</strong>
          {" — last observed "}<time dateTime={x.lastObservedAt}>{new Date(x.lastObservedAt).toLocaleString()}</time>
          {" (generation "}{x.generation}{")"}
          {" — observation age at read: "}{Date.parse(x.lastObservedAt) > Date.parse(processedView.data.readAsOf)
            ? "future observation timestamp (clock discrepancy)"
            : `${Math.floor((Date.parse(processedView.data.readAsOf) - Date.parse(x.lastObservedAt)) / 1000).toLocaleString()} seconds`}
        </li>)}</ul>)}
    </section>
    <button type="button" disabled={view.kind === "loading" || !sessionBearer} onClick={() => setSeriesRefresh(x => x + 1)}>Refresh processed state and history</button>
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
        {selected && <div aria-label="Latest processed observation">
          <h3>Latest processed observation</h3>
          <dl>
            <dt>Observed at</dt>
            <dd><time dateTime={selected.lastObservedAt}>{new Date(selected.lastObservedAt).toLocaleString()}</time></dd>
            <dt>Activity source</dt><dd>{selected.sourceAddress}</dd>
            <dt>Processing generation</dt><dd>{selected.generation}</dd>
            <dt>Last processed activity state</dt><dd>{selected.lastProcessedState ?? "Not qualified"}</dd>
          </dl>
          <p>This timestamp and interpreted state come from the verified activity checkpoint. They are historical processing evidence, not a live machine-state, connection-health, or freshness claim.</p>
        </div>}
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
      <p>Newest periods first. Times use your browser timezone. Closed periods are historical derived evidence, not proof of physical observation completeness. Cross-batch publication provenance is not yet exposed per period.</p>
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

function AcquisitionHealthPanel({ machineId, baseAddress, bearer }: {
  machineId: string; baseAddress: string; bearer: string | null;
}) {
  const [enabled, setEnabled] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [state, setState] = useState<{ kind: "loading" | Failure } |
    { kind: "ready"; data: AcquisitionHealthResult }>({ kind: "loading" });
  useEffect(() => {
    if (!enabled || !bearer) return;
    const abort = new AbortController();
    let pending = false;
    const read = () => {
      if (pending || document.visibilityState === "hidden") return;
      pending = true;
      setState({ kind: "loading" });
      void readAcquisitionHealth(baseAddress, machineId, bearer, abort.signal)
        .then(data => { if (!abort.signal.aborted) setState({ kind: "ready", data }); })
        .catch(error => {
          if (abort.signal.aborted) return;
          const kind = failure(error);
          setState({ kind });
          if (kind === "denied") setAutoRefresh(false);
        })
        .finally(() => { pending = false; });
    };
    read();
    const timer = autoRefresh ? setInterval(read, 30000) : undefined;
    return () => { abort.abort(); if (timer !== undefined) clearInterval(timer); };
  }, [enabled, refresh, autoRefresh, machineId, baseAddress, bearer]);
  const [scopeMachine, scopeBase, scopeBearer] = [machineId, baseAddress, bearer];
  const healthView = state.kind === "ready" && (
    state.data.machineId !== scopeMachine || !scopeBearer || !enabled)
    ? { kind: "loading" as const } : state;
  return <section aria-label="Acquisition host health">
    <h3>Acquisition host health</h3>
    <p>Local acquisition-host telemetry only. Does not establish live CNC connectivity or machine activity.</p>
    <button type="button" disabled={!bearer} onClick={() => { setEnabled(true); setRefresh(x => x + 1); }}>
      {enabled ? "Refresh acquisition health" : "Check acquisition health"}
    </button>
    <label><input type="checkbox" checked={autoRefresh} disabled={!enabled || !bearer}
      onChange={event => setAutoRefresh(event.currentTarget.checked)} /> Refresh acquisition health every 30 seconds while visible</label>
    {enabled && healthView.kind === "loading" && <p role="status">Reading acquisition health…</p>}
    {enabled && healthView.kind === "unavailable" && <p role="status">Acquisition health is not configured or currently unavailable.</p>}
    {enabled && healthView.kind === "denied" && <p role="alert">Acquisition health access is not authorized.</p>}
    {enabled && healthView.kind === "failed" && <p role="alert">Unable to read acquisition health.</p>}
    {enabled && healthView.kind === "ready" && <dl>
      <dt>Host status</dt><dd>{healthView.data.health.status} ({healthView.data.health.operational ? "recent operational event" : "not operational or not established"})</dd>
      {healthView.data.health.ageSeconds !== undefined && <><dt>Snapshot age</dt><dd>{healthView.data.health.ageSeconds} seconds</dd></>}
      {healthView.data.health.snapshot?.lastSourceResponseAt && <><dt>Last MTConnect source response</dt>
        <dd><time dateTime={healthView.data.health.snapshot.lastSourceResponseAt}>{new Date(healthView.data.health.snapshot.lastSourceResponseAt).toLocaleString()}</time></dd></>}
      {healthView.data.health.snapshot?.lastDurableCommitAt && <><dt>Last durable SQL admission</dt>
        <dd><time dateTime={healthView.data.health.snapshot.lastDurableCommitAt}>{new Date(healthView.data.health.snapshot.lastDurableCommitAt).toLocaleString()}</time></dd></>}
      {healthView.data.health.reason && <><dt>Reason</dt><dd>{healthView.data.health.reason}</dd></>}
    </dl>}
  </section>;
}
