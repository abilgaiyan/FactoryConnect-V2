import type { ProductionDayShiftPage } from "../api/reporting/index.ts";
import type { OverviewMetricKey } from "../application/operational-metric-identities.ts";

export type MetricSourceRevision = NonNullable<
  ProductionDayShiftPage["items"][number]["sourceRevision"]
>;

export type AuthoritativeShiftDescriptor =
  ProductionDayShiftPage["items"][number]["shift"];

export type MetricValue = Exclude<
  ProductionDayShiftPage["items"][number]["metrics"][number]["value"],
  null
>;

export type ShiftOverviewMetricKey = OverviewMetricKey;

interface PresentedMetricIdentity {
  readonly metricKey: ShiftOverviewMetricKey;
  readonly version: "1.0";
}

export type PresentedMetric =
  | (PresentedMetricIdentity & {
      readonly state: "calculated";
      readonly value: MetricValue;
      readonly unit: string;
    })
  | (PresentedMetricIdentity & {
      readonly state: "unavailable";
      readonly reasonCode: string | null;
      readonly reasonOperandName: string | null;
    })
  | (PresentedMetricIdentity & {
      readonly state: "insufficient-evidence";
      readonly reasonCode: string | null;
      readonly reasonOperandName: string | null;
    })
  | (PresentedMetricIdentity & {
      readonly state: "missing";
    });

export interface ShiftPerformanceOverview {
  readonly productionDay: string;
  readonly groups: readonly ShiftPerformanceGroup[];
}

export interface ShiftPerformanceGroup {
  readonly groupName: string | null;
  readonly machines: readonly ShiftPerformanceMachine[];
}

export interface ShiftPerformanceMachine {
  readonly machineId: string;
  readonly processorId: string;
  readonly siteId: string;
  readonly productionLineId: string;
  readonly displayName: string;
  readonly shifts: readonly ShiftPerformanceShift[];
}

export interface ShiftPerformanceShift {
  readonly shift: AuthoritativeShiftDescriptor;
  readonly productionLineId: string;
  readonly sourceRevision: MetricSourceRevision | null;
  readonly availability: PresentedMetric;
  readonly utilization: PresentedMetric;
  readonly performance: PresentedMetric;
  readonly quality: PresentedMetric;
  readonly oee: PresentedMetric;
}

export type ShiftPresentationContractFailureReason =
  | "unexpected-source"
  | "unexpected-site"
  | "unexpected-production-day"
  | "unexpected-production-line"
  | "unexpected-context"
  | "unexpected-source-revision"
  | "duplicate-occurrence"
  | "inconsistent-occurrence-descriptor"
  | "out-of-order-occurrence"
  | "unexpected-metric"
  | "duplicate-metric"
  | "malformed-metric-state";

export class ShiftPresentationContractFailure extends Error {
  readonly reason: ShiftPresentationContractFailureReason;

  constructor(reason: ShiftPresentationContractFailureReason, message: string) {
    super(message);
    this.name = "ShiftPresentationContractFailure";
    this.reason = reason;
  }
}
