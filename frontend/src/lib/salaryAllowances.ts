import type { EntitlementMatrix, MatrixComponent } from '../api/entitlements';
import type { EmployeeSalaryStructure } from '../api/payroll';
import { isCashAllowance } from './cashAllowance';
import { percentFromRate } from './entitlementMatrix';
import { roundMoney } from './gradeSalaryDefaults';
import { normalizeDigits } from './hire/saudiId';

/**
 * Monthly cash allowances (ALW_*) on a salary: the pure rules behind the "Other monthly allowances" section, the Finance
 * approval comparison and the profile list. No React, so they are unit-tested directly. Strings returned are English i18n
 * keys (src/i18n/releaseA/salaryAllowances.ts) for t().
 *
 * The wire contract (backend SalaryAllowanceLine): `allowances` on a salary request is
 *   omitted / null  the request does not mention allowances: the server carries the current lines forward (a percentage is
 *                   worked out again on the new basic), or fills a first package from the grade;
 *   []              remove every allowance;
 *   [lines]         exactly these lines.
 * A line is { code, basis: "Amount" | "PercentOfBasic", rate?, amount? }: `rate` is a fraction (0.10 = 10%), and for a
 * percentage the server computes the amount. So the section is "untouched" (null: send nothing) until the person changes it.
 */

export type AllowanceBasis = 'Amount' | 'PercentOfBasic';

/** A line as the API accepts it. */
export interface SalaryAllowanceWire { code: string; basis: AllowanceBasis; rate?: number | null; amount?: number | null }

/** A line as the API returns it on a salary change (names resolved by the server, removed definitions included). */
export interface SalaryAllowanceView extends SalaryAllowanceWire {
  nameEn?: string | null; nameAr?: string | null;
  /** Where the allowance posts in the payroll journal (the GL driver key and its label), and whether it counts in end of service pay. Optional: older servers omit them. */
  glDriverKey?: string | null; glDriverLabel?: string | null; eosbIncluded?: boolean | null; eosbExclusionReason?: string | null;
}

/**
 * One row of the section while it is edited. `value` is the amount, or the percentage (10 = 10%), as typed.
 * `nameEn` / `nameAr` are the names the server sent with the line (for reading only, never sent back): they let a reader who may not
 * read the benefits catalogue still see a name. They are dropped when the line is switched to another allowance.
 */
export interface AllowanceDraftLine { key: string; code: string; basis: AllowanceBasis; value: string; nameEn?: string; nameAr?: string }

/** The edited section: null = untouched (send nothing), otherwise exactly the lines shown. */
export type AllowanceValue = AllowanceDraftLine[] | null;

let sequence = 0;
export const newLineKey = () => `allowance-line-${++sequence}`;

export const blankLine = (): AllowanceDraftLine => ({ key: newLineKey(), code: '', basis: 'Amount', value: '' });

/** A number as typed ("1,250.50", Arabic-Indic digits); undefined when blank or not a number. */
export function parseDecimal(raw: string): number | undefined {
  const cleaned = normalizeDigits(raw ?? '').replace(/,/g, '');
  if (cleaned === '') return undefined;
  if (!/^\d+(\.\d+)?$/.test(cleaned)) return undefined;
  const n = Number(cleaned);
  return Number.isFinite(n) ? n : undefined;
}

const decimals = (n: number) => { const s = String(n); const i = s.indexOf('.'); return i < 0 ? 0 : s.length - i - 1; };

/** The fraction the server takes for a typed percentage (10 → 0.1), to 4 decimals. */
export const rateFromPercent = (percent: number) => Math.round(percent * 100) / 10000;

/** The lines on a salary record, as editable rows. Keys follow the code, so the same record always gives the same keys. */
export function linesFromWire(lines: readonly SalaryAllowanceView[] | null | undefined): AllowanceDraftLine[] {
  return (lines ?? []).map((l, i) => {
    const code = (l.code ?? '').toUpperCase();
    const key = `record-line-${code || i}`;
    const names = { ...(l.nameEn?.trim() ? { nameEn: l.nameEn.trim() } : {}), ...(l.nameAr?.trim() ? { nameAr: l.nameAr.trim() } : {}) };
    return l.basis === 'PercentOfBasic'
      ? { key, code, basis: 'PercentOfBasic' as const, value: percentFromRate(l.rate), ...names }
      : { key, code, basis: 'Amount' as const, value: l.amount == null ? '' : String(l.amount), ...names };
  });
}

