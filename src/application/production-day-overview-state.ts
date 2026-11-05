import type { QueryState } from "../query/query-state.ts";
import {
  mapProductionDayOverview,
  ProductionDayPresentationFailure,
  type ProductionDayOverviewModel,
} from "./production-day-presentation.ts";
import type { AuthoritativeProductionDayResult } from "./production-day-reporting.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

export type ProductionDayOverviewViewState =
  | { readonly kind: "idle" }
  | { readonly kind: "loading" }
  | { readonly kind: "refreshing"; readonly model: ProductionDayOverviewModel }
  | { readonly kind: "empty-factory" }
  | { readonly kind: "success"; readonly model: ProductionDayOverviewModel }
  | { readonly kind: "request-invalid"; readonly message: string }
  | { readonly kind: "reporting-failed"; readonly message: string }
  | { readonly kind: "presentation-failed"; readonly message: string };

export function deriveProductionDayOverviewViewState(
  queryState: QueryState<AuthoritativeProductionDayResult>,
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
): ProductionDayOverviewViewState {
  if (sources.length === 0 && queryState.kind !== "loading") {
    return { kind: "empty-factory" };
  }

  switch (queryState.kind) {
    case "idle":
      return { kind: "idle" };
    case "loading":
      return { kind: "loading" };
    case "refreshing":
      return mapResult(productionDay, sources, queryState.previous, "refreshing");
    case "success":
      return mapResult(productionDay, sources, queryState.data);
    case "empty":
      return mapResult(productionDay, sources, queryState.data);
    case "invalidRequest":
      return {
        kind: "request-invalid",
        message: queryState.details.detail ?? queryState.details.title ?? "The reporting request is invalid.",
      };
    case "coverageRequired":
      return {
        kind: "reporting-failed",
        message: "Production-day reporting is unavailable because the requested shift roster has not been materialized.",
      };
    case "failed":
      return {
        kind: "reporting-failed",
        message: "Production-day reporting is unavailable. Please try again.",
      };
  }
}

function mapResult(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  result: AuthoritativeProductionDayResult,
  successKind: "success" | "refreshing" = "success",
): ProductionDayOverviewViewState {
  try {
    const model = mapProductionDayOverview({ productionDay, sources, result });
    return successKind === "refreshing"
      ? { kind: "refreshing", model }
      : { kind: "success", model };
  } catch (error) {
    if (error instanceof ProductionDayPresentationFailure) {
      return {
        kind: "presentation-failed",
        message: "The reporting results could not be presented because they violated the production-day overview contract.",
      };
    }

    throw error;
  }
}
