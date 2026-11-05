import { formatRatioAsPercentage } from "../application/production-day-metric-formatting.ts";
import type { PresentedMetric } from "./shift-performance-model.ts";

export interface PresentedMetricText {
  readonly primary: string;
  readonly evidence: string | null;
}

export function formatPresentedMetric(metric: PresentedMetric): PresentedMetricText {
  switch (metric.state) {
    case "calculated":
      return {
        primary: metric.unit.toLowerCase() === "ratio"
          ? `${formatRatioAsPercentage(metric.value)}%`
          : `${String(metric.value)} ${metric.unit}`,
        evidence: null,
      };
    case "unavailable":
      return {
        primary: "Unavailable",
        evidence: formatReasonEvidence(metric.reasonCode, metric.reasonOperandName),
      };
    case "insufficient-evidence":
      return {
        primary: "Insufficient evidence",
        evidence: formatReasonEvidence(metric.reasonCode, metric.reasonOperandName),
      };
    case "missing":
      return { primary: "—", evidence: null };
  }
}

function formatReasonEvidence(reasonCode: string | null, reasonOperandName: string | null): string | null {
  const evidence = [reasonCode, reasonOperandName].filter((value): value is string => value !== null);
  return evidence.length === 0 ? null : evidence.join(" / ");
}