/**
 * The lines on the salary record in force for an employee, from GET /api/payroll/employee-salary-structures (whatever order the list
 * comes in): the active record with the latest effective date, then the latest created, as the server reads "the record in force".
 *  - `[]`   the employee has no active salary record (nothing to carry forward);
 *  - `null` the lines are not known: the list masks them (`allowances: null`), or the server predates the field.
 * A caller treats `null` as "left as they are".
 */
export function currentRecordLines(records: readonly Pick<EmployeeSalaryStructure, 'isActive' | 'effectiveDate' | 'createdAtUtc' | 'allowances'>[]): AllowanceDraftLine[] | null {
  const latest = records.filter(r => r.isActive)
    .sort((a, b) => (b.effectiveDate ?? '').localeCompare(a.effectiveDate ?? '') || (b.createdAtUtc ?? '').localeCompare(a.createdAtUtc ?? ''))[0];
  if (!latest) return [];
  return Array.isArray(latest.allowances) ? linesFromWire(latest.allowances) : null;
}

/**
 * Whether lines the person has entered cannot be saved yet because there is no basic salary to work them against. Lines the server
 * would fill in from the grade are not "entered": only an edited section (`value` is a list with a line in it) counts.
 */
export function allowancesNeedBasic(value: AllowanceValue, basic: number | undefined): boolean {
  return value !== null && value.length > 0 && !(basic !== undefined && basic > 0);
}

export const BASIC_FIRST_KEY = 'Enter the basic salary before adding monthly allowances.';

/** The monthly amount of a line: a fixed amount as typed, a percentage of `basic` to the halala (half away from zero, as the server does). */
export function lineMonthly(line: Pick<AllowanceDraftLine, 'basis' | 'value'>, basic: number | undefined): number | undefined {
  const typed = parseDecimal(line.value);
  if (typed === undefined) return undefined;
  if (line.basis === 'Amount') return typed;
  if (basic === undefined || basic <= 0) return undefined;
  return roundMoney(rateFromPercent(typed) * basic);
}

/** What a salary pays in these allowances each month: lines that are not valid yet count for nothing. */
export function totalMonthly(lines: readonly AllowanceDraftLine[], basic: number | undefined): number {
  return roundMoney(lines.reduce((sum, l) => sum + (lineMonthly(l, basic) ?? 0), 0));
}

/** What stops a line being sent, by line key (an i18n key each). Mirrors the server's rules, which stay the backstop. */
export function allowanceProblems(lines: readonly AllowanceDraftLine[], basic: number | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  const seen = new Set<string>();
  for (const l of lines) {
    const code = l.code.trim().toUpperCase();
    if (!code) { out[l.key] = 'Choose the allowance for this line.'; continue; }
    if (seen.has(code)) { out[l.key] = 'This allowance is on the salary twice. Keep one line for it.'; continue; }
    seen.add(code);
    const typed = parseDecimal(l.value);
    if (l.basis === 'Amount') {
      if (typed === undefined || typed <= 0 || decimals(typed) > 2) out[l.key] = 'Enter a monthly amount above zero, with at most two decimal places.';
    } else if (typed === undefined || typed <= 0 || typed > 100 || decimals(typed) > 2) {
      out[l.key] = 'Enter a percentage above 0 and up to 100, with at most two decimal places.';
    } else if (basic !== undefined && basic > 0 && (lineMonthly(l, basic) ?? 0) <= 0) {
      out[l.key] = 'This percentage of the basic salary comes to nothing. Use a fixed amount instead.';
    }
  }
  return out;
}

/** The request lines for the rows shown. Call only when `allowanceProblems` is empty. */
export function requestLines(lines: readonly AllowanceDraftLine[]): SalaryAllowanceWire[] {
  return lines.map(l => {
    const typed = parseDecimal(l.value) ?? 0;
    return l.basis === 'PercentOfBasic'
      ? { code: l.code.trim().toUpperCase(), basis: 'PercentOfBasic' as const, rate: rateFromPercent(typed) }
      : { code: l.code.trim().toUpperCase(), basis: 'Amount' as const, amount: typed };
  });
}

/**
 * The `allowances` property of a salary request. Untouched is `undefined` (the property is left out, so the server carries the
 * current lines forward or fills a first package from the grade), never `[]`, which would remove every allowance.
 */
