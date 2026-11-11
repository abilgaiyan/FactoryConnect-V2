import { useEffect, useState } from "react";
import { EvidenceReadFailure, readRetainedEvidence, type RetainedEvidencePage } from "../api/evidence/retained-evidence-client.ts";

type State =
  | { kind: "loading" }
  | { kind: "ready"; page: RetainedEvidencePage; machineId: string; sessionBearer: string }
  | { kind: "denied" | "unavailable" | "failed" };

export function RetainedMachineEvidence({
  machineId, baseAddress, sessionBearer,
}: {
  machineId: string;
  baseAddress: string;
  sessionBearer: string | null;
}) {
  const [state, setState] = useState<State>({ kind: "loading" });
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    if (!sessionBearer) {
      setState({ kind: "denied" });
      return () => controller.abort();
    }
    void readRetainedEvidence(baseAddress, machineId, sessionBearer, controller.signal)
      .then(page => { if (!controller.signal.aborted) setState({ kind: "ready", page, machineId, sessionBearer }); })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setState({ kind: error instanceof EvidenceReadFailure
          ? error.status === 401 || error.status === 403 ? "denied"
            : error.status === 503 ? "unavailable" : "failed"
          : "failed" });
      });
    return () => controller.abort();
  }, [machineId, baseAddress, sessionBearer, refresh]);

  // Effects run after paint: never expose a previous machine/session result while
  // React is committing a new selection or a revoked local session.
  const visible = state.kind === "ready"
    && (state.machineId !== machineId || state.sessionBearer !== sessionBearer)
    ? { kind: "loading" as const } : state;

  return <section aria-label="Retained machine evidence">
    <h2>Accepted machine observations</h2>
    <p>Historical retained source evidence. This does not establish current connectivity, machine state, quantity or OEE.</p>
    <button type="button" disabled={!sessionBearer || visible.kind === "loading"} onClick={() => setRefresh(value => value + 1)}>Refresh accepted observations</button>
    {visible.kind === "loading" && <p role="status">Loading accepted evidence…</p>}
    {visible.kind === "denied" && <p role="alert">Commissioning evidence access is not authorized.</p>}
    {visible.kind === "unavailable" && <p role="alert">Evidence authority is unavailable. No zero or healthy state is assumed.</p>}
    {visible.kind === "failed" && <p role="alert">Unable to read accepted evidence.</p>}
    {visible.kind === "ready" && (visible.page.rows.length === 0
      ? <p>No retained observations for this machine and stream.</p>
      : <div className="activity-table-scroll" tabIndex={0} role="region" aria-label="Accepted observation table">
          <table>
            <thead><tr><th>Signal</th><th>Raw value</th><th>Quality</th><th>Source timestamp</th><th>Instance / sequence</th><th>Receipt</th></tr></thead>
            <tbody>{visible.page.rows.map(row => <tr key={`${row.instanceId}:${row.sequence}:${row.batchId}`}>
              <td>{row.address}</td>
              <td><code style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{row.rawValue}</code></td>
              <td>{row.quality === "unavailable" ? "Unavailable" : "Source reported"}</td>
              <td><time dateTime={row.observedAt}>{row.sourceTimestamp}</time></td>
              <td>{row.instanceId} / {row.sequence}</td>
              <td><small>{row.batchId}</small>
                <details>
                  <summary>Observation provenance</summary>
                  <dl>
                    <dt>Mapping ID</dt><dd><code>{row.mappingId}</code></dd>
                    <dt>Batch ID</dt><dd><code>{row.batchId}</code></dd>
                    <dt>Runtime acknowledgement ID</dt><dd><code>{row.acknowledgementId}</code></dd>
                    <dt>Source document SHA-256</dt><dd><code style={{ overflowWrap: "anywhere" }}>{row.documentHash}</code></dd>
                    <dt>Receipt time</dt><dd><time dateTime={row.receivedAt}>{row.receivedAt}</time></dd>
                    <dt>Observation time</dt><dd><time dateTime={row.observedAt}>{row.observedAt}</time></dd>
                  </dl>
                </details>
              </td>
            </tr>)}</tbody>
          </table>
        </div>)}
  </section>;
}
