import { useEffect, useState } from "react";
import { readOperatorEvidenceMachines, type OperatorEvidenceMachine } from "../api/evidence/operator-evidence-machines-client.ts";
import { RetainedMachineEvidence } from "./RetainedMachineEvidence.tsx";

export function OperatorEvidencePage({
  sessionBearer, baseAddress,
}: { readonly sessionBearer: string | null; readonly baseAddress: string }) {
  const [state, setState] = useState<
    { kind: "loading" | "denied" | "failed" } |
    { kind: "ready"; bearer: string; machines: readonly OperatorEvidenceMachine[] }
  >({ kind: "loading" });
  const [selectedMachine, setSelectedMachine] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    setSelectedMachine("");
    if (!sessionBearer) {
      setState({ kind: "denied" });
      return () => controller.abort();
    }
    void readOperatorEvidenceMachines(baseAddress, sessionBearer, controller.signal)
      .then(machines => { if (!controller.signal.aborted) setState({ kind: "ready", machines, bearer: sessionBearer }); })
      .catch(() => { if (!controller.signal.aborted) setState({ kind: "failed" }); });
    return () => controller.abort();
  }, [baseAddress, sessionBearer]);
  const visible = state.kind === "ready" && state.bearer !== sessionBearer
    ? { kind: sessionBearer ? "loading" as const : "denied" as const } : state;
  const machine = visible.kind === "ready"
    ? visible.machines.find(m => m.machineId === selectedMachine) : undefined;
  return <section aria-label="Operator evidence">
    <h1>Operator machine evidence</h1>
    <p>Only machines currently authorized for this human session are selectable. Historical evidence is not live machine health.</p>
    {visible.kind === "loading" && <p role="status">Checking current machine permissions…</p>}
    {visible.kind === "denied" && <p role="alert">Sign in with a trusted identity to view accepted observations.</p>}
    {visible.kind === "failed" && <p role="alert">Authorized machine selection is unavailable.</p>}
    {visible.kind === "ready" && (visible.machines.length === 0
      ? <p>No machines currently authorized for commissioning evidence.</p>
      : <>
        <label htmlFor="operator-evidence-machine">Authorized machine</label>
        <select id="operator-evidence-machine" value={machine?.machineId ?? ""}
          onChange={event => setSelectedMachine(event.currentTarget.value)}>
          <option value="">Select machine</option>
          {visible.machines.map(m => <option key={m.machineId} value={m.machineId}>{m.displayName} ({m.machineId})</option>)}
        </select>
        {machine && <RetainedMachineEvidence key={machine.machineId} machineId={machine.machineId}
          baseAddress={baseAddress} sessionBearer={visible.bearer} />}
      </>)}
  </section>;
}
