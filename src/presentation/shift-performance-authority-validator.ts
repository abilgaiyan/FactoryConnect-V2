import type { ProductionDayShiftPage } from "../api/reporting/index.ts";
import type { AuthoritativeProductionDayShiftResult } from "../application/production-day-shift-reporting.ts";
import type { DashboardRuntimeSource } from "../application/runtime-configuration.ts";
import { overviewMetricDefinitions } from "../application/operational-metric-identities.ts";
import { ShiftPresentationContractFailure } from "./shift-performance-model.ts";

export interface ValidatedProductionDayShiftResult {
  readonly items: ProductionDayShiftPage["items"];
}

type ShiftReport = ProductionDayShiftPage["items"][number];
type ShiftMetric = ShiftReport["metrics"][number];

const allowedMetrics = new Set(
  overviewMetricDefinitions.map(({ metricKey, version }) => `${metricKey}\u0000${version}`),
);
const utcInstantPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/;

interface ParsedUtcInstant {
  readonly dateTime: string;
  readonly fraction: string;
}

export function validateProductionDayShiftAuthority(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  result: AuthoritativeProductionDayShiftResult,
): ValidatedProductionDayShiftResult {
  const configuredSources = new Map<string, DashboardRuntimeSource>();
  for (const source of sources) {
    const key = sourceKey(source.machineId, source.processorId);
    if (configuredSources.has(key)) {
      fail("unexpected-source", "Configured factory population contains a duplicate reporting source identity.");
    }
    configuredSources.set(key, source);
  }

  const seenReports = new Set<string>();
  const occurrenceDescriptors = new Map<string, string>();
  const previousBySource = new Map<string, ShiftReport>();

  for (const report of result.items) {
    const configured = configuredSources.get(sourceKey(report.machineId, report.processorId));
    if (configured === undefined) {
      fail("unexpected-source", "Shift report belongs to a source outside the configured factory population.");
    }

    if (report.productionDay.businessDate !== productionDay) {
      fail("unexpected-production-day", "Shift report belongs to a production day other than the selected production day.");
    }

    if (report.productionDay.siteId !== configured.siteId
      || report.shift.siteId !== configured.siteId
      || report.shift.siteId !== report.productionDay.siteId) {
      fail("unexpected-site", "Shift report site identity does not match the configured source and production day.");
    }

    if (report.productionLineId !== configured.productionLineId) {
      fail("unexpected-production-line", "Shift report production line does not match the configured scheduling lineage.");
    }

    if (!isCanonicalUnpartitionedContext(report.context)) {
      fail("unexpected-context", "Shift report context is not the canonical unpartitioned context requested by the overview.");
    }

    if (report.sourceRevision !== null
      && report.sourceRevision.machineId !== report.machineId) {
      fail("unexpected-source-revision", "Shift report source revision belongs to a different machine stream.");
    }

    const startsAt = parseUtcInstant(report.shift.startsAtUtc);
    const endsAt = parseUtcInstant(report.shift.endsAtUtc);
    if (startsAt === null || endsAt === null || compareUtcInstants(startsAt, endsAt) >= 0) {
      fail("inconsistent-occurrence-descriptor", "Shift occurrence timestamps must be valid zero-offset UTC instants with start before end.");
    }

    const occurrenceKey = shiftOccurrenceKey(report);
    const descriptor = shiftDescriptor(report);
    const priorDescriptor = occurrenceDescriptors.get(occurrenceKey);
    if (priorDescriptor !== undefined && priorDescriptor !== descriptor) {
      fail("inconsistent-occurrence-descriptor", "The same shift occurrence identity has conflicting authoritative descriptors.");
    }
    occurrenceDescriptors.set(occurrenceKey, descriptor);

    const reportKey = `${sourceKey(report.machineId, report.processorId)}\u0000${report.productionDay.siteId}\u0000${report.productionDay.businessDate}\u0000${descriptor}`;
    if (seenReports.has(reportKey)) {
      fail("duplicate-occurrence", "The authoritative result contains a duplicate shift occurrence report.");
    }
    seenReports.add(reportKey);

    const source = sourceKey(report.machineId, report.processorId);
    const previous = previousBySource.get(source);
    if (previous !== undefined && compareOccurrence(previous, report) >= 0) {
      fail("out-of-order-occurrence", "Shift reports are not in strictly increasing authoritative occurrence order.");
    }
    previousBySource.set(source, report);

    validateMetrics(report.metrics);
  }

  return { items: result.items };
}

