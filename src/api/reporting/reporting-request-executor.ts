import type { ReportingRequestOptions } from "./reporting-client-types.ts";
import type { ReportingHttpTransport } from "./reporting-http-transport.ts";
import type { ReportingRoute } from "./reporting-routes.ts";
import {
  ReportingCancellationFailure,
  ReportingNetworkFailure,
  ReportingTimeoutFailure,
  type ReportingTransportFailure,
} from "./reporting-transport-failures.ts";

export interface ReportingRequestExecutor {
  execute(
    route: ReportingRoute,
    request: unknown,
    options?: ReportingRequestOptions,
  ): Promise<Response>;
}

interface ReportingTimerScheduler {
  schedule(callback: () => void, delayMilliseconds: number): unknown;
  clear(handle: unknown): void;
}

interface ReportingRequestExecutorOptions {
  transport: ReportingHttpTransport;
  timeoutMilliseconds: number;
  timerScheduler?: ReportingTimerScheduler;
}

const defaultTimerScheduler: ReportingTimerScheduler = {
  schedule(callback, delayMilliseconds) {
    return globalThis.setTimeout(callback, delayMilliseconds);
  },
  clear(handle) {
    globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>);
  },
};

export function createReportingRequestExecutor(
  options: ReportingRequestExecutorOptions,
): ReportingRequestExecutor {
  const timerScheduler = options.timerScheduler ?? defaultTimerScheduler;

  return {
    async execute(route, request, requestOptions) {
      const callerSignal = requestOptions?.signal;
      if (callerSignal?.aborted === true) {
        throw new ReportingCancellationFailure(callerSignal.reason);
      }

      const controller = new AbortController();
      let terminalFailure: ReportingTransportFailure | undefined;

      const recordFailure = (failure: ReportingTransportFailure): void => {
        if (terminalFailure === undefined) {
          terminalFailure = failure;
          controller.abort(failure);
        }
      };

      const onCallerAbort = (): void => {
        recordFailure(new ReportingCancellationFailure(callerSignal?.reason));
      };

      if (callerSignal !== undefined) {
        callerSignal.addEventListener("abort", onCallerAbort, { once: true });
      }

      const timeoutHandle = timerScheduler.schedule(() => {
        recordFailure(new ReportingTimeoutFailure(options.timeoutMilliseconds));
      }, options.timeoutMilliseconds);

      try {
        const response = await options.transport.post(route, request, controller.signal);

        if (terminalFailure !== undefined) {
          throw terminalFailure;
        }

        return response;
      } catch (cause) {
        if (terminalFailure !== undefined) {
          throw terminalFailure;
        }

        throw new ReportingNetworkFailure(cause);
      } finally {
        timerScheduler.clear(timeoutHandle);
        callerSignal?.removeEventListener("abort", onCallerAbort);
      }
    },
  };
}
