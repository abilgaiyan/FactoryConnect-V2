// FC-027 definition identities are exact; labels are presentation only.
export const overviewMetricDefinitions = [
  { metricKey: "availability", version: "1.0", label: "Availability" },
  { metricKey: "utilization.elr", version: "1.0", label: "Utilization" },
  { metricKey: "performance", version: "1.0", label: "Performance" },
  { metricKey: "quality", version: "1.0", label: "Quality" },
  { metricKey: "oee", version: "1.0", label: "OEE" },
] as const;

export type OverviewMetricKey = typeof overviewMetricDefinitions[number]["metricKey"];

export function overviewMetricLabel(metricKey: OverviewMetricKey): string {
  return overviewMetricDefinitions.find(metric => metric.metricKey === metricKey)!.label;
}
