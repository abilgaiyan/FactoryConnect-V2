import { useCallback, useEffect } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  queryAuthoritativeProductionDay,
  type AuthoritativeProductionDayResult,
} from "./production-day-reporting.ts";
import { createQueryLifecycleController } from "../query/query-lifecycle-controller.ts";
import type { QueryLifecycleBinding } from "../query/use-query-lifecycle-controller.ts";
import { useQueryLifecycleController } from "../query/use-query-lifecycle-controller.ts";

export function useProductionDayReporting(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): QueryLifecycleBinding<AuthoritativeProductionDayResult> {
  const createController = useCallback(
    () => createQueryLifecycleController({
      query: (signal) => queryAuthoritativeProductionDay(
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
