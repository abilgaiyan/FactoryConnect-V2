import type { ComponentProps } from "react";

import type { ShiftPerformancePageState } from "../application/shift-performance-page-state.ts";
import { ShiftPerformancePageStateView } from "./ShiftPerformancePageStateView.tsx";

type ViewProps = ComponentProps<typeof ShiftPerformancePageStateView>;
type ExpectedProps = { readonly state: ShiftPerformancePageState };

type Assert<T extends true> = T;
type Equal<A, B> =
  (<T>() => T extends A ? 1 : 2) extends
  (<T>() => T extends B ? 1 : 2)
    ? (<T>() => T extends B ? 1 : 2) extends (<T>() => T extends A ? 1 : 2)
      ? true
      : false
    : false;

export type ShiftPerformancePageStateViewAcceptsOnlyClassifiedState = Assert<Equal<ViewProps, ExpectedProps>>;
