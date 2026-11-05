export function productionDayPath(productionDay: string): string {
  return `/production-days/${encodeURIComponent(productionDay)}`;
}
