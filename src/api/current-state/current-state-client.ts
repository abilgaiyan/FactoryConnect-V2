import type { paths } from "../generated/reporting-contract.ts";

type Operation = NonNullable<paths["/api/machines/v1/{machineId}/current-state"]["get"]>;
export type CurrentMachineStateResponse = Operation["responses"][200]["content"]["application/json"];
export type CurrentMachineStateProblemDetails = Operation["responses"][400]["content"]["application/problem+json"];

export class CurrentStateAuthorityUnavailableFailure extends Error {
  readonly kind = "authority-unavailable" as const;
  readonly problemDetails: CurrentMachineStateProblemDetails;

  constructor(problemDetails: CurrentMachineStateProblemDetails) {
    super("Authoritative current machine state is temporarily unavailable.");
    this.name = "CurrentStateAuthorityUnavailableFailure";
    this.problemDetails = problemDetails;
  }
}

export class CurrentStateInvalidMachineFailure extends Error {
  readonly kind = "invalid-machine" as const;
  readonly problemDetails: CurrentMachineStateProblemDetails;

  constructor(problemDetails: CurrentMachineStateProblemDetails) {
    super("The current-state machine identity is invalid.");
    this.name = "CurrentStateInvalidMachineFailure";
    this.problemDetails = problemDetails;
  }
}

export class CurrentStateTransportFailure extends Error {
  readonly kind = "transport" as const;
  readonly status: number;

  constructor(status: number, message: string, cause?: unknown) {
    super(message, { cause });
    this.name = "CurrentStateTransportFailure";
    this.status = status;
  }
}

export interface CurrentStateClient {
  read(machineId: string, options?: { signal?: AbortSignal }): Promise<CurrentMachineStateResponse>;
}

export function createCurrentStateClient(options: {
  baseAddress: string;
  timeoutMilliseconds: number;
  fetch?: typeof globalThis.fetch;
}): CurrentStateClient {
  const fetchImplementation = options.fetch ?? globalThis.fetch;

  return {
    async read(machineId, requestOptions) {
      const controller = new AbortController();
      const callerSignal = requestOptions?.signal;
      const onAbort = () => controller.abort(callerSignal?.reason);
      callerSignal?.addEventListener("abort", onAbort, { once: true });
      const timeout = globalThis.setTimeout(
        () => controller.abort(new Error("Current-state request timed out.")),
        options.timeoutMilliseconds,
      );

      try {
        let response: Response;
        try {
          response = await fetchImplementation(
            new URL(`api/machines/v1/${encodeURIComponent(machineId)}/current-state`, options.baseAddress),
            {
              method: "GET",
              headers: { Accept: "application/json, application/problem+json" },
              signal: controller.signal,
            },
          );
        } catch (cause) {
          if (callerSignal?.aborted === true) {
            throw cause;
          }
          throw new CurrentStateTransportFailure(0, "Current-state request failed.", cause);
        }

        if (response.status === 200) {
          requireMediaType(response, "application/json");
          const body = await parseJson(response);
          if (!isCurrentStateResponse(body)) {
            throw new CurrentStateTransportFailure(200, "Current-state response is incompatible.");
          }
          return body;
        }

        if (response.status === 400 || response.status === 503) {
          requireMediaType(response, "application/problem+json");
          const problem = await parseJson(response);
          if (!isProblemDetails(problem)) {
            throw new CurrentStateTransportFailure(response.status, "Current-state Problem Details are incompatible.");
          }
          if (response.status === 400) {
            throw new CurrentStateInvalidMachineFailure(problem);
          }
          throw new CurrentStateAuthorityUnavailableFailure(problem);
        }

        throw new CurrentStateTransportFailure(response.status, `Current-state request returned HTTP ${response.status}.`);
      } finally {
        globalThis.clearTimeout(timeout);
        callerSignal?.removeEventListener("abort", onAbort);
      }
    },
  };
}

function requireMediaType(response: Response, expected: string): void {
  const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (mediaType !== expected) {
    throw new CurrentStateTransportFailure(response.status, `Current-state response must use ${expected}.`);
  }
}

async function parseJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch (cause) {
    throw new CurrentStateTransportFailure(response.status, "Current-state response contains malformed JSON.", cause);
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isProblemDetails(value: unknown): value is CurrentMachineStateProblemDetails {
  return isRecord(value);
}

function isCurrentStateResponse(value: unknown): value is CurrentMachineStateResponse {
  if (!isRecord(value)
    || typeof value.machineId !== "string"
    || (value.outcome !== "evidence" && value.outcome !== "no-evidence")
    || !["complete", "behind", "indeterminate"].includes(String(value.coverage))) {
    return false;
  }
  if (value.outcome === "no-evidence") {
    return value.evidence === null;
  }
  if (!isRecord(value.evidence)) {
    return false;
  }
  return ["unknown", "stopped", "idle", "running", "fault"].includes(String(value.evidence.machineState))
    && ["current", "stale", "indeterminate"].includes(String(value.evidence.freshness))
    && ["current", "stale", "indeterminate", "behind"].includes(String(value.evidence.usability))
    && typeof value.evidence.readAsOf === "string";
}
