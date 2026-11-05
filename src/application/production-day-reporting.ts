import type {
  OperationalMetricPage,
  ProductionDayQueryRequest,
  ReportingClient,
  ReportingRequestOptions,
} from "../api/reporting/index.ts";
import { ReportingProtocolFailure } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

const productionDayPattern = /^\d{4}-\d{2}-\d{2}$/;
const firstQueryableProductionDay = "0001-01-01";
const lastQueryableProductionDay = "9999-12-30";
const pageSize = 200;
const maximumPageCount = 100;
const successfulResponseStatus = 200;
const overviewMetrics = [
  { metricKey: "availability", version: "1.0" },
  { metricKey: "utilization.elr", version: "1.0" },
  { metricKey: "performance", version: "1.0" },
  { metricKey: "quality", version: "1.0" },
  { metricKey: "oee", version: "1.0" },
] as const;

type UnpartitionedContextRequest = NonNullable<ProductionDayQueryRequest["context"]> & {
  unpartitionedOnly: boolean;
};

export type ProductionDayReportingTraversalFailureReason =
  | "continuation-cycle"
  | "page-limit-exceeded";

export interface AuthoritativeProductionDayResult {
  readonly items: OperationalMetricPage["items"];
}

export class ProductionDayReportingTraversalFailure extends Error {
  readonly reason: ProductionDayReportingTraversalFailureReason;

  constructor(reason: ProductionDayReportingTraversalFailureReason) {
    super(
      reason === "continuation-cycle"
        ? "Production-day reporting returned a repeated continuation token."
        : `Production-day reporting exceeded the ${maximumPageCount}-page traversal limit.`,
    );
    this.name = "ProductionDayReportingTraversalFailure";
    this.reason = reason;
  }
}

export function buildProductionDayQueryRequest(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  continuationToken: string | null = null,
): ProductionDayQueryRequest {
  const context: UnpartitionedContextRequest = {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  };

  return {
    sources: sources.map(({ machineId, processorId }) => ({ machineId, processorId })),
    fromInclusive: productionDay,
    toExclusive: nextProductionDay(productionDay),
    metrics: overviewMetrics.map(({ metricKey, version }) => ({ metricKey, version })),
    context,
    statuses: null,
    order: "period-ascending",
    pageSize,
    continuationToken,
  };
}

export async function queryAuthoritativeProductionDay(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  reportingClient: Pick<ReportingClient, "queryProductionDayMetrics">,
  options?: ReportingRequestOptions,
): Promise<AuthoritativeProductionDayResult> {
  if (!isProductionDaySelection(productionDay)) {
    throw new RangeError(
      "Production day must be a valid queryable YYYY-MM-DD calendar date from 0001-01-01 through 9999-12-30.",
    );
  }

  if (sources.length === 0) {
    return { items: [] };
  }

  const items: OperationalMetricPage["items"] = [];
  const seenContinuationTokens = new Set<string>();
  let continuationToken: string | null = null;
  let pagesRead = 0;

  do {
    if (pagesRead >= maximumPageCount) {
      throw traversalProtocolFailure("page-limit-exceeded");
    }

    const request = buildProductionDayQueryRequest(productionDay, sources, continuationToken);
    const page = await reportingClient.queryProductionDayMetrics(request, options);
    pagesRead += 1;
    items.push(...page.items);

    const nextToken = page.continuationToken;
    if (nextToken !== null) {
      if (seenContinuationTokens.has(nextToken)) {
        throw traversalProtocolFailure("continuation-cycle");
      }
      seenContinuationTokens.add(nextToken);
    }

    continuationToken = nextToken;
  } while (continuationToken !== null);

  return { items };
}

export function isProductionDaySelection(value: string): boolean {
  if (!productionDayPattern.test(value) || value < firstQueryableProductionDay || value > lastQueryableProductionDay) {
    return false;
  }

  const date = new Date(`${value}T00:00:00.000Z`);
  return !Number.isNaN(date.valueOf()) && date.toISOString().slice(0, 10) === value;
}

function traversalProtocolFailure(reason: ProductionDayReportingTraversalFailureReason): ReportingProtocolFailure {
  const cause = new ProductionDayReportingTraversalFailure(reason);
  return new ReportingProtocolFailure(successfulResponseStatus, cause.message, cause);
}

function nextProductionDay(productionDay: string): string {
  if (!isProductionDaySelection(productionDay)) {
    throw new RangeError(
      "Production day must be a valid queryable YYYY-MM-DD calendar date from 0001-01-01 through 9999-12-30.",
    );
  }

  const date = new Date(`${productionDay}T00:00:00.000Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
}
