import type {
  OperationalMetricPage,
  ProductionDayShiftPage,
  ReportingProblemDetails,
} from "./reporting-client-types.ts";
import {
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingProtocolFailure,
} from "./reporting-response-failures.ts";

const invalidQueryProblemType =
  "urn:factoryconnect:problem:reporting:invalid-request";
const malformedContinuationTokenProblemType =
  "urn:factoryconnect:problem:reporting:malformed-continuation-token";
const incompatibleContinuationTokenProblemType =
  "urn:factoryconnect:problem:reporting:incompatible-continuation-token";

const jsonNumberPattern = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/;
const unsignedIntegerPattern = /^\d+$/;
const maximumUInt64 = 18_446_744_073_709_551_615n;

export interface ReportingResponseDecoder {
  decode(response: Response): Promise<OperationalMetricPage>;
}

export interface ProductionDayShiftResponseDecoder {
  decode(response: Response): Promise<ProductionDayShiftPage>;
}

export function createReportingResponseDecoder(): ReportingResponseDecoder {
  return {
    async decode(response) {
      if (response.status === 200) {
        requireMediaType(response, "application/json");
        const body = await parseJson(response);
        if (!isOperationalMetricPage(body)) {
          throw new ReportingProtocolFailure(
            response.status,
            "Reporting API returned an invalid operational metric page.",
          );
        }

        return body;
      }

      if (response.status === 400) {
        requireMediaType(response, "application/problem+json");
        const body = await parseJson(response);
        if (!isProblemDetails(body)) {
          throw new ReportingProtocolFailure(
            response.status,
            "Reporting API returned invalid Problem Details.",
          );
        }

        return classifyProblemDetails(body);
      }

      if (response.status >= 200 && response.status < 300) {
        throw new ReportingProtocolFailure(
          response.status,
          `Reporting API returned unexpected successful HTTP status ${response.status}.`,
        );
      }

      throw new ReportingHttpFailure(response.status);
    },
  };
}

export function createProductionDayShiftResponseDecoder(): ProductionDayShiftResponseDecoder {
  return {
    async decode(response) {
      if (response.status === 200) {
        requireMediaType(response, "application/json");
        const body = await parseJson(response);
        if (!isProductionDayShiftPage(body)) {
          throw new ReportingProtocolFailure(
            response.status,
            "Reporting API returned an invalid production-day shift page.",
          );
        }

        return body;
      }

      if (response.status === 400) {
        requireMediaType(response, "application/problem+json");
        const body = await parseJson(response);
        if (!isProblemDetails(body)) {
          throw new ReportingProtocolFailure(
            response.status,
            "Reporting API returned invalid Problem Details.",
          );
        }

        return classifyProblemDetails(body);
      }

      if (response.status >= 200 && response.status < 300) {
        throw new ReportingProtocolFailure(
          response.status,
          `Reporting API returned unexpected successful HTTP status ${response.status}.`,
        );
      }

      throw new ReportingHttpFailure(response.status);
    },
  };
}

function classifyProblemDetails(problemDetails: ReportingProblemDetails): never {
  switch (problemDetails.type) {
    case invalidQueryProblemType:
      throw new ReportingInvalidQueryFailure(problemDetails);
    case malformedContinuationTokenProblemType:
      throw new ReportingMalformedContinuationTokenFailure(problemDetails);
    case incompatibleContinuationTokenProblemType:
      throw new ReportingIncompatibleContinuationTokenFailure(problemDetails);
    default:
      throw new ReportingHttpFailure(400, problemDetails);
  }
}

function requireMediaType(response: Response, expected: string): void {
  const contentType = response.headers.get("content-type");
  if (contentType === null) {
    throw new ReportingProtocolFailure(
      response.status,
      `Reporting API response is missing Content-Type; expected ${expected}.`,
    );
  }

  const mediaType = contentType.split(";", 1)[0]?.trim().toLowerCase();
  if (mediaType !== expected) {
    throw new ReportingProtocolFailure(
      response.status,
      `Reporting API returned Content-Type ${contentType}; expected ${expected}.`,
    );
  }
}

async function parseJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch (cause) {
    throw new ReportingProtocolFailure(
      response.status,
      "Reporting API returned malformed or empty JSON content.",
      cause,
    );
  }
}

function isOperationalMetricPage(value: unknown): value is OperationalMetricPage {
  if (!isRecord(value)) {
    return false;
  }

  return Array.isArray(value.items)
    && value.items.every(isOperationalMetricItem)
    && isNullableString(value.continuationToken);
}

