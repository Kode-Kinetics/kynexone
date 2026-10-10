// ============================================================
// Payslip logic — pure, unit-tested (tests/leavePay.test.ts). No react / react-native imports.
// ============================================================

const CENT = 0.005;

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}

/**
 * The check line under net pay: Gross − Deductions = Net. Every figure is the payslip's own; the
 * app never adjusts one to make the sum work. A mismatch is shown as a mismatch.
 */
export function payslipCheck(gross: number, deductions: number, net: number): {
  expectedNet: number;
  matches: boolean;
  difference: number;
} {
  const expectedNet = round2(gross - deductions);
  const difference = round2(net - expectedNet);
  return { expectedNet, matches: Math.abs(difference) < CENT, difference };
}

/** Sum of line amounts, to the cent. */
export function sumLines(lines: { amount: number }[]): number {
  return round2(lines.reduce((s, l) => s + (Number.isFinite(l.amount) ? l.amount : 0), 0));
}

/**
 * The part of the deductions total that no line explains. Shown as a flag ("not itemised"),
 * never as an invented "Other deductions" line.
 */
export function unitemisedAmount(total: number, lines: { amount: number }[]): number {
  const gap = round2(total - sumLines(lines));
  return Math.abs(gap) < CENT ? 0 : gap;
}

/** Newer / older neighbours of a payslip in a list sorted newest first. */
export function adjacentPayslips<T extends { id: string }>(list: T[], id: string): { newer: T | null; older: T | null } {
  const i = list.findIndex((p) => p.id === id);
  if (i < 0) return { newer: null, older: null };
  return { newer: i > 0 ? list[i - 1] : null, older: i < list.length - 1 ? list[i + 1] : null };
}

/** First and last calendar day of a pay month ('YYYY-MM-DD'), for the Hijri secondary line. */
export function monthBounds(year: number, month: number): { first: string; last: string } | null {
  if (!(year > 2000) || !(month >= 1 && month <= 12)) return null;
  const pad = (n: number) => String(n).padStart(2, '0');
  const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return { first: `${year}-${pad(month)}-01`, last: `${year}-${pad(month)}-${pad(lastDay)}` };
}

/** Release A deduction categories (DeductionCategories.cs). Unknown values read as 'Other'. */
export type DeductionCategory =
  | 'Statutory'
  | 'EmployerLoan'
  | 'SalaryAdvance'
  | 'Absence'
  | 'PenaltyOrAdjustment'
  | 'CourtOrder'
  | 'Other';

const CATEGORIES: readonly DeductionCategory[] = [
  'Statutory',
  'EmployerLoan',
  'SalaryAdvance',
  'Absence',
  'PenaltyOrAdjustment',
  'CourtOrder',
  'Other',
];

export function deductionCategory(raw: string | null | undefined): DeductionCategory {
  return (CATEGORIES as readonly string[]).includes(raw ?? '') ? (raw as DeductionCategory) : 'Other';
}

/** Payslip status from the API ('Final', 'Paid', …) as a display kind. */
export function payslipStatusKind(raw: string | null | undefined): 'paid' | 'final' | 'unknown' {
  const v = (raw ?? '').toLowerCase();
  if (v === 'paid') return 'paid';
  if (v === 'final' || v === 'processed' || v === 'published' || v === 'finalized' || v === 'finalised') return 'final';
  return 'unknown';
}

/** 'YYYY-MM-01' for a pay month, so the UI formats the month in its own language. null when unknown. */
export function periodIso(year: number, month: number): string | null {
  return monthBounds(year, month)?.first ?? null;
}
