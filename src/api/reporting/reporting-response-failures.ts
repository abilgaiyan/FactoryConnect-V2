import type { ReportingProblemDetails } from "./reporting-client-types.ts";
import type { ReportingTransportFailure } from "./reporting-transport-failures.ts";

export interface ProductionDayShiftRosterCoverageDetails {
  readonly machineId: string;
  readonly siteId: string;
  readonly businessDate: string;
}

export type ReportingResponseFailure =
  | ReportingInvalidQueryFailure
  | ReportingMalformedContinuationTokenFailure
  | ReportingIncompatibleContinuationTokenFailure
  | ProductionDayShiftRosterCoverageRequiredFailure
  | ReportingHttpFailure
  | ReportingProtocolFailure;

export type ReportingClientFailure = ReportingTransportFailure | ReportingResponseFailure;

abstract class ReportingProblemFailure extends Error {
  readonly problemDetails: ReportingProblemDetails;

  protected constructor(message: string, problemDetails: ReportingProblemDetails) {
    super(message);
    this.problemDetails = problemDetails;
  }
}

export class ReportingInvalidQueryFailure extends ReportingProblemFailure {
  readonly kind = "invalid-query" as const;

  constructor(problemDetails: ReportingProblemDetails) {
    super("The reporting query is invalid.", problemDetails);
    this.name = "ReportingInvalidQueryFailure";
  }
}

export class ReportingMalformedContinuationTokenFailure extends ReportingProblemFailure {
  readonly kind = "malformed-continuation-token" as const;

  constructor(problemDetails: ReportingProblemDetails) {
    super("The reporting continuation token is malformed.", problemDetails);
    this.name = "ReportingMalformedContinuationTokenFailure";
  }
}

export class ReportingIncompatibleContinuationTokenFailure extends ReportingProblemFailure {
  readonly kind = "incompatible-continuation-token" as const;

  constructor(problemDetails: ReportingProblemDetails) {
    super("The reporting continuation token is incompatible with this query.", problemDetails);
    this.name = "ReportingIncompatibleContinuationTokenFailure";
  }
}

export class ProductionDayShiftRosterCoverageRequiredFailure extends ReportingProblemFailure {
  readonly kind = "production-day-shift-roster-coverage-required" as const;
  readonly machineId: string;
  readonly siteId: string;
  readonly businessDate: string;

  constructor(problemDetails: ReportingProblemDetails, details: ProductionDayShiftRosterCoverageDetails) {
    super("Production-day shift roster coverage is required before this report can be queried.", problemDetails);
    this.name = "ProductionDayShiftRosterCoverageRequiredFailure";
    this.machineId = details.machineId;
    this.siteId = details.siteId;
    this.businessDate = details.businessDate;
  }
}

export class ReportingHttpFailure extends Error {
  readonly kind = "http" as const;
  readonly status: number;
  readonly problemDetails: ReportingProblemDetails | undefined;

  constructor(status: number, problemDetails?: ReportingProblemDetails) {
    super(`Reporting request returned HTTP ${status}.`);
    this.name = "ReportingHttpFailure";
    this.status = status;
    this.problemDetails = problemDetails;
  }
}

export class ReportingProtocolFailure extends Error {
  readonly kind = "protocol" as const;
  readonly status: number;

  constructor(status: number, message: string, cause?: unknown) {
    super(message, { cause });
    this.name = "ReportingProtocolFailure";
    this.status = status;
  }
}
