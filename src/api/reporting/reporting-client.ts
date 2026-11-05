import type {
  OperationalMetricPage,
  ProductionDayQueryRequest,
  ProductionDayShiftPage,
  ProductionDayShiftQueryRequest,
  ReportingClient,
  ReportingClientOptions,
  ReportingRequestOptions,
  ShiftQueryRequest,
} from "./reporting-client-types.ts";
import { createReportingHttpTransport } from "./reporting-http-transport.ts";
import { createReportingRequestExecutor } from "./reporting-request-executor.ts";
import {
  createProductionDayShiftResponseDecoder,
  createReportingResponseDecoder,
} from "./reporting-response-decoder.ts";
import { throwProductionDayShiftConflict } from "./production-day-shift-coverage-response.ts";
import { reportingRoutes, type ReportingRoute } from "./reporting-routes.ts";

export function createReportingClient(
  options: ReportingClientOptions,
): ReportingClient {
  const transport = createReportingHttpTransport(options);
  const executor = createReportingRequestExecutor({
    transport,
    timeoutMilliseconds: options.timeoutMilliseconds,
  });
  const decoder = createReportingResponseDecoder();
  const productionDayShiftDecoder = createProductionDayShiftResponseDecoder();

  const executeAndDecode = async (
    route: ReportingRoute,
    request: unknown,
    requestOptions?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage> => {
    const response = await executor.execute(route, request, requestOptions);
    return decoder.decode(response);
  };

  return {
    queryShiftMetrics(
      request: ShiftQueryRequest,
      requestOptions?: ReportingRequestOptions,
    ) {
      return executeAndDecode(reportingRoutes.shiftQuery, request, requestOptions);
    },

    queryProductionDayMetrics(
      request: ProductionDayQueryRequest,
      requestOptions?: ReportingRequestOptions,
    ) {
      return executeAndDecode(
        reportingRoutes.productionDayQuery,
        request,
        requestOptions,
      );
    },

    async queryProductionDayShiftMetrics(
      request: ProductionDayShiftQueryRequest,
      requestOptions?: ReportingRequestOptions,
    ): Promise<ProductionDayShiftPage> {
      const response = await executor.execute(
        reportingRoutes.productionDayShiftQuery,
        request,
        requestOptions,
      );
      await throwProductionDayShiftConflict(response);
      return productionDayShiftDecoder.decode(response);
    },
  };
}
