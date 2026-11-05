export type ApplicationRoute =
  | { kind: "supervisorPreview" }
  | { kind: "commissioning" }
  | { kind: "overview"; search: string }
  | { kind: "fleet" }
  | { kind: "dateRange" }
  | { kind: "productionDayOverview" }
  | { kind: "productionDayDetail"; productionDay: string }
  | { kind: "shiftPerformance"; productionDay: string }
  | { kind: "machineDetail"; machineId: string }
  | { kind: "machineActivity"; machineId: string }
  | { kind: "machineCounter"; machineId: string }
  | { kind: "dailyReport"; productionDay: string }
  | { kind: "notFound"; path: string };

export const applicationBasePath = "/";

export function parseApplicationRoute(pathname: string, search = ""): ApplicationRoute {
  if (pathname === "/supervisor-preview") return { kind: "supervisorPreview" };
  if (pathname === "/commissioning") return { kind: "commissioning" };
  if (pathname === "/overview") return { kind: "overview", search };
  if (pathname === "/fleet") return { kind: "fleet" };
  if (pathname === "/reports/range") return { kind: "dateRange" };
  if (pathname === "/") {
    return { kind: "productionDayOverview" };
  }

  if (!pathname.startsWith("/")) {
    return { kind: "notFound", path: pathname };
  }

  const rawSegments = pathname.slice(1).split("/");
  const segments = decodeSegments(rawSegments);
  if (segments === null) {
    return { kind: "notFound", path: pathname };
  }

  const first = segments[0];
  const second = segments[1];
  const third = segments[2];

  if (
    segments.length === 2 &&
    first === "production-days" &&
    second !== undefined &&
    second !== ""
  ) {
    return { kind: "productionDayDetail", productionDay: second };
  }

  if (
    segments.length === 3 &&
    first === "production-days" &&
    second !== undefined &&
    second !== "" &&
    third === "shifts"
  ) {
    return { kind: "shiftPerformance", productionDay: second };
  }

  if (
    segments.length === 2 &&
    first === "machines" &&
    second !== undefined &&
    second !== ""
  ) {
    return { kind: "machineDetail", machineId: second };
  }

  if (
    segments.length === 3 &&
    first === "production-days" &&
    second !== undefined &&
    second !== "" &&
    third === "report"
  ) {
    return { kind: "dailyReport", productionDay: second };
  }

  if (segments.length === 3 && first === "machines" && second !== undefined && second !== "") {
    if (third === "activity") return { kind: "machineActivity", machineId: second };
    if (third === "counter") return { kind: "machineCounter", machineId: second };
  }

  return { kind: "notFound", path: pathname };
}

export function routePath(route: Exclude<ApplicationRoute, { kind: "notFound" }>): string {
  switch (route.kind) {
    case "supervisorPreview": return "/supervisor-preview";
    case "commissioning": return "/commissioning";
    case "overview": return `/overview${route.search}`;
    case "fleet": return "/fleet";
    case "dateRange": return "/reports/range";
    case "productionDayOverview":
      return "/";
    case "productionDayDetail":
      return `/production-days/${encodeURIComponent(route.productionDay)}`;
    case "shiftPerformance":
      return `/production-days/${encodeURIComponent(route.productionDay)}/shifts`;
    case "machineDetail":
      return `/machines/${encodeURIComponent(route.machineId)}`;
    case "machineActivity":
      return `/machines/${encodeURIComponent(route.machineId)}/activity`;
    case "machineCounter":
      return `/machines/${encodeURIComponent(route.machineId)}/counter`;
    case "dailyReport":
      return `/production-days/${encodeURIComponent(route.productionDay)}/report`;
  }
}

function decodeSegments(rawSegments: readonly string[]): string[] | null {
  const decoded: string[] = [];
  try {
    for (const segment of rawSegments) {
      decoded.push(decodeURIComponent(segment));
    }
  } catch (error) {
    if (error instanceof URIError) {
      return null;
    }

    throw error;
  }

  return decoded;
}
