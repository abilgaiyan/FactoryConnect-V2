import { isProductionDayIdentity } from "./production-day-shift-reporting.ts";

export function shiftPerformancePath(productionDay: string): string {
  return `/production-days/${encodeURIComponent(productionDay)}/shifts`;
}

export function isShiftPerformanceProductionDaySelection(value: string): boolean {
  return isProductionDayIdentity(value);
}
