import { useCallback, useEffect } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  queryAuthoritativeProductionDayShifts,
  type AuthoritativeProductionDayShiftResult,
} from "./production-day-shift-reporting.ts";
import { createQueryLifecycleController } from "../query/query-lifecycle-controller.ts";
import type { QueryLifecycleBinding } from "../query/use-query-lifecycle-controller.ts";
import { useQueryLifecycleController } from "../query/use-query-lifecycle-controller.ts";

export function useProductionDayShiftReporting(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): QueryLifecycleBinding<AuthoritativeProductionDayShiftResult> {
  const createController = useCallback(
    () => createQueryLifecycleController({
      query: (signal) => queryAuthoritativeProductionDayShifts(
        productionDay,
        runtime.configuration.sources,
        runtime.reportingClient,
        { signal },
      ),
      isEmpty: (result) => result.items.length === 0,
    }),
    [productionDay, runtime.configuration.sources, runtime.reportingClient],
  );
  const query = useQueryLifecycleController(createController);

  useEffect(() => {
    void query.execute();
  }, [createController, query.execute]);

  return query;
}
