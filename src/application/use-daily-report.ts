import { useCallback, useEffect, useRef, useState } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  createDailyReportLifecycleController,
  type DailyReportLifecycleController,
  type DailyReportLifecycleState,
} from "./daily-report-lifecycle.ts";

export interface DailyReportBinding {
  readonly state: DailyReportLifecycleState;
  readonly refresh: () => Promise<void>;
}

export function useDailyReport(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): DailyReportBinding {
  const controllerRef = useRef<DailyReportLifecycleController | undefined>(undefined);
  const [state, setState] = useState<DailyReportLifecycleState>({
    kind: "idle",
    canPrint: false,
  });

  useEffect(() => {
    const controller = createDailyReportLifecycleController({
      reportingClient: runtime.reportingClient,
    });
    controllerRef.current = controller;
    setState(controller.current());
    const unsubscribe = controller.subscribe(setState);
    void controller.execute(productionDay, runtime.configuration.sources);

    return () => {
      unsubscribe();
      controller.dispose();
      if (controllerRef.current === controller) {
        controllerRef.current = undefined;
      }
    };
  }, [productionDay, runtime.configuration.sources, runtime.reportingClient]);

  const refresh = useCallback(async () => {
    const controller = controllerRef.current;
    if (controller === undefined) {
      throw new Error("Daily Report lifecycle controller is not mounted.");
    }

    await controller.execute(productionDay, runtime.configuration.sources);
  }, [productionDay, runtime.configuration.sources]);

  return { state, refresh };
}
