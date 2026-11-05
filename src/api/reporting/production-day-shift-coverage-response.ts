import type { ReportingProblemDetails } from "./reporting-client-types.ts";
import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingHttpFailure,
  ReportingProtocolFailure,
} from "./reporting-response-failures.ts";

const coverageProblemType = "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required";
const coverageProblemCode = "production-day-shift-roster-coverage-required";
const guidPattern = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const emptyGuid = "00000000-0000-0000-0000-000000000000";
const businessDatePattern = /^(\d{4})-(\d{2})-(\d{2})$/;

export async function throwProductionDayShiftConflict(response: Response): Promise<void> {
  if (response.status !== 409) {
    return;
  }

  const contentType = response.headers.get("content-type");
  const mediaType = contentType?.split(";", 1)[0]?.trim().toLowerCase();
  if (mediaType !== "application/problem+json") {
    throw new ReportingProtocolFailure(409, "Production-day shift reporting conflict must use application/problem+json.");
  }

  let body: unknown;
  try {
    body = await response.json();
  } catch (cause) {
    throw new ReportingProtocolFailure(409, "Production-day shift reporting conflict returned malformed or empty JSON content.", cause);
  }

  if (!isProblemDetails(body)) {
    throw new ReportingProtocolFailure(409, "Production-day shift reporting conflict returned invalid Problem Details.");
  }

  if (body.type !== coverageProblemType) {
    throw new ReportingHttpFailure(409, body);
  }

  if (body.code !== coverageProblemCode
    || !isGuidWireIdentity(body.machineId)
    || !isTrimmedNonEmptyString(body.siteId)
    || !isBusinessDate(body.businessDate)) {
    throw new ReportingProtocolFailure(409, "Roster-coverage Problem Details contains invalid required reporting identity fields.");
  }

  throw new ProductionDayShiftRosterCoverageRequiredFailure(body, {
    machineId: body.machineId,
    siteId: body.siteId,
    businessDate: body.businessDate,
  });
}

type ProblemRecord = ReportingProblemDetails & Record<string, unknown>;

function isProblemDetails(value: unknown): value is ProblemRecord {
  if (!isRecord(value)) {
    return false;
  }

  return isOptionalNullableString(value.type)
    && isOptionalNullableString(value.title)
    && isOptionalProblemStatus(value.status)
    && isOptionalNullableString(value.detail)
    && isOptionalNullableString(value.instance);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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

function isGuidWireIdentity(value: unknown): value is string {
  return typeof value === "string"
    && guidPattern.test(value)
    && value.toLowerCase() !== emptyGuid;
}

function isTrimmedNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.trim() === value;
}

function isBusinessDate(value: unknown): value is string {
  if (typeof value !== "string") {
    return false;
  }

  const match = businessDatePattern.exec(value);
  if (match === null) {
    return false;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1) {
    return false;
  }

  const daysInMonth = month === 2
    ? (isLeapYear(year) ? 29 : 28)
    : (month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31);

  return day <= daysInMonth;
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}
