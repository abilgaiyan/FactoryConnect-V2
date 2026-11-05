import type { paths } from "../generated/reporting-contract";

type Assert<T extends true> = T;
type Equal<A, B> =
  (<T>() => T extends A ? 1 : 2) extends
  (<T>() => T extends B ? 1 : 2)
    ? (<T>() => T extends B ? 1 : 2) extends
        (<T>() => T extends A ? 1 : 2)
      ? true
      : false
    : false;

type ShiftQueryOperation = NonNullable<
  paths["/api/reporting/v1/operational-metrics/shifts/query"]["post"]
>;

type ProductionDayQueryOperation = NonNullable<
  paths["/api/reporting/v1/operational-metrics/production-days/query"]["post"]
>;

type ProductionDayShiftQueryOperation = NonNullable<
  paths["/api/reporting/v1/operational-metrics/production-day-shifts/query"]["post"]
>;

export type ShiftQueryRequest =
  ShiftQueryOperation["requestBody"]["content"]["application/json"];

export type ProductionDayQueryRequest =
  ProductionDayQueryOperation["requestBody"]["content"]["application/json"];

export type ProductionDayShiftQueryRequest =
  ProductionDayShiftQueryOperation["requestBody"]["content"]["application/json"];

export type OperationalMetricPage =
  ShiftQueryOperation["responses"][200]["content"]["application/json"];

export type ProductionDayShiftPage =
  ProductionDayShiftQueryOperation["responses"][200]["content"]["application/json"];

export type ReportingProblemDetails =
  ShiftQueryOperation["responses"][400]["content"]["application/problem+json"];

type ProductionDayOperationalMetricPage =
  ProductionDayQueryOperation["responses"][200]["content"]["application/json"];

type ProductionDayReportingProblemDetails =
  ProductionDayQueryOperation["responses"][400]["content"]["application/problem+json"];

type SuccessfulResponseContractsMatch = Assert<
  Equal<OperationalMetricPage, ProductionDayOperationalMetricPage>
>;

type ProblemDetailsContractsMatch = Assert<
  Equal<ReportingProblemDetails, ProductionDayReportingProblemDetails>
>;

export interface ReportingRequestOptions {
  signal?: AbortSignal;
}

export interface ReportingClientOptions {
  baseAddress: string;
  timeoutMilliseconds: number;
  fetch?: typeof globalThis.fetch;
}

export interface ReportingClient {
  queryShiftMetrics(
    request: ShiftQueryRequest,
    options?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage>;

  queryProductionDayMetrics(
    request: ProductionDayQueryRequest,
    options?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage>;

  queryProductionDayShiftMetrics(
    request: ProductionDayShiftQueryRequest,
    options?: ReportingRequestOptions,
  ): Promise<ProductionDayShiftPage>;
}

export type ReportingClientContractConformance =
  SuccessfulResponseContractsMatch & ProblemDetailsContractsMatch;
