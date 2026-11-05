export { createReportingClient } from "./reporting-client.ts";

export type {
  OperationalMetricPage,
  ProductionDayQueryRequest,
  ProductionDayShiftPage,
  ProductionDayShiftQueryRequest,
  ReportingClient,
  ReportingClientOptions,
  ReportingProblemDetails,
  ReportingRequestOptions,
  ShiftQueryRequest,
} from "./reporting-client-types.ts";

export {
  ReportingCancellationFailure,
  ReportingNetworkFailure,
  ReportingTimeoutFailure,
} from "./reporting-transport-failures.ts";
export type { ReportingTransportFailure } from "./reporting-transport-failures.ts";

export {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingProtocolFailure,
} from "./reporting-response-failures.ts";
export type {
  ProductionDayShiftRosterCoverageDetails,
  ReportingClientFailure,
  ReportingResponseFailure,
} from "./reporting-response-failures.ts";
