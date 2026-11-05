import type { ReportingClientOptions } from "./reporting-client-types";
import type { ReportingRoute } from "./reporting-routes";

const maximumTimeoutMilliseconds = 5 * 60 * 1000;

export interface ReportingHttpTransport {
  post(
    route: ReportingRoute,
    request: unknown,
    signal?: AbortSignal,
  ): Promise<Response>;
}

export function createReportingHttpTransport(
  options: ReportingClientOptions,
): ReportingHttpTransport {
  const baseAddress = normalizeBaseAddress(options.baseAddress);
  validateTimeout(options.timeoutMilliseconds);

  const fetchImplementation = options.fetch ?? globalThis.fetch;
  if (typeof fetchImplementation !== "function") {
    throw new Error("A fetch implementation is required for reporting HTTP transport.");
  }

  return {
    post(route, request, signal) {
      const url = new URL(route, baseAddress);
      const requestInit: RequestInit = {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json, application/problem+json",
        },
        body: JSON.stringify(request),
      };

      if (signal !== undefined) {
        requestInit.signal = signal;
      }

      return fetchImplementation(url, requestInit);
    },
  };
}

function normalizeBaseAddress(value: string): URL {
  let url: URL;

  try {
    url = new URL(value);
  } catch {
    throw new Error("Reporting API base address must be an absolute URL.");
  }

  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error("Reporting API base address must use HTTP or HTTPS.");
  }

  if (url.username.length > 0 || url.password.length > 0) {
    throw new Error("Reporting API base address must not contain credentials.");
  }

  if (url.search.length > 0) {
    throw new Error("Reporting API base address must not contain a query string.");
  }

  if (url.hash.length > 0) {
    throw new Error("Reporting API base address must not contain a fragment.");
  }

  if (!url.pathname.endsWith("/")) {
    url.pathname = `${url.pathname}/`;
  }

  return url;
}

function validateTimeout(value: number): void {
  if (!Number.isFinite(value) || value <= 0 || value > maximumTimeoutMilliseconds) {
    throw new Error(
      `Reporting request timeout must be greater than zero and no more than ${maximumTimeoutMilliseconds} milliseconds.`,
    );
  }
}
