export const reportingRoutes = {
  shiftQuery: "api/reporting/v1/operational-metrics/shifts/query",
  productionDayQuery: "api/reporting/v1/operational-metrics/production-days/query",
  productionDayShiftQuery: "api/reporting/v1/operational-metrics/production-day-shifts/query",
} as const;

export type ReportingRoute =
  (typeof reportingRoutes)[keyof typeof reportingRoutes];