function validateMetrics(metrics: readonly ShiftMetric[]): void {
  const seen = new Set<string>();
  for (const metric of metrics) {
    const identity = `${metric.metricKey}\u0000${metric.definitionVersion}`;
    if (!allowedMetrics.has(identity)) {
      fail("unexpected-metric", "Shift report contains a metric outside the exact FC-029.3 overview vocabulary.");
    }
    if (seen.has(identity)) {
      fail("duplicate-metric", "Shift report contains a duplicate exact metric definition identity.");
    }
    seen.add(identity);

    if (metric.unit.length === 0) {
      fail("malformed-metric-state", "Every authoritative shift metric requires a non-empty unit.");
    }

    if (metric.status === "calculated") {
      if (metric.value === null || metric.reasonCode !== null || metric.reasonOperandName !== null) {
        fail("malformed-metric-state", "Calculated shift metrics require a value and must not carry reason evidence.");
      }
      continue;
    }

    if (metric.value !== null || metric.reasonCode === null || metric.reasonCode.length === 0) {
      fail("malformed-metric-state", "Non-calculated shift metrics require null value and a non-empty reason code.");
    }
  }
}

function isCanonicalUnpartitionedContext(context: ShiftReport["context"]): boolean {
  return context.productionOrderId === null
    && context.operationId === null
    && context.partId === null
    && context.operatorId === null;
}

function sourceKey(machineId: string, processorId: string): string {
  return `${machineId}\u0000${processorId}`;
}

function shiftOccurrenceKey(report: ShiftReport): string {
  return `${report.machineId}\u0000${report.productionLineId}\u0000${report.shift.shiftScheduleAssignmentId}\u0000${report.shift.shiftId}`;
}

function shiftDescriptor(report: ShiftReport): string {
  const shift = report.shift;
  return `${shift.siteId}\u0000${shift.shiftScheduleAssignmentId}\u0000${shift.shiftId}\u0000${shift.startsAtUtc}\u0000${shift.endsAtUtc}`;
}

function compareOccurrence(left: ShiftReport, right: ShiftReport): number {
  const leftStart = requireUtcInstant(left.shift.startsAtUtc);
  const rightStart = requireUtcInstant(right.shift.startsAtUtc);
  const leftEnd = requireUtcInstant(left.shift.endsAtUtc);
  const rightEnd = requireUtcInstant(right.shift.endsAtUtc);

  return compareUtcInstants(leftStart, rightStart)
    || compareUtcInstants(leftEnd, rightEnd)
    || compareText(left.shift.siteId, right.shift.siteId)
    || compareText(left.shift.shiftScheduleAssignmentId, right.shift.shiftScheduleAssignmentId)
    || compareText(left.shift.shiftId, right.shift.shiftId);
}

function parseUtcInstant(value: string): ParsedUtcInstant | null {
  const match = utcInstantPattern.exec(value);
  if (match === null) {
    return null;
  }

  const year = match[1];
  const month = match[2];
  const day = match[3];
  const hour = match[4];
  const minute = match[5];
  const second = match[6];
  const fraction = match[7] ?? "";
  const offset = match[8];
  if (year === undefined
    || month === undefined
    || day === undefined
    || hour === undefined
    || minute === undefined
    || second === undefined
    || offset === undefined
    || (offset !== "Z" && offset !== "+00:00")
    || !isValidUtcDateTime(year, month, day, hour, minute, second)) {
    return null;
  }

  return {
    dateTime: `${year}${month}${day}${hour}${minute}${second}`,
    fraction,
  };
}

function requireUtcInstant(value: string): ParsedUtcInstant {
  const parsed = parseUtcInstant(value);
  if (parsed === null) {
    fail("inconsistent-occurrence-descriptor", "Shift occurrence contains an invalid zero-offset UTC timestamp.");
  }
  return parsed;
}

function compareUtcInstants(left: ParsedUtcInstant, right: ParsedUtcInstant): number {
  const dateTimeComparison = compareText(left.dateTime, right.dateTime);
  if (dateTimeComparison !== 0) {
    return dateTimeComparison;
  }

  const precision = Math.max(left.fraction.length, right.fraction.length);
  return compareText(
    left.fraction.padEnd(precision, "0"),
    right.fraction.padEnd(precision, "0"),
  );
}

function isValidUtcDateTime(
  yearText: string,
  monthText: string,
  dayText: string,
  hourText: string,
  minuteText: string,
  secondText: string,
): boolean {
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const hour = Number(hourText);
  const minute = Number(minuteText);
  const second = Number(secondText);
  const days = daysInMonth(year, month);
  return year >= 1
    && days !== null
    && day >= 1 && day <= days
    && hour >= 0 && hour <= 23
    && minute >= 0 && minute <= 59
    && second >= 0 && second <= 59;
}

function daysInMonth(year: number, month: number): number | null {
  if (month < 1 || month > 12) {
    return null;
  }
  if (month === 2) {
    return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28;
  }
  return month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31;
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function fail(
  reason: ConstructorParameters<typeof ShiftPresentationContractFailure>[0],
  message: string,
): never {
  throw new ShiftPresentationContractFailure(reason, message);
}
