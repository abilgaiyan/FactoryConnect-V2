import type { ShiftPerformancePageState } from "../application/shift-performance-page-state.ts";
import { ShiftPerformanceOverviewView } from "./ShiftPerformanceOverviewView.tsx";

export interface ShiftPerformancePageStateViewProps {
  readonly state: ShiftPerformancePageState;
}

export function ShiftPerformancePageStateView({ state }: ShiftPerformancePageStateViewProps) {
  switch (state.kind) {
    case "loading":
      return <p role="status">Loading shift performance…</p>;
    case "success":
      return (
        <>
          <ShiftPerformanceOverviewView key={state.overview.productionDay} overview={state.overview} />
          {state.isRefreshing
            ? <p role="status" aria-live="polite">Refreshing shift performance…</p>
            : null}
        </>
      );
    case "invalid-request":
      return <p role="alert">{state.message}</p>;
    case "roster-coverage-required":
      return (
        <p role="alert">
          Shift roster coverage is required for machine {state.machineId} at site {state.siteId} on {state.businessDate}.
        </p>
      );
    case "transport-failure":
      return <p role="alert">{state.message}</p>;
    case "presentation-contract-failure":
      return (
        <>
          <p role="alert">{state.message}</p>
          {state.isRefreshing
            ? <p role="status" aria-live="polite">Refreshing shift performance…</p>
            : null}
        </>
      );
  }
}
