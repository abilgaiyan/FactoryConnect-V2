import { useMemo } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  deriveShiftPerformancePageState,
  type ShiftPerformancePageState,
} from "./shift-performance-page-state.ts";
import { useProductionDayShiftReporting } from "./use-production-day-shift-reporting.ts";

export interface ShiftPerformanceOverviewBinding {
  readonly state: ShiftPerformancePageState;
  readonly refresh: () => Promise<void>;
}

export function useShiftPerformanceOverview(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): ShiftPerformanceOverviewBinding {
  const query = useProductionDayShiftReporting(productionDay, runtime);
  const state = useMemo(
    () => deriveShiftPerformancePageState(
      query.state,
      productionDay,
      runtime.configuration.sources,
    ),
    [query.state, productionDay, runtime.configuration.sources],
  );
  const refresh = async () => {
    await query.execute();
  };

  return { state, refresh };
}
