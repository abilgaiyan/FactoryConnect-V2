import { formatReportDate, formatReportTimestamp } from "../presentation/date-formatting.ts";
import { createElement, type ChangeEvent, type ReactElement } from "react";

import { ProductionDayOverviewMatrix } from "./ProductionDayOverviewMatrix.ts";
import { isProductionDaySelection } from "./production-day-reporting.ts";
import type { ProductionDayOverviewViewState } from "./production-day-overview-state.ts";
import type { ProductionDayOverviewBinding } from "./use-production-day-overview.ts";

export interface ProductionDayOverviewSurfaceProps {
  readonly productionDay: string;
  readonly overview: ProductionDayOverviewBinding;
  readonly onProductionDayChange: (day: string) => void;
}

export function ProductionDayOverviewSurface({
  productionDay,
  overview,
  onProductionDayChange,
}: ProductionDayOverviewSurfaceProps): ReactElement {
  const loading = overview.state.kind === "loading";
  const refreshing = overview.state.kind === "refreshing";

  const handleProductionDayChange = (event: ChangeEvent<HTMLInputElement>) => {
    const nextProductionDay = event.currentTarget.value;
    if (isProductionDaySelection(nextProductionDay) && nextProductionDay !== productionDay) {
      onProductionDayChange(nextProductionDay);
    }
  };

  const handleRefresh = () => {
    void overview.refresh();
  };

  return createElement(
    "div",
    { "aria-busy": loading || refreshing ? "true" : "false" },
    createElement("div", { className: "production-day-overview-controls" },
    createElement("label", { htmlFor: "production-day-overview-selector" }, "Production day"),
    createElement("input", {
      id: "production-day-overview-selector",
      name: "production-day-overview-selector",
      type: "date",
      value: productionDay,
      onChange: handleProductionDayChange,
    }),
    createElement("button", {
      type: "button",
      onClick: handleRefresh,
      disabled: loading,
    }, "Refresh")),
    overview.lastSuccessfulRetrieval === null
      ? null
      : createElement(
        "p",
        null,
        `Last loaded for ${formatReportDate(overview.lastSuccessfulRetrieval.productionDay)}: ${formatReportTimestamp(overview.lastSuccessfulRetrieval.retrievedAt)}`,
      ),
    createElement(ProductionDayOverviewStateView, { state: overview.state }),
  );
}

export function ProductionDayOverviewStateView({
  state,
}: {
  readonly state: ProductionDayOverviewViewState;
}): ReactElement {
  switch (state.kind) {
    case "idle":
    case "loading":
      return createElement("p", { role: "status" }, "Loading production-day reporting…");
    case "refreshing":
      return createElement(
        "div",
        null,
        createElement(ProductionDayOverviewMatrix, { model: state.model }),
        createElement("p", { role: "status" }, "Refreshing production-day reporting…"),
      );
    case "empty-factory":
      return createElement("p", null, "No machines are configured for this dashboard.");
    case "success":
      return createElement(ProductionDayOverviewMatrix, { model: state.model });
    case "request-invalid":
    case "reporting-failed":
    case "presentation-failed":
      return createElement("p", { role: "alert" }, state.message);
  }
}
