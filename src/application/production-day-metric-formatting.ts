import type { CalculatedMetricDisplay, MetricValue } from "./production-day-presentation.ts";

const ratioUnit = "ratio";
const decimalPattern = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/;
const maximumExpandedZeros = 32n;

export function formatCalculatedMetric(metric: CalculatedMetricDisplay): string {
  if (metric.unit.toLowerCase() === ratioUnit) {
    return `${formatRatioAsPercentage(metric.value)}%`;
  }

  return `${String(metric.value)} ${metric.unit}`;
}

export function formatRatioAsPercentage(value: MetricValue): string {
  const text = typeof value === "number" ? String(value) : value;
  const match = decimalPattern.exec(text);
  if (match === null) {
    return `${text} × 100`;
  }

  const sign = match[1] ?? "";
  const integer = match[2] ?? "0";
  const fraction = match[3] ?? "";
  const exponent = BigInt(match[4] ?? "0");
  const rawDigits = `${integer}${fraction}`;
  const digits = rawDigits.replace(/^0+/, "");

  if (digits.length === 0) {
    return "0";
  }

  const scale = exponent - BigInt(fraction.length) + 2n;
  const decimalIndex = BigInt(digits.length) + scale;

  if (canExpand(decimalIndex, digits.length)) {
    return `${sign}${formatExpandedDecimal(digits, decimalIndex)}`;
  }

  return `${sign}${formatScientificDecimal(digits, scale)}`;
}

function canExpand(decimalIndex: bigint, digitCount: number): boolean {
  const digitCountBigInt = BigInt(digitCount);
  if (decimalIndex <= 0n) {
    return -decimalIndex <= maximumExpandedZeros;
  }

  if (decimalIndex >= digitCountBigInt) {
    return decimalIndex - digitCountBigInt <= maximumExpandedZeros;
  }

  return true;
}

function formatExpandedDecimal(digits: string, decimalIndex: bigint): string {
  const digitCount = BigInt(digits.length);

  if (decimalIndex <= 0n) {
    const zeroCount = Number(-decimalIndex);
    return normalizeDecimalText(`0.${"0".repeat(zeroCount)}${digits}`);
  }

  if (decimalIndex >= digitCount) {
    const zeroCount = Number(decimalIndex - digitCount);
    return normalizeDecimalText(`${digits}${"0".repeat(zeroCount)}`);
  }

  const split = Number(decimalIndex);
  return normalizeDecimalText(`${digits.slice(0, split)}.${digits.slice(split)}`);
}

function formatScientificDecimal(digits: string, scale: bigint): string {
  const exponent = scale + BigInt(digits.length - 1);
  const fractionalDigits = digits.slice(1).replace(/0+$/, "");
  const significand = fractionalDigits.length === 0
    ? digits[0]
    : `${digits[0]}.${fractionalDigits}`;
  return `${significand}e${exponent.toString()}`;
}

function normalizeDecimalText(value: string): string {
  const [wholePart = "0", fractionPart] = value.split(".", 2);
  const whole = wholePart.replace(/^0+(?=\d)/, "");
  if (fractionPart === undefined) {
    return whole;
  }

  const fraction = fractionPart.replace(/0+$/, "");
  return fraction.length === 0 ? whole : `${whole}.${fraction}`;
}
