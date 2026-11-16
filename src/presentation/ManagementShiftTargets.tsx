import { useEffect, useState } from "react";
import type { ShiftPerformanceMachine, ShiftPerformanceShift } from "./shift-performance-model.ts";

interface Target {
  shiftId: string;
  targetQuantity: number;
  quantityUnit: string;
  revision: number;
  productionOrder: string | null;
  partNumber: string | null;
  operation: string | null;
  recordedAtUtc: string;
}
interface TargetResponse {
  machineId: string;
  businessDate: string;
  isSample: boolean;
  targets: Target[];
}
function csvCell(value: string | number): string {
  const text = String(value);
  // Keep factory-entered order, part and operation identifiers as text in spreadsheet applications.
  const safe = /^\s*[=+@-]/.test(text) ? "'" + text : text;
  return '"' + safe.replace(/"/g, '""') + '"';
}
function planCsv(rows: readonly (readonly (string | number)[])[]): string {
  return "\uFEFF" + rows.map(row => row.map(csvCell).join(",")).join("\r\n");
}
export default function ManagementShiftTargets({ productionDay, selected }: {
  readonly productionDay: string;
  readonly selected: readonly { machine: ShiftPerformanceMachine; shift: ShiftPerformanceShift }[];
}) {
  const [open, setOpen] = useState(false);
  const [retry, setRetry] = useState(0);
  const [state, setState] = useState<{ targets: Record<string, Target[]>; errors: Record<string, string> } | null>(null);
  const machineIds = [...new Set(selected.map(({ machine }) => machine.machineId))].sort();
  const scopeKey = JSON.stringify([productionDay, machineIds]);
  useEffect(() => {
    if (!open || machineIds.length === 0) { setState(null); return; }
    const controller = new AbortController();
    setState(null);
    void Promise.all(machineIds.map(async machineId => {
      try {
        const response = await fetch(`/api/planning/v2/machines/${encodeURIComponent(machineId)}/targets?businessDate=${encodeURIComponent(productionDay)}`,
          { credentials: "include", signal: controller.signal });
        if (!response.ok) throw Error(`HTTP ${response.status}`);
        const data: TargetResponse = await response.json();
        if (data.isSample || data.machineId.toLowerCase() !== machineId.toLowerCase() ||
          data.businessDate !== productionDay || !Array.isArray(data.targets)) throw Error("Target scope mismatch");
        return { machineId, targets: data.targets, error: "" };
      } catch (error) {
        return { machineId, targets: [], error: String(error) };
      }
    })).then(results => {
      if (controller.signal.aborted) return;
      setState({
        targets: Object.fromEntries(results.map(result => [result.machineId, result.targets])),
        errors: Object.fromEntries(results.filter(result => result.error).map(result => [result.machineId, result.error])),
      });
    });
    return () => controller.abort();
  }, [open, scopeKey, retry]);
  const exportRows: (string | number)[][] = [
    ["Production date", "Site", "Production line", "Machine", "Machine ID", "Reporting processor", "Shift", "Shift assignment", "Shift start UTC", "Shift end UTC", "Target status", "Target quantity", "Unit", "Revision", "Production order", "Part", "Operation", "Revision recorded UTC", "Actual status", "Achievement status"],
    ...selected.map(({ machine, shift }) => {
      const error = state?.errors[machine.machineId];
      const target = state?.targets[machine.machineId]?.find(item => item.shiftId.toLowerCase() === shift.shift.shiftId.toLowerCase());
      return [productionDay, machine.siteId, machine.productionLineId, machine.displayName, machine.machineId, machine.processorId,
        shift.shift.shiftId, shift.shift.shiftScheduleAssignmentId, shift.shift.startsAtUtc, shift.shift.endsAtUtc,
        error ? "Unavailable: " + error : target ? "Recorded" : "No recorded target",
        target?.targetQuantity ?? "", target?.quantityUnit ?? "", target?.revision ?? "",
        target?.productionOrder ?? "", target?.partNumber ?? "", target?.operation ?? "", target?.recordedAtUtc ?? "",
        "Not available", "Not calculated"];
    }),
  ];
  return <section aria-label="Management SQL production targets">
    <h3>Recorded production targets · SQL planning</h3>
    <p>Compare configured machine-shift targets for this production day. Actual production and target achievement are not provided by this read and are not estimated.</p>
    <button type="button" onClick={() => setOpen(value => !value)}>{open ? "Hide SQL targets" : "Load SQL targets for selected machines"}</button>
    {open && (state === null ? <p role="status">Loading recorded targets…</p> : <>
      <button type="button" onClick={() => setRetry(value => value + 1)}>Refresh SQL targets</button>
      <p><a download="factoryconnect-management-production-plan.csv" href={"data:text/csv;charset=utf-8," + encodeURIComponent(planCsv(exportRows))}>Export selected SQL production plan CSV</a></p>

      <table><thead><tr><th>Machine</th><th>Shift</th><th>Target</th><th>Production order</th><th>Part</th><th>Operation</th><th>Revision recorded UTC</th><th>Actual</th><th>Achievement</th></tr></thead>
        <tbody>{selected.map(({ machine, shift }) => {
          const error = state.errors[machine.machineId];
          const target = state.targets[machine.machineId]?.find(item => item.shiftId.toLowerCase() === shift.shift.shiftId.toLowerCase());
          return <tr key={JSON.stringify([machine.machineId, machine.processorId, shift.shift.shiftScheduleAssignmentId, shift.shift.startsAtUtc])}>
            <th scope="row">{machine.displayName}</th><td>{shift.shift.shiftId} · {shift.shift.startsAtUtc}</td>
            <td>{error ? "Unavailable: " + error : target ? target.targetQuantity + " " + target.quantityUnit + " · rev " + target.revision : "No recorded target"}</td>
            <td>{target?.productionOrder || "Not specified"}</td><td>{target?.partNumber || "Not specified"}</td><td>{target?.operation || "Not specified"}</td><td>{target?.recordedAtUtc || "Not available"}</td><td>Not available</td><td>Not calculated</td>
          </tr>;
        })}</tbody>
      </table>
    </>)}
  </section>;
}
