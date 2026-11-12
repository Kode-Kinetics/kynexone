import type { EntitlementMatrix } from '../../api/entitlements';
import { roundMoney } from '../gradeSalaryDefaults';
import type { HireForm } from './hireForm';
import { parseAmount } from './payload';

/**
 * What the wizard shows, totals and sends for the monthly pay, so the saved salary is what the person saw and confirmed.
 *
 * For a Benefits-by-grade tenant the server fills a cash allowance left BLANK (housing, transport, other) from the grade's cells in force
 * on the start date (EmployeeManagementService.PrefillSalaryFromMatrixAsync / GradeCashAllowance.MonthlyCash). This module reads the same
 * cells, so the amount is on screen, inside the total, and goes out in the request instead of being added after the confirmation.
 * It mirrors the server field by field: a typed figure (0 included) is the person's own; only a blank is filled; only a salary that has
 * figures is filled at all; a percentage is the rate times the basic entered, to the halala, half away from zero.
 */
export type GradeAllowanceField = 'housing' | 'transport' | 'other';
export type PackageField = 'basic' | GradeAllowanceField;
/** The amount the grade adds to each allowance the person left blank. Only a cash amount above zero is here. */
export type GradeFill = Partial<Record<GradeAllowanceField, number>>;

export interface PackageRow { field: PackageField; amount: number; fromGrade: boolean }
export interface HirePackage {
  rows: PackageRow[];
  /** Basic and the three allowances, the grade's included: what the server compares with the grade's band. */
  total: number;
  /** True when at least one amount comes from the grade rather than from what was typed. */
  gradeFilled: boolean;
}

const CODE_FIELD: Record<string, GradeAllowanceField> = { HOUSING: 'housing', TRANSPORT: 'transport', OTHER_ALLOWANCES: 'other' };
const ALLOWANCES: readonly GradeAllowanceField[] = ['housing', 'transport', 'other'];

/** The cash the grade's matrix gives each blank allowance on `basic`. In kind, not offered, no value, or a percentage with no basic add nothing. */
export function gradeFillFromMatrix(matrix: Pick<EntitlementMatrix, 'cells'>, gradeId: string, basic: number | undefined): GradeFill {
  const fill: GradeFill = {};
  for (const cell of matrix.cells) {
    const field = CODE_FIELD[(cell.componentCode ?? '').toUpperCase()];
    if (!field || cell.gradeId !== gradeId || fill[field] !== undefined || !cell.eligible) continue;
    let cash: number | undefined;
    if (cell.valueType === 'Amount') cash = cell.amount ?? undefined;
    else if (cell.valueType === 'PercentOfBasic' && cell.rate != null && basic !== undefined && basic > 0) cash = roundMoney(basic * cell.rate);
    if (cash !== undefined && cash > 0) fill[field] = cash;
  }
  return fill;
}

type PackageForm = Pick<HireForm, 'basic' | 'housing' | 'transport' | 'other'>;

/** The monthly package as it will be saved: typed figures, then the grade's amount for each blank allowance. */
export function hirePackage(form: PackageForm, fill: GradeFill): HirePackage {
  const typed: Record<PackageField, number | undefined> = {
    basic: parseAmount(form.basic), housing: parseAmount(form.housing), transport: parseAmount(form.transport), other: parseAmount(form.other),
  };
  const typedTotal = (['basic', ...ALLOWANCES] as PackageField[]).reduce((sum, f) => sum + (typed[f] ?? 0), 0);
  // The server fills only a salary that already has figures; no figures is the ordinary empty salary section.
  if (typedTotal <= 0) return { rows: [], total: 0, gradeFilled: false };
  const rows: PackageRow[] = [];
  if (typed.basic !== undefined && typed.basic > 0) rows.push({ field: 'basic', amount: typed.basic, fromGrade: false });
  for (const field of ALLOWANCES) {
    const own = typed[field];
    if (own !== undefined) { if (own > 0) rows.push({ field, amount: own, fromGrade: false }); continue; }
    const given = fill[field];
    if (given !== undefined && given > 0) rows.push({ field, amount: given, fromGrade: true });
  }
  return { rows, total: roundMoney(rows.reduce((sum, r) => sum + r.amount, 0)), gradeFilled: rows.some(r => r.fromGrade) };
}

/** Where the total pay sits against the grade's band: the server's rule (total pay, not the basic alone). No band, or no pay, is never out of it. */
export function bandStatus(total: number, grade: { minSalary: number; maxSalary: number } | undefined): 'below' | 'above' | null {
  if (!grade || total <= 0) return null;
  if (grade.minSalary > 0 && total < grade.minSalary) return 'below';
  if (grade.maxSalary > 0 && total > grade.maxSalary) return 'above';
  return null;
}
