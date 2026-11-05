import type { ProductionDayShiftPage } from "../api/reporting/index.ts";
import type { AuthoritativeProductionDayShiftResult } from "../application/production-day-shift-reporting.ts";
import type { DashboardRuntimeSource } from "../application/runtime-configuration.ts";
import { validateProductionDayShiftAuthority } from "./shift-performance-authority-validator.ts";
import type {
  PresentedMetric,
  ShiftOverviewMetricKey,
  ShiftPerformanceMachine,
  ShiftPerformanceOverview,
  ShiftPerformanceShift,
} from "./shift-performance-model.ts";

type ShiftReport = ProductionDayShiftPage["items"][number];
type ShiftMetric = ShiftReport["metrics"][number];

const metricDefinitions = [
  ["availability", "availability"],
  ["utilization.elr", "utilization"],
  ["performance", "performance"],
  ["quality", "quality"],
  ["oee", "oee"],
] as const satisfies readonly (readonly [ShiftOverviewMetricKey, keyof Pick<ShiftPerformanceShift, "availability" | "utilization" | "performance" | "quality" | "oee">])[];

export function mapShiftPerformanceOverview(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  result: AuthoritativeProductionDayShiftResult,
): ShiftPerformanceOverview {
  const validated = validateProductionDayShiftAuthority(productionDay, sources, result);
  return projectValidatedShiftPerformanceOverview(productionDay, sources, validated.items);
}

function projectValidatedShiftPerformanceOverview(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  items: ProductionDayShiftPage["items"],
): ShiftPerformanceOverview {
  const reportsBySource = new Map<string, ShiftReport[]>();
  for (const report of items) {
    const key = sourceKey(report.machineId, report.processorId);
    const reports = reportsBySource.get(key);
    if (reports === undefined) {
      reportsBySource.set(key, [report]);
    } else {
      reports.push(report);
    }
  }

  const groups: MutableGroup[] = [];
  const groupsByName = new Map<string | null, MutableGroup>();

  for (const source of sources) {
    let group = groupsByName.get(source.groupName);
    if (group === undefined) {
      group = { groupName: source.groupName, machines: [] };
      groupsByName.set(source.groupName, group);
      groups.push(group);
    }

    const reports = reportsBySource.get(sourceKey(source.machineId, source.processorId)) ?? [];
    group.machines.push(projectMachine(source, reports));
  }

  return {
    productionDay,
    groups: groups.map(group => ({
      groupName: group.groupName,
      machines: group.machines,
    })),
  };
}

interface MutableGroup {
  readonly groupName: string | null;
  readonly machines: ShiftPerformanceMachine[];
}

function projectMachine(
  source: DashboardRuntimeSource,
  reports: readonly ShiftReport[],
): ShiftPerformanceMachine {
  return {
    machineId: source.machineId,
    processorId: source.processorId,
    siteId: source.siteId,
    productionLineId: source.productionLineId,
    displayName: source.displayName,
    shifts: reports.map(projectShift),
  };
}

function projectShift(report: ShiftReport): ShiftPerformanceShift {
  const metrics = new Map<ShiftOverviewMetricKey, PresentedMetric>();
  for (const metric of report.metrics) {
    metrics.set(metric.metricKey as ShiftOverviewMetricKey, projectMetric(metric));
  }

  const slots = Object.fromEntries(
    metricDefinitions.map(([metricKey, property]) => [property, metrics.get(metricKey) ?? missingMetric(metricKey)]),
  ) as Pick<ShiftPerformanceShift, "availability" | "utilization" | "performance" | "quality" | "oee">;

  return {
    shift: report.shift,
    productionLineId: report.productionLineId,
    sourceRevision: report.sourceRevision,
    ...slots,
  };
}

function projectMetric(metric: ShiftMetric): PresentedMetric {
  const identity = {
    metricKey: metric.metricKey as ShiftOverviewMetricKey,
    version: "1.0" as const,
  };

  if (metric.status === "calculated") {
    return {
      ...identity,
      state: "calculated",
      value: metric.value!,
      unit: metric.unit,
    };
  }

  return {
    ...identity,
    state: metric.status,
    reasonCode: metric.reasonCode,
    reasonOperandName: metric.reasonOperandName,
  };
}

function missingMetric(metricKey: ShiftOverviewMetricKey): PresentedMetric {
  return {
    metricKey,
    version: "1.0",
    state: "missing",
  };
}

function sourceKey(machineId: string, processorId: string): string {
  return `${machineId}\u0000${processorId}`;
}
