import type { QueryState } from "./query-state.ts";

export type QueryStatePresentation =
  | { readonly kind: "idle"; readonly message: string }
  | { readonly kind: "loading"; readonly message: string }
  | { readonly kind: "refreshing"; readonly message: string }
  | { readonly kind: "success"; readonly message: string }
  | { readonly kind: "empty"; readonly message: string }
  | { readonly kind: "invalidRequest"; readonly message: string }
  | { readonly kind: "coverageRequired"; readonly message: string }
  | { readonly kind: "failed"; readonly message: string };

export function presentQueryState<T>(state: QueryState<T>): QueryStatePresentation {
  switch (state.kind) {
    case "idle":
      return { kind: "idle", message: "Ready." };
    case "loading":
      return { kind: "loading", message: "Loading." };
    case "refreshing":
      return { kind: "refreshing", message: "Refreshing." };
    case "success":
      return { kind: "success", message: "Data loaded." };
    case "empty":
      return { kind: "empty", message: "No matching data." };
    case "invalidRequest":
      return {
        kind: "invalidRequest",
        message: state.details.detail ?? state.details.title ?? "The reporting request is invalid.",
      };
    case "coverageRequired":
      return {
        kind: "coverageRequired",
        message: `Shift roster coverage is required for machine ${state.details.machineId}, site ${state.details.siteId}, production day ${state.details.businessDate}.`,
      };
    case "failed":
      return { kind: "failed", message: state.failure.message };
  }
}
