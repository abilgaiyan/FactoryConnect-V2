import type { QueryState } from "../query/query-state.ts";
import {
  mapShiftPerformanceOverview,
} from "../presentation/shift-performance-projector.ts";
import {
  ShiftPresentationContractFailure,
  type ShiftPerformanceOverview,
} from "../presentation/shift-performance-model.ts";
import type { AuthoritativeProductionDayShiftResult } from "./production-day-shift-reporting.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

export type ShiftPerformancePageState =
  | { readonly kind: "loading"; readonly productionDay: string }
  | {
      readonly kind: "success";
      readonly productionDay: string;
      readonly overview: ShiftPerformanceOverview;
      readonly isRefreshing: boolean;
    }
  | { readonly kind: "invalid-request"; readonly productionDay: string; readonly message: string }
  | {
      readonly kind: "roster-coverage-required";
      readonly productionDay: string;
      readonly machineId: string;
      readonly siteId: string;
      readonly businessDate: string;
    }
  | { readonly kind: "transport-failure"; readonly productionDay: string; readonly message: string }
  | {
      readonly kind: "presentation-contract-failure";
      readonly productionDay: string;
      readonly message: string;
      readonly isRefreshing: boolean;
    };

export function deriveShiftPerformancePageState(
  queryState: QueryState<AuthoritativeProductionDayShiftResult>,
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
): ShiftPerformancePageState {
  switch (queryState.kind) {
    case "idle":
    case "loading":
      return { kind: "loading", productionDay };
    case "refreshing":
      return mapCompletedResult(queryState.previous, productionDay, sources, true);
    case "success":
    case "empty":
      return mapCompletedResult(queryState.data, productionDay, sources, false);
    case "invalidRequest":
      return {
        kind: "invalid-request",
        productionDay,
        message: queryState.details.detail ?? queryState.details.title ?? "The shift reporting request is invalid.",
      };
    case "coverageRequired":
      return {
        kind: "roster-coverage-required",
        productionDay,
        machineId: queryState.details.machineId,
        siteId: queryState.details.siteId,
        businessDate: queryState.details.businessDate,
      };
    case "failed":
      return {
        kind: "transport-failure",
        productionDay,
        message: "Shift performance reporting is unavailable. Please try again.",
      };
  }
}

function mapCompletedResult(
  result: AuthoritativeProductionDayShiftResult,
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  isRefreshing: boolean,
): ShiftPerformancePageState {
  try {
    return {
      kind: "success",
      productionDay,
      overview: mapShiftPerformanceOverview(productionDay, sources, result),
      isRefreshing,
    };
  } catch (error) {
    if (error instanceof ShiftPresentationContractFailure) {
      return {
        kind: "presentation-contract-failure",
        productionDay,
        message: "The shift reporting results could not be presented because they violated the Shift Performance contract.",
        isRefreshing,
      };
    }

    throw error;
  }
}
