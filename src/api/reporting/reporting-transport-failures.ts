export type ReportingTransportFailure =
  | ReportingCancellationFailure
  | ReportingTimeoutFailure
  | ReportingNetworkFailure;

export class ReportingCancellationFailure extends Error {
  readonly kind = "cancellation" as const;

  constructor(cause?: unknown) {
    super("Reporting request was cancelled.", { cause });
    this.name = "ReportingCancellationFailure";
  }
}

export class ReportingTimeoutFailure extends Error {
  readonly kind = "timeout" as const;
  readonly timeoutMilliseconds: number;

  constructor(timeoutMilliseconds: number) {
    super(`Reporting request timed out after ${timeoutMilliseconds} milliseconds.`);
    this.name = "ReportingTimeoutFailure";
    this.timeoutMilliseconds = timeoutMilliseconds;
  }
}

export class ReportingNetworkFailure extends Error {
  readonly kind = "network" as const;

  constructor(cause: unknown) {
    super("Reporting request failed before an HTTP response was received.", { cause });
    this.name = "ReportingNetworkFailure";
  }
}
