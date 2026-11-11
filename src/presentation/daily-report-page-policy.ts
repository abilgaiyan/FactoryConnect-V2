import type { DailyReportLifecycleState } from "../application/daily-report-lifecycle.ts";
import type { DailyReportModel } from "./daily-report-model.ts";

export function canPrintDailyReportState(state: DailyReportLifecycleState): boolean {
  return state.kind === "success" && state.canPrint;
}

export function visibleDailyReportModel(state: DailyReportLifecycleState): DailyReportModel | null {
  switch (state.kind) {
    case "success":
      return state.model;
    case "refreshing":
      return state.previous;
    case "roster-prerequisite-failure":
    case "reporting-failure":
    case "presentation-contract-failure":
    case "invalid-request":
      return state.previous ?? null;
    default:
      return null;
  }
}

/**
 * View-only filtering. Never recompute metrics or convert unavailable evidence to zero.
 * Empty groups are omitted; the original authoritative model remains unchanged.
 */
export function filterDailyReportModel(
  model: DailyReportModel,
  productionLineId: string,
  machineId: string,
  shiftId = "",
): DailyReportModel {
  if (productionLineId === "" && machineId === "" && shiftId === "") {
    return model;
  }

  return {
    ...model,
    groups: model.groups.map(group => ({
      ...group,
      machines: group.machines.filter(machine =>
        (productionLineId === "" || machine.productionLineId === productionLineId) &&
        (machineId === "" || machine.machineId === machineId) &&
        (shiftId === "" || machine.shifts.some(shift => shift.shift.shiftId === shiftId)))
        .map(machine => shiftId === "" ? machine : {
          ...machine,
          shifts: machine.shifts.filter(shift => shift.shift.shiftId === shiftId),
        }),
    })).filter(group => group.machines.length > 0),
  };
}
