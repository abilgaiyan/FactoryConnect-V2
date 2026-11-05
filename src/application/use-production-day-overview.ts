import { useEffect, useMemo, useRef, useState } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  deriveProductionDayOverviewViewState,
  type ProductionDayOverviewViewState,
} from "./production-day-overview-state.ts";
import { useProductionDayReporting } from "./use-production-day-reporting.ts";

export interface ProductionDaySuccessfulRetrieval {
  readonly productionDay: string;
  readonly retrievedAt: Date;
}

export interface ProductionDayOverviewBinding {
  readonly state: ProductionDayOverviewViewState;
  readonly lastSuccessfulRetrieval: ProductionDaySuccessfulRetrieval | null;
  readonly refresh: () => Promise<void>;
}

export function useProductionDayOverview(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): ProductionDayOverviewBinding {
  const query = useProductionDayReporting(productionDay, runtime);
  const state = useMemo(
    () => deriveProductionDayOverviewViewState(query.state, productionDay, runtime.configuration.sources),
    [query.state, productionDay, runtime.configuration.sources],
  );
  const [lastSuccessfulRetrieval, setLastSuccessfulRetrieval] = useState<ProductionDaySuccessfulRetrieval | null>(null);
  const recordedQueryState = useRef<object | null>(null);

  useEffect(() => {
    if (state.kind === "success" && recordedQueryState.current !== query.state) {
      recordedQueryState.current = query.state;
      const now = runtime.now ?? (() => new Date());
      setLastSuccessfulRetrieval({ productionDay, retrievedAt: now() });
    }
  }, [productionDay, query.state, runtime.now, state]);

  const refresh = async () => {
    await query.execute();
  };

  return { state, lastSuccessfulRetrieval, refresh };
}
