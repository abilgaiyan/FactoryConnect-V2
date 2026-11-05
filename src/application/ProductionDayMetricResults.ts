import { createElement, type ReactElement } from "react";

import type { OperationalMetricPage } from "../api/reporting/index.ts";
import type { AuthoritativeProductionDayResult } from "./production-day-reporting.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

export interface ProductionDayMetricResultsProps {
  readonly page: AuthoritativeProductionDayResult;
  readonly sources: readonly DashboardRuntimeSource[];
}

export function ProductionDayMetricResults({
  page,
  sources,
}: ProductionDayMetricResultsProps): ReactElement {
  const sourceNames = new Map(
    sources.map((source) => [`${source.machineId}\u0000${source.processorId}`, source.displayName]),
  );

  return createElement(
    "div",
    null,
    createElement(
      "table",
      null,
      createElement("caption", null, "Operational metrics returned by the reporting API"),
      createElement(
        "thead",
        null,
        createElement(
          "tr",
          null,
          ...[
            "Source",
            "Machine",
            "Processor",
            "Site",
            "Business date",
            "Metric",
            "Version",
            "Status",
            "Value",
            "Unit",
            "Reason",
            "Production order",
            "Operation",
            "Part",
            "Operator",
            "Revision machine",
            "Revision processor",
            "Revision stream",
            "Revision position",
          ].map((heading) => createElement("th", { key: heading, scope: "col" }, heading)),
        ),
      ),
      createElement(
        "tbody",
        null,
        ...page.items.map((item) => {
          const sourceKey = `${item.machineId}\u0000${item.processorId}`;
          const sourceName = sourceNames.get(sourceKey) ?? "Unconfigured source";
          const reason = item.reasonCode === null
            ? "—"
            : item.reasonOperandName === null
              ? item.reasonCode
              : `${item.reasonCode} (${item.reasonOperandName})`;
          const context = item.context;

          const cells = [
            sourceName,
            item.machineId,
            item.processorId,
            item.productionDay?.siteId ?? "—",
            item.productionDay?.businessDate ?? "—",
            item.metricKey,
            item.definitionVersion,
            item.status,
            item.value === null ? "—" : String(item.value),
            item.unit,
            reason,
            context.productionOrderId ?? "—",
            context.operationId ?? "—",
            context.partId ?? "—",
            context.operatorId ?? "—",
            item.sourceRevision.machineId,
            item.sourceRevision.processorId,
            item.sourceRevision.streamKey,
            String(item.sourceRevision.position),
          ];

          return createElement(
            "tr",
            { key: metricRowKey(item) },
            ...cells.map((value, index) => createElement("td", { key: index }, value)),
          );
        }),
      ),
    ),
  );
}

function metricRowKey(item: OperationalMetricPage["items"][number]): string {
  const context = item.context;
  const period = item.productionDay?.businessDate ?? item.shift?.shiftScheduleAssignmentId ?? "no-period";
  return [
    item.processorId,
    item.machineId,
    period,
    context.productionOrderId ?? "",
    context.operationId ?? "",
    context.partId ?? "",
    context.operatorId ?? "",
    item.metricKey,
    item.definitionVersion,
    item.sourceRevision.machineId,
    item.sourceRevision.processorId,
    item.sourceRevision.streamKey,
    String(item.sourceRevision.position),
  ].join("\u0000");
}
