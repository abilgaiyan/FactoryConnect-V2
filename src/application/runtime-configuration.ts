import { validProfiles, type MachineProfile } from "./machine-capabilities.ts";
export interface DashboardRuntimeSource {
  readonly machineId: string;
  readonly processorId: string;
  readonly siteId: string;
  readonly productionLineId: string;
  readonly displayName: string;
  readonly groupName: string | null;
  readonly displayOrder: number;
}

export interface DashboardRuntimeConfiguration {
  readonly machineProfileSchemaVersion?: 1;
  readonly machineProfiles?: readonly MachineProfile[];
  readonly reportingBasePath: string;
  readonly requestTimeoutMilliseconds: number;
  readonly sources: readonly DashboardRuntimeSource[];
}

export async function loadDashboardRuntimeConfiguration(
  fetchImplementation: typeof globalThis.fetch = globalThis.fetch,
): Promise<DashboardRuntimeConfiguration> {
  const response = await fetchImplementation("/dashboard/config", {
    method: "GET",
    headers: { Accept: "application/json" },
  });

  if (!response.ok) {
    throw new Error(`Dashboard runtime configuration returned HTTP ${response.status}.`);
  }

  const value: unknown = await response.json();
  if (!isRuntimeConfiguration(value)) {
    throw new Error("Dashboard runtime configuration is malformed.");
  }

  return value;
}

function isRuntimeConfiguration(value: unknown): value is DashboardRuntimeConfiguration {
  if (!isRecord(value)) {
    return false;
  }

  return (
    value.reportingBasePath === "/" &&
    typeof value.requestTimeoutMilliseconds === "number" &&
    Number.isInteger(value.requestTimeoutMilliseconds) &&
    value.requestTimeoutMilliseconds > 0 &&
    Array.isArray(value.sources) &&
    value.sources.every(isRuntimeSource) &&
    validProfiles(value.machineProfileSchemaVersion, value.machineProfiles, value.sources.filter(isRuntimeSource).map(s => s.machineId.toLowerCase()))
  );
}

function isRuntimeSource(value: unknown): value is DashboardRuntimeSource {
  return (
    isRecord(value) &&
    typeof value.machineId === "string" &&
    value.machineId.length > 0 &&
    typeof value.processorId === "string" &&
    value.processorId.length > 0 &&
    typeof value.siteId === "string" &&
    value.siteId.trim() === value.siteId &&
    value.siteId.length > 0 &&
    typeof value.productionLineId === "string" &&
    value.productionLineId.trim() === value.productionLineId &&
    value.productionLineId.length > 0 &&
    typeof value.displayName === "string" &&
    value.displayName.length > 0 &&
    (value.groupName === null ||
      (typeof value.groupName === "string" && value.groupName.length > 0)) &&
    typeof value.displayOrder === "number" &&
    Number.isInteger(value.displayOrder) &&
    value.displayOrder >= 0
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
