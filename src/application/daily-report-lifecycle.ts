import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingCancellationFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingNetworkFailure,
  ReportingProtocolFailure,
  ReportingTimeoutFailure,
  type ProductionDayShiftRosterCoverageDetails,
  type ReportingClient,
  type ReportingClientFailure,
  type ReportingProblemDetails,
} from "../api/reporting/index.ts";
import {
  ProductionDayPresentationFailure,
} from "./production-day-presentation.ts";
import {
  isProductionDaySelection,
  queryAuthoritativeProductionDay,
} from "./production-day-reporting.ts";
import {
  queryAuthoritativeProductionDayShifts,
} from "./production-day-shift-reporting.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";
import { composeDailyReport } from "../presentation/daily-report-composer.ts";
import {
  DailyReportCompositionFailure,
  type DailyReportModel,
} from "../presentation/daily-report-model.ts";
import { ShiftPresentationContractFailure } from "../presentation/shift-performance-model.ts";

const supersededCancellationReason = Symbol("daily-report-superseded");
const disposedCancellationReason = Symbol("daily-report-disposed");
const failedSiblingCancellationReason = Symbol("daily-report-sibling-failed");

type ApplicationCancellationReason =
  | typeof supersededCancellationReason
  | typeof disposedCancellationReason
  | typeof failedSiblingCancellationReason;

export type DailyReportLifecycleState =
  | { readonly kind: "idle"; readonly canPrint: false }
  | { readonly kind: "loading"; readonly productionDay: string; readonly canPrint: false }
  | {
      readonly kind: "refreshing";
      readonly productionDay: string;
      readonly previous: DailyReportModel;
      readonly canPrint: false;
    }
  | {
      readonly kind: "success";
      readonly productionDay: string;
      readonly model: DailyReportModel;
      readonly canPrint: true;
    }
  | {
      readonly kind: "invalid-selection";
      readonly productionDay: string;
      readonly canPrint: false;
    }
  | {
      readonly kind: "roster-prerequisite-failure";
      readonly productionDay: string;
      readonly details: ProductionDayShiftRosterCoverageDetails;
      readonly previous: DailyReportModel | undefined;
      readonly canPrint: false;
    }
  | {
      readonly kind: "reporting-failure";
      readonly productionDay: string;
      readonly failure: ReportingClientFailure;
      readonly previous: DailyReportModel | undefined;
      readonly canPrint: false;
    }
  | {
      readonly kind: "presentation-contract-failure";
      readonly productionDay: string;
      readonly failure:
        | ProductionDayPresentationFailure
        | ShiftPresentationContractFailure
        | DailyReportCompositionFailure;
      readonly previous: DailyReportModel | undefined;
      readonly canPrint: false;
    }
  | {
      readonly kind: "invalid-request";
      readonly productionDay: string;
      readonly details: ReportingProblemDetails;
      readonly previous: DailyReportModel | undefined;
      readonly canPrint: false;
    };

export interface DailyReportLifecycleController {
  current(): DailyReportLifecycleState;
  subscribe(listener: (state: DailyReportLifecycleState) => void): () => void;
  execute(
    productionDay: string,
    sources: readonly DashboardRuntimeSource[],
  ): Promise<DailyReportLifecycleState>;
  dispose(): void;
}

export interface DailyReportLifecycleControllerOptions {
  readonly reportingClient: Pick<
    ReportingClient,
    "queryProductionDayMetrics" | "queryProductionDayShiftMetrics"
  >;
}

interface ActiveExecution {
  readonly generation: number;
  readonly productionDay: string;
  readonly controller: AbortController;
  applicationCancellationReason?: ApplicationCancellationReason;
}

