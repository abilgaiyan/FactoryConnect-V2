import type { OperationalMetricPage } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";
import {
  isProductionDaySelection,
  type AuthoritativeProductionDayResult,
} from "./production-day-reporting.ts";

type OperationalMetricItem = OperationalMetricPage["items"][number];
export type MetricValue = Exclude<OperationalMetricItem["value"], null>;
export type MetricSourceRevision = OperationalMetricItem["sourceRevision"];
export type OverviewMetricKey = "availability" | "utilization.elr" | "performance" | "quality" | "oee";

export type ProductionDayPresentationFailureReason =
  | "duplicate-result"
  | "unexpected-source"
  | "unexpected-scope"
  | "unexpected-period"
  | "unexpected-context"
  | "unexpected-metric"
  | "invalid-result-shape";

export interface ProductionDayPresentationInput {
  readonly productionDay: string;
  readonly sources: readonly DashboardRuntimeSource[];
  readonly result: AuthoritativeProductionDayResult;
}

export interface ProductionDayOverviewModel {
  readonly productionDay: string;
  readonly groups: readonly ProductionDayMachineGroup[];
}

export interface ProductionDayMachineGroup {
  readonly groupName: string | null;
  readonly machines: readonly ProductionDayMachineOverview[];
}

export interface ProductionDayMachineOverview {
  readonly machineId: string;
  readonly processorId: string;
  readonly displayName: string;
  readonly groupName: string | null;
  readonly displayOrder: number;
  readonly metrics: ProductionDayMetricSet;
}

export interface ProductionDayMetricSet {
  readonly availability: ProductionDayMetricDisplay;
  readonly utilization: ProductionDayMetricDisplay;
  readonly performance: ProductionDayMetricDisplay;
  readonly quality: ProductionDayMetricDisplay;
  readonly oee: ProductionDayMetricDisplay;
}

export type ProductionDayMetricDisplay =
  | CalculatedMetricDisplay
  | UnavailableMetricDisplay
  | InsufficientEvidenceMetricDisplay
  | MissingMetricDisplay;

export interface CalculatedMetricDisplay {
  readonly kind: "calculated";
  readonly metricKey: OverviewMetricKey;
  readonly version: "1.0";
  readonly value: MetricValue;
  readonly unit: string;
  readonly sourceRevision: MetricSourceRevision;
}

export interface UnavailableMetricDisplay {
  readonly kind: "unavailable";
  readonly metricKey: OverviewMetricKey;
  readonly version: "1.0";
  readonly reasonCode: string | null;
  readonly reasonOperandName: string | null;
  readonly sourceRevision: MetricSourceRevision;
}

export interface InsufficientEvidenceMetricDisplay {
  readonly kind: "insufficient-evidence";
  readonly metricKey: OverviewMetricKey;
  readonly version: "1.0";
  readonly reasonCode: string | null;
  readonly reasonOperandName: string | null;
  readonly sourceRevision: MetricSourceRevision;
}

export interface MissingMetricDisplay {
  readonly kind: "missing";
  readonly metricKey: OverviewMetricKey;
  readonly version: "1.0";
}

export class ProductionDayPresentationFailure extends Error {
  readonly reason: ProductionDayPresentationFailureReason;

  constructor(reason: ProductionDayPresentationFailureReason, message: string) {
    super(message);
    this.name = "ProductionDayPresentationFailure";
    this.reason = reason;
  }
}

const overviewMetricKeys = new Set<OverviewMetricKey>([
  "availability",
  "utilization.elr",
  "performance",
  "quality",
  "oee",
]);

const jsonNumberPattern = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/;

export function mapProductionDayOverview(
  input: ProductionDayPresentationInput,
): ProductionDayOverviewModel {
  if (!isProductionDaySelection(input.productionDay)) {
    throw new RangeError(
      "Production day must be a valid queryable YYYY-MM-DD calendar date from 0001-01-01 through 9999-12-30.",
    );
  }

  const configuredSources = buildConfiguredSourceIndex(input.sources);
  const resultIndex = new Map<string, OperationalMetricItem>();

  for (const item of input.result.items) {
    validateAuthoritativeItem(item, input.productionDay, configuredSources);
    const identity = resultIdentity(item);
    if (resultIndex.has(identity)) {
      throw new ProductionDayPresentationFailure(
        "duplicate-result",
        "Production-day reporting returned more than one result for the same overview identity.",
      );
    }

    resultIndex.set(identity, item);
  }

  const groups = new Map<string | null, ProductionDayMachineOverview[]>();

  for (const source of input.sources) {
    let machines = groups.get(source.groupName);
    if (machines === undefined) {
      machines = [];
      groups.set(source.groupName, machines);
    }

    machines.push({
      machineId: source.machineId,
      processorId: source.processorId,
      displayName: source.displayName,
      groupName: source.groupName,
      displayOrder: source.displayOrder,
      metrics: buildMetricSet(source, input.productionDay, resultIndex),
    });
  }

  return {
    productionDay: input.productionDay,
    groups: Array.from(groups, ([groupName, machines]) => ({ groupName, machines })),
  };
}

function buildConfiguredSourceIndex(
  sources: readonly DashboardRuntimeSource[],
): Set<string> {
  const identities = new Set<string>();

  for (const source of sources) {
    const identity = sourceIdentity(source.machineId, source.processorId);
    if (identities.has(identity)) {
      throw new ProductionDayPresentationFailure(
        "unexpected-source",
        "Configured production-day source identity is duplicated.",
      );
    }

    identities.add(identity);
  }

  return identities;
}

