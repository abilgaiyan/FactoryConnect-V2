import type { DailyReportCell } from "./daily-report-model.ts";

const labels: Record<string, string> = {
  availability: "Availability", performance: "Performance", quality: "Quality", oee: "OEE",
};

/** Visualizes only authoritative calculated percentages. Never substitutes zero for missing evidence. */
export function OeeMetricBars({ cells }: { readonly cells: readonly DailyReportCell[] }) {
  const metrics = (["availability", "performance", "quality", "oee"] as const).map(key => ({
    key, cell: cells.find(cell => cell.metricKey === key),
  }));
  return <section aria-label="OEE component graph" className="oee-metric-bars">
    <h5>OEE components — production day</h5>
    <div className="oee-metric-bars-grid">
      {metrics.map(({ key, cell }) => {
        const numeric = cell?.state === "calculated" ? Number(cell.value) : NaN;
        const supported = cell?.state === "calculated" && cell.unit === "%" &&
          Number.isFinite(numeric) && numeric >= 0 && numeric <= 100;
        return <div className="oee-metric-bar" key={key}>
          <div className="oee-metric-bar-label"><strong>{labels[key]}</strong>
            <span>{supported ? numeric.toFixed(1) + "%" :
              cell?.state === "calculated" ? String(cell.value) + " " + cell.unit :
              cell?.state === "insufficient-evidence" ? "Insufficient evidence" :
              cell?.state === "unavailable" ? "Unavailable" : "Missing"}</span></div>
          {supported ? <progress max={100} value={numeric} aria-label={labels[key] + " percentage"} /> :
            <div className="oee-metric-no-bar">No percentage bar available</div>}
        </div>;
      })}
    </div>
    <p>Calculated values only. Missing or insufficient evidence is not interpreted as zero.
      This graph reflects the selected production day, not live machine connectivity.</p>
  </section>;
}