export function createDailyReportLifecycleController(
  options: DailyReportLifecycleControllerOptions,
): DailyReportLifecycleController {
  let state: DailyReportLifecycleState = { kind: "idle", canPrint: false };
  let activeExecution: ActiveExecution | undefined;
  let nextGeneration = 0;
  let disposed = false;
  const listeners = new Set<(state: DailyReportLifecycleState) => void>();

  const publish = (next: DailyReportLifecycleState): DailyReportLifecycleState => {
    state = next;
    for (const listener of listeners) {
      listener(state);
    }
    return state;
  };

  const ownsPublication = (execution: ActiveExecution): boolean =>
    !disposed && activeExecution?.generation === execution.generation;

  const cancel = (
    execution: ActiveExecution,
    reason: ApplicationCancellationReason,
  ): void => {
    execution.applicationCancellationReason = reason;
    execution.controller.abort(reason);
  };

  const previousFor = (productionDay: string): DailyReportModel | undefined => {
    switch (state.kind) {
      case "success":
        return state.productionDay === productionDay ? state.model : undefined;
      case "refreshing":
      case "roster-prerequisite-failure":
      case "reporting-failure":
      case "presentation-contract-failure":
      case "invalid-request":
        return state.productionDay === productionDay ? state.previous : undefined;
      default:
        return undefined;
    }
  };

  return {
    current() {
      return state;
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    async execute(productionDay, sources) {
      if (disposed) {
        throw new Error("Daily Report lifecycle controller is disposed.");
      }

      const previous = previousFor(productionDay);
      if (activeExecution !== undefined) {
        cancel(activeExecution, supersededCancellationReason);
      }

      if (!isProductionDaySelection(productionDay)) {
        activeExecution = undefined;
        return publish({ kind: "invalid-selection", productionDay, canPrint: false });
      }

      const execution: ActiveExecution = {
        generation: ++nextGeneration,
        productionDay,
        controller: new AbortController(),
      };
      activeExecution = execution;
      const sourceSnapshot = sources.map(source => ({ ...source }));

      publish(previous === undefined
        ? { kind: "loading", productionDay, canPrint: false }
        : { kind: "refreshing", productionDay, previous, canPrint: false });

      try {
        let productionDayResult;
        let shiftResult;
        try {
          [productionDayResult, shiftResult] = await Promise.all([
            queryAuthoritativeProductionDay(
              productionDay,
              sourceSnapshot,
              options.reportingClient,
              { signal: execution.controller.signal },
            ),
            queryAuthoritativeProductionDayShifts(
              productionDay,
              sourceSnapshot,
              options.reportingClient,
              { signal: execution.controller.signal },
            ),
          ]);
        } catch (error) {
          if (!execution.controller.signal.aborted) {
            cancel(execution, failedSiblingCancellationReason);
          }
          throw error;
        }

        if (!ownsPublication(execution)) {
          return state;
        }

        const model = composeDailyReport({
          productionDay,
          sources: sourceSnapshot,
          productionDayResult,
          shiftResult,
        });

        if (!ownsPublication(execution)) {
          return state;
        }

        activeExecution = undefined;
        return publish({ kind: "success", productionDay, model, canPrint: true });
      } catch (error) {
        if (!ownsPublication(execution)) {
          if (isExpectedDailyReportFailure(error)) {
            return state;
          }
          throw error;
        }

        activeExecution = undefined;

        if (error instanceof ReportingInvalidQueryFailure) {
          return publish({
            kind: "invalid-request",
            productionDay,
            details: error.problemDetails,
            previous,
            canPrint: false,
          });
        }

        if (error instanceof ProductionDayShiftRosterCoverageRequiredFailure) {
          return publish({
            kind: "roster-prerequisite-failure",
            productionDay,
            details: {
              machineId: error.machineId,
              siteId: error.siteId,
              businessDate: error.businessDate,
            },
            previous,
            canPrint: false,
          });
        }

        if (isPresentationContractFailure(error)) {
          return publish({
            kind: "presentation-contract-failure",
            productionDay,
            failure: error,
            previous,
            canPrint: false,
          });
        }

        if (isReportingClientFailure(error)) {
          return publish({
            kind: "reporting-failure",
            productionDay,
            failure: error,
            previous,
            canPrint: false,
          });
        }

        publish({ kind: "idle", canPrint: false });
        throw error;
      }
    },

    dispose() {
      if (disposed) {
        return;
      }

      disposed = true;
      if (activeExecution !== undefined) {
        cancel(activeExecution, disposedCancellationReason);
        activeExecution = undefined;
      }
      listeners.clear();
    },
  };
}

function isPresentationContractFailure(
  error: unknown,
): error is
  | ProductionDayPresentationFailure
  | ShiftPresentationContractFailure
  | DailyReportCompositionFailure {
  return error instanceof ProductionDayPresentationFailure
    || error instanceof ShiftPresentationContractFailure
    || error instanceof DailyReportCompositionFailure;
}

function isReportingClientFailure(error: unknown): error is ReportingClientFailure {
  return error instanceof ReportingCancellationFailure
    || error instanceof ReportingTimeoutFailure
    || error instanceof ReportingNetworkFailure
    || error instanceof ReportingInvalidQueryFailure
    || error instanceof ReportingMalformedContinuationTokenFailure
    || error instanceof ReportingIncompatibleContinuationTokenFailure
    || error instanceof ProductionDayShiftRosterCoverageRequiredFailure
    || error instanceof ReportingHttpFailure
    || error instanceof ReportingProtocolFailure;
}

function isExpectedDailyReportFailure(error: unknown): boolean {
  return isReportingClientFailure(error) || isPresentationContractFailure(error);
}
