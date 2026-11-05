import type { DashboardRuntimeSource } from "../application/runtime-configuration.ts";
import type { AuthoritativeProductionDayResult } from "../application/production-day-reporting.ts";
import type { AuthoritativeProductionDayShiftResult } from "../application/production-day-shift-reporting.ts";
import type {
  MetricSourceRevision as ProductionDayMetricSourceRevision,
  MetricValue as ProductionDayMetricValue,
  OverviewMetricKey,
} from "../application/production-day-presentation.ts";
import type {
  AuthoritativeShiftDescriptor,
  MetricSourceRevision as ShiftMetricSourceRevision,
  MetricValue as ShiftMetricValue,
} from "./shift-performance-model.ts";

export type DailyReportMetricKey = OverviewMetricKey;
export type DailyReportCellState =
  | "calculated"
  | "unavailable"
  | "insufficient-evidence"
  | "missing";

interface DailyReportCellIdentity {
  readonly metricKey: DailyReportMetricKey;
  readonly version: "1.0";
}

export type DailyReportCell =
  | (DailyReportCellIdentity & {
      readonly state: "calculated";
      readonly value: ProductionDayMetricValue | ShiftMetricValue;
      readonly unit: string;
      readonly sourceRevision?: ProductionDayMetricSourceRevision;
    })
  | (DailyReportCellIdentity & {
      readonly state: "unavailable" | "insufficient-evidence";
      readonly reasonCode: string | null;
      readonly reasonOperandName: string | null;
      readonly sourceRevision?: ProductionDayMetricSourceRevision;
    })
  | (DailyReportCellIdentity & {
      readonly state: "missing";
    });

export interface DailyReportModel {
  readonly productionDay: string;
  readonly groups: readonly DailyReportGroup[];
}

export interface DailyReportGroup {
  readonly groupName: string | null;
  readonly machines: readonly DailyReportMachine[];
}

export interface DailyReportMachine {
  readonly machineId: string;
  readonly processorId: string;
  readonly siteId: string;
  readonly productionLineId: string;
  readonly displayName: string;
  readonly groupName: string | null;
  readonly displayOrder: number;
  readonly productionDayCells: readonly DailyReportCell[];
  readonly shifts: readonly DailyReportShift[];
}

export interface DailyReportShift {
  readonly shift: AuthoritativeShiftDescriptor;
  readonly productionLineId: string;
  readonly sourceRevision: ShiftMetricSourceRevision | null;
  readonly cells: readonly DailyReportCell[];
}

export interface DailyReportCompositionInput {
  readonly productionDay: string;
  readonly sources: readonly DashboardRuntimeSource[];
  readonly productionDayResult: AuthoritativeProductionDayResult;
  readonly shiftResult: AuthoritativeProductionDayShiftResult;
}

export type DailyReportCompositionFailureReason =
  | "inconsistent-production-day"
  | "inconsistent-source-population";

export class DailyReportCompositionFailure extends Error {
  readonly reason: DailyReportCompositionFailureReason;

  constructor(reason: DailyReportCompositionFailureReason, message: string) {
    super(message);
    this.name = "DailyReportCompositionFailure";
    this.reason = reason;
  }
}