function validateAuthoritativeItem(
  item: OperationalMetricItem,
  productionDay: string,
  configuredSources: ReadonlySet<string>,
): void {
  if (item.scope !== "production-day") {
    throw new ProductionDayPresentationFailure(
      "unexpected-scope",
      "Production-day overview received a non-production-day reporting item.",
    );
  }

  if (item.productionDay === null || item.productionDay.businessDate !== productionDay) {
    throw new ProductionDayPresentationFailure(
      "unexpected-period",
      "Production-day overview received an item for an unexpected business date.",
    );
  }

  if (!configuredSources.has(sourceIdentity(item.machineId, item.processorId))) {
    throw new ProductionDayPresentationFailure(
      "unexpected-source",
      "Production-day overview received an item for an unconfigured reporting source.",
    );
  }

  if (!isUnpartitionedContext(item.context)) {
    throw new ProductionDayPresentationFailure(
      "unexpected-context",
      "Production-day overview received a partitioned reporting item.",
    );
  }

  if (!isOverviewMetric(item.metricKey) || item.definitionVersion !== "1.0") {
    throw new ProductionDayPresentationFailure(
      "unexpected-metric",
      "Production-day overview received an unexpected metric identity or version.",
    );
  }

  if (!hasValidStatusValueShape(item)) {
    throw invalidResultShape();
  }
}

function buildMetricSet(
  source: DashboardRuntimeSource,
  productionDay: string,
  resultIndex: ReadonlyMap<string, OperationalMetricItem>,
): ProductionDayMetricSet {
  return {
    availability: metricDisplay(source, productionDay, "availability", resultIndex),
    utilization: metricDisplay(source, productionDay, "utilization.elr", resultIndex),
    performance: metricDisplay(source, productionDay, "performance", resultIndex),
    quality: metricDisplay(source, productionDay, "quality", resultIndex),
    oee: metricDisplay(source, productionDay, "oee", resultIndex),
  };
}

function metricDisplay(
  source: DashboardRuntimeSource,
  productionDay: string,
  metricKey: OverviewMetricKey,
  resultIndex: ReadonlyMap<string, OperationalMetricItem>,
): ProductionDayMetricDisplay {
  const item = resultIndex.get(
    expectedIdentity(source.machineId, source.processorId, productionDay, metricKey),
  );

  return item === undefined
    ? missingMetric(metricKey)
    : mapAuthoritativeMetric(item, metricKey);
}

function mapAuthoritativeMetric(
  item: OperationalMetricItem,
  metricKey: OverviewMetricKey,
): ProductionDayMetricDisplay {
  switch (item.status) {
    case "calculated":
      if (item.value === null) {
        throw invalidResultShape();
      }

      return {
        kind: "calculated",
        metricKey,
        version: "1.0",
        value: item.value,
        unit: item.unit,
        sourceRevision: item.sourceRevision,
      };

    case "unavailable":
      if (item.value !== null) {
        throw invalidResultShape();
      }

      return {
        kind: "unavailable",
        metricKey,
        version: "1.0",
        reasonCode: item.reasonCode,
        reasonOperandName: item.reasonOperandName,
        sourceRevision: item.sourceRevision,
      };

    case "insufficient-evidence":
      if (item.value !== null) {
        throw invalidResultShape();
      }

      return {
        kind: "insufficient-evidence",
        metricKey,
        version: "1.0",
        reasonCode: item.reasonCode,
        reasonOperandName: item.reasonOperandName,
        sourceRevision: item.sourceRevision,
      };

    default:
      throw invalidResultShape();
  }
}

function missingMetric(metricKey: OverviewMetricKey): MissingMetricDisplay {
  return {
    kind: "missing",
    metricKey,
    version: "1.0",
  };
}

function hasValidStatusValueShape(item: OperationalMetricItem): boolean {
  switch (item.status) {
    case "calculated":
      return item.value !== null && isDecodedMetricValueShape(item.value);
    case "unavailable":
    case "insufficient-evidence":
      return item.value === null;
    default:
      return false;
  }
}

function isDecodedMetricValueShape(value: unknown): value is MetricValue {
  if (typeof value === "number") {
    return Number.isFinite(value);
  }

  return typeof value === "string" && jsonNumberPattern.test(value);
}

function isUnpartitionedContext(item: OperationalMetricItem["context"]): boolean {
  return item.productionOrderId === null
    && item.operationId === null
    && item.partId === null
    && item.operatorId === null;
}

function isOverviewMetric(metricKey: string): metricKey is OverviewMetricKey {
  return overviewMetricKeys.has(metricKey as OverviewMetricKey);
}

function resultIdentity(item: OperationalMetricItem): string {
  return expectedIdentity(
    item.machineId,
    item.processorId,
    item.productionDay?.businessDate ?? "",
    item.metricKey as OverviewMetricKey,
  );
}

function expectedIdentity(
  machineId: string,
  processorId: string,
  productionDay: string,
  metricKey: OverviewMetricKey,
): string {
  return JSON.stringify([
    processorId,
    machineId,
    productionDay,
    null,
    null,
    null,
    null,
    metricKey,
    "1.0",
  ]);
}

function sourceIdentity(machineId: string, processorId: string): string {
  return JSON.stringify([processorId, machineId]);
}

function invalidResultShape(): ProductionDayPresentationFailure {
  return new ProductionDayPresentationFailure(
    "invalid-result-shape",
    "Production-day overview received an internally inconsistent metric result.",
  );
}
