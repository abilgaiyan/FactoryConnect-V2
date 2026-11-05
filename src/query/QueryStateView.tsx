import type { ReactNode } from "react";

import type { QueryState } from "./query-state.ts";
import { presentQueryState } from "./query-state-presentation.ts";

export interface QueryStateViewProps<T> {
  readonly state: QueryState<T>;
  readonly children: (data: T) => ReactNode;
}

export function QueryStateView<T>({ state, children }: QueryStateViewProps<T>) {
  if (state.kind === "success") {
    return <>{children(state.data)}</>;
  }

  if (state.kind === "refreshing") {
    const presentation = presentQueryState(state);
    return (
      <>
        {children(state.previous)}
        <p role="status">{presentation.message}</p>
      </>
    );
  }

  const presentation = presentQueryState(state);
  const role = presentation.kind === "failed" || presentation.kind === "invalidRequest"
    ? "alert"
    : "status";

  return <p role={role}>{presentation.message}</p>;
}
