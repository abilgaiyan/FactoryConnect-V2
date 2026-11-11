import { useEffect, useMemo, useState } from "react";
import { readAllOperatorEvidenceMachines, type OperatorEvidenceMachine } from "../api/evidence/operator-evidence-machines-client.ts";

/**
 * First machine-administration surface: authenticated, read-only discovery.
 * This is an evidence-authorized subset, not a complete administrative inventory.
 * Registration controls will be enabled only after audited SQL write authority exists.
 */
export function MachineAdministrationPage({ sessionBearer, baseAddress }: {
  readonly sessionBearer: string | null;
  readonly baseAddress: string;
}) {
  const [revision, setRevision] = useState(0);
  const [search, setSearch] = useState("");
  const [state, setState] = useState<
    { kind: "loading" | "signIn" | "unavailable" } |
    { kind: "ready"; bearer: string; machines: readonly OperatorEvidenceMachine[]; complete: boolean }
  >({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    if (!sessionBearer) {
      setState({ kind: "signIn" });
      return () => controller.abort();
    }
    void readAllOperatorEvidenceMachines(baseAddress, sessionBearer, controller.signal)
      .then(result => {
        if (!controller.signal.aborted)
          setState({ kind: "ready", bearer: sessionBearer, machines: result.machines, complete: result.isComplete });
      })
      .catch(() => { if (!controller.signal.aborted) setState({ kind: "unavailable" }); });
    return () => controller.abort();
  }, [baseAddress, sessionBearer, revision]);

  const current = state.kind === "ready" && state.bearer !== sessionBearer
    ? { kind: "signIn" as const } : state;
  const matches = useMemo(() => current.kind === "ready" ? current.machines.filter(machine =>
    machine.displayName.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()) ||
    machine.machineId.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()) ||
    machine.siteId.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) : [],
    [current, search]);

  return <section aria-labelledby="machine-administration-title">
    <h1 id="machine-administration-title">Machine administration</h1>
    <p>Authorized machine discovery — read only. This list includes only machines for which your session has
      commissioning evidence access. It is not the complete registered machine inventory or proof of live connectivity.</p>
    <p>Site, production-line and machine registration is not yet enabled. No configuration or acquisition changes can be made here.</p>
    <button type="button" disabled={!sessionBearer || current.kind === "loading"}
      onClick={() => setRevision(value => value + 1)}>Refresh authorized machines</button>
    {current.kind === "loading" && <p role="status">Checking authorized machines…</p>}
    {current.kind === "signIn" && <p role="alert">Sign in to view authorized machines.</p>}
    {current.kind === "unavailable" && <p role="alert">Machine discovery is unavailable. No cached inventory is shown.</p>}
    {current.kind === "ready" && <>
      {!current.complete && <p role="alert">The authorized machine list is incomplete. Counts below are partial.</p>}
      <label htmlFor="admin-machine-search">Search by machine, site or ID</label>
      <input id="admin-machine-search" type="search" value={search}
        onChange={event => setSearch(event.currentTarget.value)} />
      <p role="status">{matches.length} matching machines from {current.machines.length} returned.</p>
      <div className="commissioning-table-scroll" role="region" aria-label="Authorized machine directory" tabIndex={0}>
        <table><thead><tr><th>Machine name</th><th>Machine ID</th><th>Site ID</th></tr></thead>
          <tbody>{matches.map(machine => <tr key={machine.machineId}>
            <td>{machine.displayName}</td><td><code>{machine.machineId}</code></td>
            <td><code>{machine.siteId}</code></td>
          </tr>)}</tbody>
        </table>
      </div>
      {matches.length === 0 && <p>No authorized machines match the current search.</p>}
    </>}
  </section>;
}
