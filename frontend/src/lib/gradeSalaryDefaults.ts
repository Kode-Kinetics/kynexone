import type { GradePayScaleComponentDto } from '../api/organization';

/**
 * The grade pay scale → monthly salary rule, as the create wizard shows it. It mirrors the server's ONE rule
 * (backend EmployeeSalaryBreakdownWriter.MergeGradePayScale / MonthlySalaryField), so what the form pre-fills, the gross
 * package it shows and what payroll is paid are the same numbers:
 * - only an active MONTHLY line is monthly salary (an Annual ticket or bonus is not);
 * - an Earning line is cash by its code: BASIC, HOUS…, TRANS…, FOOD…, MOBILE…, anything else Other allowance;
 * - a Deduction line is the fixed deduction; a Benefit line (medical cover, a ticket in kind) is never cash;
 * - basic resolves first (what the user typed, else the grade's fixed BASIC), then every percent-of-basic line is
 *   that basic × its percentage, to 2 dp, half away from zero.
 * A field left blank is filled from the grade by the server too; a figure typed — 0 included — is kept.
 */
export type SalaryAmountField =
  | 'basicSalary' | 'housingAllowance' | 'transportAllowance' | 'foodAllowance' | 'mobileAllowance' | 'otherAllowance' | 'fixedDeduction';

export const SALARY_AMOUNT_FIELDS: readonly SalaryAmountField[] = [
  'basicSalary', 'housingAllowance', 'transportAllowance', 'foodAllowance', 'mobileAllowance', 'otherAllowance', 'fixedDeduction',
];

export type SalaryAmounts = Partial<Record<SalaryAmountField, number | undefined>>;

type PayScaleLine = Pick<GradePayScaleComponentDto, 'componentCode' | 'componentType' | 'calculationType' | 'amount' | 'percentage' | 'frequency' | 'isActive'>;

const same = (a: string | undefined | null, b: string) => (a ?? '').trim().toLowerCase() === b.toLowerCase();

/** The salary field a pay-scale line fills, or null when it is not part of the monthly salary. */
export function monthlySalaryField(line: PayScaleLine): SalaryAmountField | null {
  if (!line.isActive) return null;
  const frequency = (line.frequency ?? '').trim();
  if (frequency && !same(frequency, 'Monthly')) return null;
  const type = (line.componentType ?? '').trim();
  if (same(type, 'Deduction')) return 'fixedDeduction';
  if (type && !same(type, 'Earning')) return null;
  const code = (line.componentCode ?? '').toUpperCase();
  if (code.includes('BASIC')) return 'basicSalary';
  if (code.includes('HOUS')) return 'housingAllowance';
  if (code.includes('TRANS')) return 'transportAllowance';
  if (code.includes('FOOD')) return 'foodAllowance';
  if (code.includes('MOBILE')) return 'mobileAllowance';
  return 'otherAllowance';
}

const isPercent = (line: PayScaleLine) => same(line.calculationType, 'PercentOfBasic');

/** Round to 2 dp, half away from zero (as the server's Math.Round(…, 2, MidpointRounding.AwayFromZero)). */
export function roundMoney(value: number): number {
  const scaled = Math.abs(value) * 100;
  // Guard binary noise (1000.2 × 12.5 = 12502.499999…) before deciding the half.
  const rounded = Math.round(Number(scaled.toFixed(6))) / 100;
  return value < 0 ? -rounded : rounded;
}

/**
 * The monthly package the grade gives on top of what is typed: `typed` fields that are a number (0 included) are kept,
 * blank (undefined) fields are filled from the grade; a field the grade has no line for stays blank.
 */
export function mergeGradeSalary(typed: SalaryAmounts, lines: readonly PayScaleLine[]): SalaryAmounts {
  const classified = lines.flatMap((line) => {
    const field = monthlySalaryField(line);
    return field ? [{ line, field }] : [];
  });
  if (classified.length === 0) return { ...typed };
  const gradeBasic = classified.filter((x) => x.field === 'basicSalary' && !isPercent(x.line));
  const basic = typed.basicSalary ?? (gradeBasic.length > 0 ? gradeBasic.reduce((sum, x) => sum + Number(x.line.amount || 0), 0) : undefined);
  const out: SalaryAmounts = { ...typed, basicSalary: basic };
  for (const field of SALARY_AMOUNT_FIELDS) {
    if (field === 'basicSalary' || typed[field] !== undefined) continue;
    const forField = classified.filter((x) => x.field === field);
    if (forField.length === 0) continue;
    out[field] = roundMoney(forField.reduce((sum, x) => sum + (isPercent(x.line)
      ? roundMoney((basic ?? 0) * Number(x.line.percentage || 0) / 100)
      : Number(x.line.amount || 0)), 0));
  }
  return out;
}

/**
 * Pre-fill the form from the grade. A field counts as the user's own once it holds a value the grade did not put
 * there; every other field (blank, or still the value the grade filled last time) follows the grade. So:
 * - choosing or changing the grade fills basic and every allowance/deduction, replacing the previous grade's fill;
 * - typing a basic re-computes the percent lines the user has not overridden (the fixed ones stay as they were);
 * - a line the user changed is never touched again.
 * Returns the new values and the record of what the grade filled (pass it back as `previousFill` next time).
 */
export function applyGradeSalaryDefaults(current: SalaryAmounts, previousFill: SalaryAmounts, lines: readonly PayScaleLine[]):
  { values: SalaryAmounts; filled: SalaryAmounts } {
  const followsGrade = (field: SalaryAmountField) =>
    current[field] === undefined || (previousFill[field] !== undefined && current[field] === previousFill[field]);
  const typed: SalaryAmounts = {};
  for (const field of SALARY_AMOUNT_FIELDS) typed[field] = followsGrade(field) ? undefined : current[field];
  const merged = mergeGradeSalary(typed, lines);
  const values: SalaryAmounts = {};
  const filled: SalaryAmounts = {};
  for (const field of SALARY_AMOUNT_FIELDS) {
    values[field] = merged[field];
    if (followsGrade(field) && merged[field] !== undefined) filled[field] = merged[field];
  }
  return { values, filled };
}

/** Sum of the monthly earnings (the gross package): basic and the five allowances, never the deduction. */
export function grossPackage(values: SalaryAmounts): number {
  return roundMoney(SALARY_AMOUNT_FIELDS.filter((f) => f !== 'fixedDeduction').reduce((sum, f) => sum + Number(values[f] ?? 0), 0));
}
