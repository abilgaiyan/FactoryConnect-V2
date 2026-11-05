import { useCallback, useEffect, useRef, useState } from "react";

import type { QueryLifecycleController } from "./query-lifecycle-controller.ts";
import type { QueryState } from "./query-state.ts";

export interface QueryLifecycleBinding<T> {
  readonly state: QueryState<T>;
  readonly execute: () => Promise<QueryState<T>>;
}

export function useQueryLifecycleController<T>(
  createController: () => QueryLifecycleController<T>,
): QueryLifecycleBinding<T> {
  const controllerRef = useRef<QueryLifecycleController<T> | undefined>(undefined);
  const [state, setState] = useState<QueryState<T>>({ kind: "idle" });

  useEffect(() => {
    const controller = createController();
    controllerRef.current = controller;
    setState(controller.current());
    const unsubscribe = controller.subscribe(setState);

    return () => {
      unsubscribe();
      controller.dispose();
      if (controllerRef.current === controller) {
        controllerRef.current = undefined;
      }
    };
  }, [createController]);

  const execute = useCallback(async () => {
    const controller = controllerRef.current;
    if (controller === undefined) {
      throw new Error("Query lifecycle controller is not mounted.");
    }

    return controller.execute();
  }, []);

  return { state, execute };
}
