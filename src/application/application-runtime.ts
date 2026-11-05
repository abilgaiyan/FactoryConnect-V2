import {
  createReportingClient,
  type ReportingClient,
} from "../api/reporting/index.ts";
import {
  createCurrentStateClient,
  type CurrentStateClient,
} from "../api/current-state/current-state-client.ts";
import {
  loadDashboardRuntimeConfiguration,
  type DashboardRuntimeConfiguration,
} from "./runtime-configuration.ts";

export interface DashboardApplicationRuntime {
  readonly configuration: DashboardRuntimeConfiguration;
  readonly reportingClient: ReportingClient;
  readonly currentStateClient: CurrentStateClient;
  readonly now?: () => Date;
}

export async function createDashboardApplicationRuntime(
  origin: string,
  fetchImplementation: typeof globalThis.fetch = globalThis.fetch,
): Promise<DashboardApplicationRuntime> {
  const configuration = await loadDashboardRuntimeConfiguration(fetchImplementation);
  const baseAddress = new URL(configuration.reportingBasePath, origin).toString();
  const reportingClient = createReportingClient({
    baseAddress,
    timeoutMilliseconds: configuration.requestTimeoutMilliseconds,
    fetch: fetchImplementation,
  });

  const currentStateClient = createCurrentStateClient({
    baseAddress,
    timeoutMilliseconds: configuration.requestTimeoutMilliseconds,
    fetch: fetchImplementation,
  });

  return {
    configuration,
    reportingClient,
    currentStateClient,
    now: () => new Date(),
  };
}