function isProductionDayShiftPage(value: unknown): value is ProductionDayShiftPage {
  return isRecord(value)
    && Array.isArray(value.items)
    && value.items.every(isProductionDayShiftItem)
    && isNullableString(value.continuationToken);
}

function isProductionDayShiftItem(value: unknown): boolean {
  return isRecord(value)
    && isString(value.processorId)
    && isString(value.machineId)
    && isProductionDayPeriod(value.productionDay)
    && isString(value.productionLineId)
    && isShiftPeriod(value.shift)
    && isOperationalMetricContext(value.context)
    && (value.sourceRevision === null || isSourceRevision(value.sourceRevision))
    && Array.isArray(value.metrics)
    && value.metrics.every(isProductionDayShiftMetric);
}

function isProductionDayShiftMetric(value: unknown): boolean {
  return isRecord(value)
    && isString(value.metricKey)
    && isString(value.definitionVersion)
    && isMetricStatus(value.status)
    && isMetricValue(value.value)
    && isString(value.unit)
    && isNullableString(value.reasonCode)
    && isNullableString(value.reasonOperandName);
}

function isOperationalMetricItem(value: unknown): boolean {
  if (!isRecord(value)
    || !isString(value.scope)
    || !isString(value.processorId)
    || !isString(value.machineId)
    || !isOperationalMetricContext(value.context)
    || !isString(value.metricKey)
    || !isString(value.definitionVersion)
    || !isString(value.status)
    || !isMetricValue(value.value)
    || !isString(value.unit)
    || !isNullableString(value.reasonCode)
    || !isNullableString(value.reasonOperandName)
    || !isSourceRevision(value.sourceRevision)) {
    return false;
  }

  if (!isMetricStatus(value.status)) {
    return false;
  }

  if (value.scope === "shift") {
    return isShiftPeriod(value.shift) && value.productionDay === null;
  }

  if (value.scope === "production-day") {
    return value.shift === null && isProductionDayPeriod(value.productionDay);
  }

  return false;
}

function isMetricStatus(value: unknown): boolean {
  return value === "calculated"
    || value === "unavailable"
    || value === "insufficient-evidence";
}

function isOperationalMetricContext(value: unknown): boolean {
  return isRecord(value)
    && isNullableString(value.productionOrderId)
    && isNullableString(value.operationId)
    && isNullableString(value.partId)
    && isNullableString(value.operatorId);
}

function isSourceRevision(value: unknown): boolean {
  return isRecord(value)
    && isString(value.processorId)
    && isString(value.machineId)
    && isString(value.streamKey)
    && isPosition(value.position);
}

function isShiftPeriod(value: unknown): boolean {
  return isRecord(value)
    && isString(value.siteId)
    && isString(value.shiftScheduleAssignmentId)
    && isString(value.shiftId)
    && isString(value.startsAtUtc)
    && isString(value.endsAtUtc);
}

function isProductionDayPeriod(value: unknown): boolean {
  return isRecord(value)
    && isString(value.siteId)
    && isString(value.businessDate);
}

function isMetricValue(value: unknown): boolean {
  if (value === null) {
    return true;
  }

  if (typeof value === "number") {
    return Number.isFinite(value);
  }

  if (typeof value !== "string" || !jsonNumberPattern.test(value)) {
    return false;
  }

  return Number.isFinite(Number(value));
}

function isPosition(value: unknown): boolean {
  if (typeof value === "number") {
    return Number.isSafeInteger(value) && value >= 0;
  }

  return typeof value === "string"
    && unsignedIntegerPattern.test(value)
    && BigInt(value) <= maximumUInt64;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === "string";
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === "string";
}

function isProblemDetails(value: unknown): value is ReportingProblemDetails {
  if (!isRecord(value)) {
    return false;
  }

  return isOptionalNullableString(value.type)
    && isOptionalNullableString(value.title)
    && isOptionalProblemStatus(value.status)
    && isOptionalNullableString(value.detail)
    && isOptionalNullableString(value.instance);
}

function isOptionalNullableString(value: unknown): boolean {
  return value === undefined || value === null || typeof value === "string";
}

function isOptionalProblemStatus(value: unknown): boolean {
  return value === undefined
    || value === null
    || typeof value === "string"
    || (typeof value === "number" && Number.isFinite(value));
}
