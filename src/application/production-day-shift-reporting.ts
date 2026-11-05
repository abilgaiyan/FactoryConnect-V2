import type {
  ProductionDayShiftPage,
  ProductionDayShiftQueryRequest,
  ReportingClient,
  ReportingRequestOptions,
} from "../api/reporting/index.ts";
import { ReportingProtocolFailure } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";
import { overviewMetricDefinitions } from "./operational-metric-identities.ts";

const productionDayPattern = /^\d{4}-\d{2}-\d{2}$/;
const firstProductionDay = "0001-01-01";
const lastProductionDay = "9999-12-31";
const pageSize = 200;
const maximumPageCount = 100;
const successfulResponseStatus = 200;

type UnpartitionedContextRequest = NonNullable<ProductionDayShiftQueryRequest["context"]> & {
  unpartitionedOnly: boolean;
};

export type ProductionDayShiftReportingTraversalFailureReason =
  | "continuation-cycle"
  | "page-limit-exceeded";

export interface AuthoritativeProductionDayShiftResult {
  readonly items: ProductionDayShiftPage["items"];
}

export class ProductionDayShiftReportingTraversalFailure extends Error {
  readonly reason: ProductionDayShiftReportingTraversalFailureReason;

  constructor(reason: ProductionDayShiftReportingTraversalFailureReason) {
    super(
      reason === "continuation-cycle"
        ? "Production-day shift reporting returned a repeated continuation token."
        : `Production-day shift reporting exceeded the ${maximumPageCount}-page traversal limit.`,
    );
    this.name = "ProductionDayShiftReportingTraversalFailure";
    this.reason = reason;
  }
}

export function buildProductionDayShiftQueryRequest(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  continuationToken: string | null = null,
): ProductionDayShiftQueryRequest {
  if (!isProductionDayIdentity(productionDay)) {
    throw new RangeError(
      "Production day must be a valid YYYY-MM-DD calendar identity from 0001-01-01 through 9999-12-31.",
    );
  }

  const context: UnpartitionedContextRequest = {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  };

  return {
    sources: sources.map(({ machineId, processorId, siteId }) => ({
      machineId,
      processorId,
      siteId,
      businessDate: productionDay,
    })),
    context,
    metrics: overviewMetricDefinitions.map(({ metricKey, version }) => ({ metricKey, version })),
    statuses: null,
    pageSize,
    continuationToken,
  };
}

export async function queryAuthoritativeProductionDayShifts(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  reportingClient: Pick<ReportingClient, "queryProductionDayShiftMetrics">,
  options?: ReportingRequestOptions,
): Promise<AuthoritativeProductionDayShiftResult> {
  if (!isProductionDayIdentity(productionDay)) {
    throw new RangeError(
      "Production day must be a valid YYYY-MM-DD calendar identity from 0001-01-01 through 9999-12-31.",
    );
  }

  if (sources.length === 0) {
    return { items: [] };
  }

  const items: ProductionDayShiftPage["items"] = [];
  const seenContinuationTokens = new Set<string>();
  let continuationToken: string | null = null;
  let pagesRead = 0;

  do {
    if (pagesRead >= maximumPageCount) {
      throw traversalProtocolFailure("page-limit-exceeded");
    }

    const request = buildProductionDayShiftQueryRequest(productionDay, sources, continuationToken);
    const page = await reportingClient.queryProductionDayShiftMetrics(request, options);
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

export function isProductionDayIdentity(value: string): boolean {
  if (!productionDayPattern.test(value) || value < firstProductionDay || value > lastProductionDay) {
    return false;
  }

  const [yearText, monthText, dayText] = value.split("-");
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const daysInMonth = monthLength(year, month);
  return daysInMonth !== null && day >= 1 && day <= daysInMonth;
}

function traversalProtocolFailure(reason: ProductionDayShiftReportingTraversalFailureReason): ReportingProtocolFailure {
  const cause = new ProductionDayShiftReportingTraversalFailure(reason);
  return new ReportingProtocolFailure(successfulResponseStatus, cause.message, cause);
}

function monthLength(year: number, month: number): number | null {
  switch (month) {
    case 1:
    case 3:
    case 5:
    case 7:
    case 8:
    case 10:
    case 12:
      return 31;
    case 4:
    case 6:
    case 9:
    case 11:
      return 30;
    case 2:
      return isLeapYear(year) ? 29 : 28;
    default:
      return null;
  }
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}
