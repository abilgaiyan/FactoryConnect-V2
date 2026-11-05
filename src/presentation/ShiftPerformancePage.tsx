import type { ShiftPerformancePageState } from "../application/shift-performance-page-state.ts";
import { ShiftPerformancePageStateView } from "./ShiftPerformancePageStateView.tsx";

export interface ShiftPerformancePageProps {
  readonly state: ShiftPerformancePageState;
  readonly refresh: () => Promise<void>;
}

export function ShiftPerformancePage({ state, refresh }: ShiftPerformancePageProps) {
  const refreshDisabled = isShiftPerformanceRefreshDisabled(state);

  return (
    <>
      <ShiftPerformancePageStateView state={state} />
      <button
        type="button"
        disabled={refreshDisabled}
        onClick={() => {
          void invokeShiftPerformanceRefresh(refresh);
        }}
      >
        Refresh
      </button>
    </>
  );
}

export function isShiftPerformanceRefreshDisabled(state: ShiftPerformancePageState): boolean {
  if (state.kind === "loading") {
    return true;
  }

  if (state.kind === "success" || state.kind === "presentation-contract-failure") {
    return state.isRefreshing;
  }

  return false;
}

export function invokeShiftPerformanceRefresh(refresh: () => Promise<void>): Promise<void> {
  return refresh();
}
