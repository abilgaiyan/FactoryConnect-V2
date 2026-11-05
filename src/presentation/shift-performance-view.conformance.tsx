import type { ComponentProps } from "react";

import { ShiftPerformanceOverviewView } from "./ShiftPerformanceOverviewView.tsx";
import type { ShiftPerformanceOverview } from "./shift-performance-model.ts";

type ViewProps = ComponentProps<typeof ShiftPerformanceOverviewView>;
type ExpectedProps = { readonly overview: ShiftPerformanceOverview; readonly showFilters?: boolean };

type Assert<T extends true> = T;
type Equal<A, B> =
  (<T>() => T extends A ? 1 : 2) extends
  (<T>() => T extends B ? 1 : 2)
    ? (<T>() => T extends B ? 1 : 2) extends (<T>() => T extends A ? 1 : 2)
      ? true
      : false
    : false;

export type ShiftPerformanceViewAcceptsOverviewAndOptionalFilterVisibility = Assert<Equal<ViewProps, ExpectedProps>>;
