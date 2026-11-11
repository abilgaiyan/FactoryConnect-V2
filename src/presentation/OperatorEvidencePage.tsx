import { useEffect, useState } from "react";
import { readAllOperatorEvidenceMachines, type OperatorEvidenceMachine } from "../api/evidence/operator-evidence-machines-client.ts";
import { PublishedMachineActivity } from "./PublishedMachineActivity.tsx";
import { RetainedMachineEvidence } from "./RetainedMachineEvidence.tsx";

export function OperatorEvidencePage({
  sessionBearer, baseAddress,
}: { readonly sessionBearer: string | null; readonly baseAddress: string }) {
  const [state, setState] = useState<
    { kind: "loading" | "denied" | "failed" } |
    { kind: "ready"; bearer: string; machines: readonly OperatorEvidenceMachine[]; isComplete: boolean }
  >({ kind: "loading" });
  const [selectedMachine, setSelectedMachine] = useState("");
  const [machineSearch, setMachineSearch] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    setSelectedMachine("");
    setMachineSearch("");
    if (!sessionBearer) {
      setState({ kind: "denied" });
      return () => controller.abort();
    }
    void readAllOperatorEvidenceMachines(baseAddress, sessionBearer, controller.signal)
      .then(page => { if (!controller.signal.aborted) setState({ kind: "ready", machines: page.machines, isComplete: page.isComplete, bearer: sessionBearer }); })
      .catch(() => { if (!controller.signal.aborted) setState({ kind: "failed" }); });
    return () => controller.abort();
  }, [baseAddress, sessionBearer]);
  const visible = state.kind === "ready" && state.bearer !== sessionBearer
    ? { kind: sessionBearer ? "loading" as const : "denied" as const } : state;
  const machine = visible.kind === "ready"
    ? visible.machines.find(m => m.machineId === selectedMachine) : undefined;
  const matchingMachines = visible.kind === "ready" ? visible.machines.filter(m =>
    m.displayName.toLowerCase().includes(machineSearch.trim().toLowerCase()) ||
    m.machineId.toLowerCase().includes(machineSearch.trim().toLowerCase())) : [];
  return <section aria-label="Operator evidence">
    <h1>Operator machine evidence</h1>
    <p>Only machines currently authorized for this human session are selectable. Historical evidence is not live machine health.</p>
    {visible.kind === "loading" && <p role="status">Checking current machine permissions…</p>}
    {visible.kind === "denied" && <p role="alert">Sign in with a trusted identity to view accepted observations.</p>}
    {visible.kind === "failed" && <p role="alert">Authorized machine selection is unavailable.</p>}
    {visible.kind === "ready" && !visible.isComplete && <p role="status">Machine list is incomplete: additional authorized machines may not be selectable until continuation is enabled.</p>}
    {visible.kind === "ready" && (visible.machines.length === 0
      ? <p>No machines currently authorized for commissioning evidence.</p>
      : <>
        <label htmlFor="operator-machine-search">Find authorized machine</label>
        <input id="operator-machine-search" type="search" value={machineSearch} onChange={event => setMachineSearch(event.currentTarget.value)} placeholder="Search name or machine ID" />
        <p role="status">{matchingMachines.length} of {visible.machines.length} authorized machines match.</p>
        <label htmlFor="operator-evidence-machine">Authorized machine</label>
        <select id="operator-evidence-machine" value={machine?.machineId ?? ""}
          onChange={event => setSelectedMachine(event.currentTarget.value)}>
          <option value="">Select machine</option>
          {matchingMachines.map(m => <option key={m.machineId} value={m.machineId}>{m.displayName} ({m.machineId})</option>)}
        </select>
        {matchingMachines.length === 0 && <p>No authorized machines match the search.</p>}
        {machine && <div key={machine.machineId}>
          <PublishedMachineActivity machineId={machine.machineId} baseAddress={baseAddress} sessionBearer={visible.bearer} />
          <RetainedMachineEvidence machineId={machine.machineId} baseAddress={baseAddress} sessionBearer={visible.bearer} />
        </div>}
      </>)}
  </section>;
}
