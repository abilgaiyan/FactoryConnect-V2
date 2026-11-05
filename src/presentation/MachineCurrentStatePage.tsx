import { formatReportTimestamp } from "./date-formatting.ts";
import type { DashboardRuntimeSource } from "../application/runtime-configuration.ts";
import type { MachineCurrentStateViewState } from "../application/use-machine-current-state.ts";

export function MachineCurrentStatePage(props: {
  source: DashboardRuntimeSource;
  state: MachineCurrentStateViewState;
  refresh: () => Promise<void>;
}) {
  const { source, state, refresh } = props;

  if (state.kind === "loading") {
    return <p role="status">Loading authoritative current state…</p>;
  }
  if (state.kind === "authorityUnavailable") {
    return <><p role="alert">Authoritative current machine state is temporarily unavailable.</p><button type="button" onClick={() => void refresh()}>Retry</button></>;
  }
  if (state.kind === "invalidMachine") {
    return <p role="alert">The configured machine identity was rejected by the current-state API.</p>;
  }
  if (state.kind === "failed") {
    return <><p role="alert">Current machine state could not be retrieved because of a transport or protocol failure.</p><button type="button" onClick={() => void refresh()}>Retry</button></>;
  }

  const response = state.data;
  return (
    <>
      <p>{source.displayName}</p>
      <dl>
        <dt>Outcome</dt><dd>{response.outcome}</dd>
        <dt>Coverage</dt><dd>{response.coverage}</dd>
        {response.evidence === null ? (
          <><dt>Evidence</dt><dd>No evidence</dd></>
        ) : (
          <>
            <dt>Machine state</dt><dd>{response.evidence.machineState}</dd>
            <dt>Freshness</dt><dd>{response.evidence.freshness}</dd>
            <dt>Usability</dt><dd>{response.evidence.usability}</dd>
            <dt>Read as of</dt><dd><time dateTime={response.evidence.readAsOf}>{formatReportTimestamp(response.evidence.readAsOf)}</time></dd>
          </>
        )}
      </dl>
      <button type="button" disabled={state.refreshing} onClick={() => void refresh()}>
        {state.refreshing ? "Refreshing…" : "Refresh"}
      </button>
    </>
  );
}
