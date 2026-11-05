import {
  mapProductionDayOverview,
  type ProductionDayMetricDisplay,
  type ProductionDayMetricSet,
} from "../application/production-day-presentation.ts";
import { mapShiftPerformanceOverview } from "./shift-performance-projector.ts";
import type { PresentedMetric, ShiftPerformanceShift } from "./shift-performance-model.ts";
import {
  DailyReportCompositionFailure,
  type DailyReportCell,
  type DailyReportCompositionInput,
  type DailyReportMachine,
  type DailyReportModel,
  type DailyReportShift,
} from "./daily-report-model.ts";

const metricOrder = [
  "availability", "utilization", "performance", "quality", "oee",
] as const;

export function composeDailyReport(input: DailyReportCompositionInput): DailyReportModel {
  const productionDayOverview = mapProductionDayOverview({
    productionDay: input.productionDay,
    sources: input.sources,
    result: input.productionDayResult,
  });
  const shiftOverview = mapShiftPerformanceOverview(
    input.productionDay,
    input.sources,
    input.shiftResult,
  );

  if (productionDayOverview.productionDay !== shiftOverview.productionDay) {
    throw new DailyReportCompositionFailure(
      "inconsistent-production-day",
      "Daily Report inputs resolved to different production days.",
    );
  }

  const shiftMachines = new Map<string, ShiftMachineProjection>();
  for (const group of shiftOverview.groups) {
    for (const machine of group.machines) {
      const identity = sourceIdentity(machine.machineId, machine.processorId);
      if (shiftMachines.has(identity)) {
        throw new DailyReportCompositionFailure(
          "inconsistent-source-population",
          "Daily Report shift presentation contained a duplicated configured source.",
        );
      }

      shiftMachines.set(identity, {
        groupName: group.groupName,
        siteId: machine.siteId,
        productionLineId: machine.productionLineId,
        displayName: machine.displayName,
        shifts: machine.shifts,
      });
    }
  }

  const groups = productionDayOverview.groups.map(group => ({
    groupName: group.groupName,
    machines: group.machines.map(machine => {
      const shifts = shiftMachines.get(sourceIdentity(machine.machineId, machine.processorId));
      if (shifts === undefined
        || shifts.groupName !== machine.groupName
        || shifts.displayName !== machine.displayName) {
        throw new DailyReportCompositionFailure(
          "inconsistent-source-population",
          "Daily Report production-day and shift presentations did not resolve the same configured source population.",
        );
      }

      shiftMachines.delete(sourceIdentity(machine.machineId, machine.processorId));
      return projectMachine(machine, shifts);
    }),
  }));

  if (shiftMachines.size !== 0) {
    throw new DailyReportCompositionFailure(
      "inconsistent-source-population",
      "Daily Report shift presentation contained configured sources not present in the production-day presentation.",
    );
  }

  return {
    productionDay: input.productionDay,
    groups,
  };
}

interface ShiftMachineProjection {
  readonly groupName: string | null;
  readonly siteId: string;
  readonly productionLineId: string;
  readonly displayName: string;
  readonly shifts: readonly ShiftPerformanceShift[];
}

function projectMachine(
  machine: {
    readonly machineId: string;
    readonly processorId: string;
    readonly displayName: string;
    readonly groupName: string | null;
    readonly displayOrder: number;
    readonly metrics: ProductionDayMetricSet;
  },
  shiftMachine: ShiftMachineProjection,
): DailyReportMachine {
  return {
    machineId: machine.machineId,
    processorId: machine.processorId,
    siteId: shiftMachine.siteId,
    productionLineId: shiftMachine.productionLineId,
    displayName: machine.displayName,
    groupName: machine.groupName,
    displayOrder: machine.displayOrder,
    productionDayCells: metricOrder.map(property => projectProductionDayCell(machine.metrics[property])),
    shifts: shiftMachine.shifts.map(projectShift),
  };
}

function projectShift(shift: ShiftPerformanceShift): DailyReportShift {
  return {
    shift: shift.shift,
    productionLineId: shift.productionLineId,
    sourceRevision: shift.sourceRevision,
    cells: metricOrder.map(property => projectShiftCell(shift[property])),
  };
}

function projectProductionDayCell(metric: ProductionDayMetricDisplay): DailyReportCell {
  switch (metric.kind) {
    case "calculated":
      return {
        state: "calculated",
        metricKey: metric.metricKey,
        version: metric.version,
        value: metric.value,
        unit: metric.unit,
        sourceRevision: metric.sourceRevision,
      };
    case "unavailable":
    case "insufficient-evidence":
      return {
        state: metric.kind,
        metricKey: metric.metricKey,
        version: metric.version,
        reasonCode: metric.reasonCode,
        reasonOperandName: metric.reasonOperandName,
        sourceRevision: metric.sourceRevision,
      };
    case "missing":
      return {
        state: "missing",
        metricKey: metric.metricKey,
        version: metric.version,
      };
  }
}

function projectShiftCell(metric: PresentedMetric): DailyReportCell {
  switch (metric.state) {
    case "calculated":
      return {
        state: "calculated",
        metricKey: metric.metricKey,
        version: metric.version,
        value: metric.value,
        unit: metric.unit,
      };
    case "unavailable":
    case "insufficient-evidence":
      return {
        state: metric.state,
        metricKey: metric.metricKey,
        version: metric.version,
        reasonCode: metric.reasonCode,
        reasonOperandName: metric.reasonOperandName,
      };
    case "missing":
      return {
        state: "missing",
        metricKey: metric.metricKey,
        version: metric.version,
      };
  }
}

function sourceIdentity(machineId: string, processorId: string): string {
  return JSON.stringify([processorId, machineId]);
}
