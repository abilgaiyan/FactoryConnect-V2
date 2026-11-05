import { routePath } from "../routing/application-route.ts";

export function dailyReportPath(productionDay: string): string {
  return routePath({ kind: "dailyReport", productionDay });
}