export function allowancesForRequest(value: AllowanceValue): SalaryAllowanceWire[] | undefined {
  return value === null ? undefined : requestLines(value);
}

const same = (a: AllowanceDraftLine, b: AllowanceDraftLine) =>
  a.code.trim().toUpperCase() === b.code.trim().toUpperCase() && a.basis === b.basis
  && (parseDecimal(a.value) ?? a.value.trim()) === (parseDecimal(b.value) ?? b.value.trim());

/** Whether two sets of rows say the same thing (order aside). */
export function sameLines(a: readonly AllowanceDraftLine[], b: readonly AllowanceDraftLine[]): boolean {
  if (a.length !== b.length) return false;
  const sorted = (xs: readonly AllowanceDraftLine[]) => [...xs].sort((x, y) => x.code.localeCompare(y.code));
  const sa = sorted(a), sb = sorted(b);
  return sa.every((l, i) => same(l, sb[i]));
}

/** The section's new value after an edit: back to untouched when the rows are what the baseline already says. */
export const valueAfterEdit = (baseline: readonly AllowanceDraftLine[], lines: AllowanceDraftLine[]): AllowanceValue =>
  sameLines(lines, baseline) ? null : lines;

/** The rows to show: the person's own once they have edited, otherwise the baseline (the record's lines, or the grade's). */
export const shownLines = (baseline: readonly AllowanceDraftLine[], value: AllowanceValue): readonly AllowanceDraftLine[] => value ?? baseline;

/** The edit form keeps every field as text: '' = untouched, otherwise the rows as JSON. */
export const encodeValue = (value: AllowanceValue): string => (value === null ? '' : JSON.stringify(value));
export function decodeValue(raw: string | undefined): AllowanceValue {
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return null;
    return parsed.map(p => {
      const o = p as Partial<AllowanceDraftLine>;
      const names = { ...(typeof o.nameEn === 'string' && o.nameEn ? { nameEn: o.nameEn } : {}), ...(typeof o.nameAr === 'string' && o.nameAr ? { nameAr: o.nameAr } : {}) };
      return { key: o.key || newLineKey(), code: String(o.code ?? ''), basis: o.basis === 'PercentOfBasic' ? 'PercentOfBasic' as const : 'Amount' as const, value: String(o.value ?? ''), ...names };
    });
  } catch { return null; }
}

// ── Definitions and grade values (the benefits matrix) ──────────────────────────────────────────────────────────────────

export interface AllowanceChoice { code: string; nameEn: string; nameAr: string }

/** The monthly cash allowances the customer has defined, from the benefits matrix, by name. */
export function allowanceChoices(components: readonly Pick<MatrixComponent, 'code' | 'nameEn' | 'nameAr' | 'isCustom'>[], locale: string): AllowanceChoice[] {
  return components.filter(isCashAllowance)
    .map(c => ({ code: c.code.toUpperCase(), nameEn: c.nameEn, nameAr: c.nameAr }))
    .sort((a, b) => nameOf(a, locale).localeCompare(nameOf(b, locale)) || a.code.localeCompare(b.code));
}

/** A name in the reader's language, falling back to the other language, then the code. */
export function nameOf(x: { code: string; nameEn?: string | null; nameAr?: string | null }, locale: string): string {
  const ar = x.nameAr?.trim(), en = x.nameEn?.trim();
  return (locale.startsWith('ar') ? ar || en : en || ar) || x.code;
}

/**
 * The name to show for a line: the names the server sent with it, then the benefits catalogue (per language), then the code.
 * A reader without access to the catalogue (`choices` empty) still sees the server's names.
 */
export function lineName(line: { code: string; nameEn?: string | null; nameAr?: string | null }, choices: readonly AllowanceChoice[], locale: string): string {
  const hit = choices.find(c => c.code === line.code.toUpperCase());
  return nameOf({ code: line.code, nameEn: line.nameEn?.trim() || hit?.nameEn, nameAr: line.nameAr?.trim() || hit?.nameAr }, locale);
}

/**
 * The allowance lines a grade gives, as the server fills them into a first package that does not state any
 * (EntitlementMatrixService.CashAllowanceLinesAsync): a cash allowance the grade offers as a fixed amount or a percentage of basic.
 * `matrix` is the group or company view in force on the effective date.
 */
