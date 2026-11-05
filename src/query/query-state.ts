import type {
  ProductionDayShiftRosterCoverageDetails,
  ReportingClientFailure,
  ReportingProblemDetails,
} from "../api/reporting/index.ts";

export type QueryState<T> =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "refreshing"; previous: T }
  | { kind: "success"; data: T }
  | { kind: "empty"; data: T }
  | { kind: "invalidRequest"; details: ReportingProblemDetails }
  | { kind: "coverageRequired"; details: ProductionDayShiftRosterCoverageDetails }
  | { kind: "failed"; failure: ReportingClientFailure };