export function gradeAllowanceLines(matrix: Pick<EntitlementMatrix, 'components' | 'cells'>, gradeId: string): AllowanceDraftLine[] {
  const live = new Set(matrix.components.filter(isCashAllowance).map(c => c.code));
  return matrix.cells
    .filter(c => c.gradeId === gradeId && live.has(c.componentCode) && c.eligible)
    .flatMap((c): SalaryAllowanceWire[] => {
      if (c.valueType === 'PercentOfBasic' && c.rate != null) return [{ code: c.componentCode, basis: 'PercentOfBasic' as const, rate: c.rate }];
      if (c.valueType === 'Amount' && c.amount != null && c.amount > 0) return [{ code: c.componentCode, basis: 'Amount' as const, amount: c.amount }];
      return [];
    })
    .sort((a, b) => (a.code < b.code ? -1 : a.code > b.code ? 1 : 0))
    .map(l => linesFromWire([l])[0]);
}

// ── Comparison for the Finance approver ─────────────────────────────────────────────────────────────────────────────────

export type AllowanceChange = 'added' | 'removed' | 'changed' | 'unchanged';

export interface AllowanceComparisonRow {
  code: string;
  current: SalaryAllowanceView | null;
  proposed: SalaryAllowanceView | null;
  change: AllowanceChange;
}

const view = (x: SalaryAllowanceView | undefined): SalaryAllowanceView | null => x ?? null;
const differs = (a: SalaryAllowanceView, b: SalaryAllowanceView) =>
  a.basis !== b.basis || (a.rate ?? null) !== (b.rate ?? null) || Number(a.amount ?? 0) !== Number(b.amount ?? 0);

/** Current against proposed, one row per allowance, by code. A percentage whose amount moves with a new basic reads as changed. */
export function compareAllowances(current: readonly SalaryAllowanceView[] | null | undefined, proposed: readonly SalaryAllowanceView[] | null | undefined): AllowanceComparisonRow[] {
  const cur = new Map((current ?? []).map(l => [l.code.toUpperCase(), l]));
  const pro = new Map((proposed ?? []).map(l => [l.code.toUpperCase(), l]));
  return [...new Set([...cur.keys(), ...pro.keys()])].sort().map(code => {
    const c = view(cur.get(code)), p = view(pro.get(code));
    const change: AllowanceChange = !c ? 'added' : !p ? 'removed' : differs(c, p) ? 'changed' : 'unchanged';
    return { code, current: c, proposed: p, change };
  });
}

/** The monthly amount of a line on a record: a percentage's stored amount, else worked out from the rate on `basic`. */
export function viewMonthly(line: SalaryAllowanceView, basic?: number): number | undefined {
  if (line.amount != null) return Number(line.amount);
  if (line.basis === 'PercentOfBasic' && line.rate != null && basic !== undefined) return roundMoney(line.rate * basic);
  return undefined;
}

/** The percentage a line's rate means, for display (0.125 → 12.5). */
export const viewPercent = (line: Pick<SalaryAllowanceView, 'rate'>): number => Math.round((line.rate ?? 0) * 10000) / 100;

/** The server's reasons for refusing an allowance on a salary, and where they belong. */
export const ALLOWANCE_ERROR_CODES = ['salary_allowance_invalid', 'salary_allowance_unknown', 'salary_change_stale'] as const;
export function allowanceRefusal(err: unknown): { code: string; message: string } | null {
  const data = (err as { response?: { data?: { error?: unknown; message?: unknown } } })?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : '';
  if (!(ALLOWANCE_ERROR_CODES as readonly string[]).includes(code)) return null;
  return { code, message: typeof data?.message === 'string' ? data.message : '' };
}

/** The lines in one sentence for a review: "Meal: 300.00 a month; Car: 10% of basic = 500.00 a month", or `none` for no lines. */
export function describeLines(
  lines: readonly AllowanceDraftLine[], basic: number | undefined,
  words: { none: string; name: (line: AllowanceDraftLine) => string; money: (n: number) => string; amount: (amount: string) => string; percent: (rate: string, amount: string) => string },
): string {
  if (lines.length === 0) return words.none;
  return lines.map(l => {
    const monthly = lineMonthly(l, basic);
    const amount = monthly === undefined ? '\u2014' : words.money(monthly);
    return `${words.name(l)}: ${l.basis === 'PercentOfBasic' ? words.percent(l.value, amount) : words.amount(amount)}`;
  }).join('; ');
}
