import type { ReducedItem } from '../api/payroll';
import { roundMoney } from './gradeSalaryDefaults';
import { lineMonthly, viewMonthly, type AllowanceValue, type SalaryAllowanceView } from './salaryAllowances';

/**
 * Lowering an employee's pay needs their written agreement. This mirrors the server rule (backend SalaryReductions.Compare) so the
 * form can say so before the person saves; the server stays the backstop and always has the last word. No React: unit-tested directly.
 *
 * "Pay" is the six slots (basic, housing, transport, food, mobile, other) and every monthly cash allowance line, carried lines
 * included (a percentage line worked out again on the proposed basic). The fixed deduction is not pay (the server also treats a HIGHER one as a
 * reduction, named FIXED_DEDUCTION; the form learns of that from the server's refusal). A proposal is a reduction
 * when the whole monthly pay goes down OR any single slot or line goes down or disappears. A first package (nothing in force)
 * never is one. Strings returned are English i18n keys (src/i18n/releaseA/salaryReduction.ts) for t().
 */

export const PAY_SLOT_CODES = ['BASIC', 'HOUSING', 'TRANSPORT', 'FOOD', 'MOBILE', 'OTHER'] as const;
export type PaySlotCode = (typeof PAY_SLOT_CODES)[number];

export const CONSENT_REFERENCE_MAX = 200;
/** The shortest reference the server keeps (backend SalaryReductions.ReferenceMinLength): 3 characters, at least one a letter or a number. */
export const CONSENT_REFERENCE_MIN = 3;
/** The reduction item the server names when the fixed deduction goes UP (it lowers take-home pay though it is not pay). */
export const FIXED_DEDUCTION_ITEM = 'FIXED_DEDUCTION';

export interface PayLine { code: string; amount: number }
/** The pay a salary gives each month: the six slots and the allowance lines. */
export interface PayPackage { slots: Record<PaySlotCode, number>; lines: PayLine[] }

export interface ReductionCheck {
  isReduction: boolean;
  /** Every item that goes down, with what it was and what it becomes (0 when it is removed). */
  items: ReducedItem[];
  currentTotal: number;
  proposedTotal: number;
  /** Any slot or line differs from the record in force, up or down. */
  changesContractWage: boolean;
}

export const NO_REDUCTION: ReductionCheck = { isReduction: false, items: [], currentTotal: 0, proposedTotal: 0, changesContractWage: false };

const cents = (n: number) => Math.round(roundMoney(n) * 100);
const sum = (lines: readonly PayLine[]) => lines.reduce((total, l) => total + l.amount, 0);
const byCode = (lines: readonly PayLine[]) => {
  const map = new Map<string, number>();
  for (const l of lines) map.set(l.code.toUpperCase(), (map.get(l.code.toUpperCase()) ?? 0) + l.amount);
  return map;
};
const slotTotal = (p: PayPackage) => PAY_SLOT_CODES.reduce((total, code) => total + p.slots[code], 0);

/** Compares a proposal with the record in force. `current` null = nothing in force. */
export function compareReduction(current: PayPackage | null, proposed: PayPackage): ReductionCheck {
  if (!current) return NO_REDUCTION;
  const items: ReducedItem[] = [];
  let wageChanged = false;
  for (const code of PAY_SLOT_CODES) {
    const before = current.slots[code], after = proposed.slots[code];
    if (cents(after) !== cents(before)) wageChanged = true;
    if (cents(after) < cents(before)) items.push({ item: code, from: roundMoney(before), to: roundMoney(after) });
  }
  const now = byCode(current.lines), next = byCode(proposed.lines);
  for (const [code, before] of [...now.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    const after = next.get(code) ?? 0;
    if (cents(after) !== cents(before)) wageChanged = true;
    if (cents(after) < cents(before)) items.push({ item: code, from: roundMoney(before), to: roundMoney(after) });
  }
  if ([...next.keys()].some(code => !now.has(code))) wageChanged = true;
  const currentTotal = roundMoney(slotTotal(current) + sum(current.lines));
  const proposedTotal = roundMoney(slotTotal(proposed) + sum(proposed.lines));
  return { isReduction: cents(proposedTotal) < cents(currentTotal) || items.length > 0, items, currentTotal, proposedTotal, changesContractWage: wageChanged };
}

/** The slot amounts of a salary request or record, as numbers (a blank or unreadable one counts as 0). */
export interface SlotNumbers {
  basicSalary: number; housingAllowance: number; transportAllowance: number; foodAllowance: number; mobileAllowance: number; otherAllowance: number;
}
const slotsOf = (s: SlotNumbers): Record<PaySlotCode, number> => ({
  BASIC: s.basicSalary, HOUSING: s.housingAllowance, TRANSPORT: s.transportAllowance, FOOD: s.foodAllowance, MOBILE: s.mobileAllowance, OTHER: s.otherAllowance,
});

/** A salary record in force, as the salary list or the employee detail gives it. `allowances` null/absent = the lines are not known (masked or an older API). */
export interface RecordInForce extends SlotNumbers { allowances?: readonly SalaryAllowanceView[] | null }

/** The record in force from the salary list: the active one with the latest effective date on or before `effectiveDate`, then the latest created. */
export function recordInForce<T extends { isActive: boolean; effectiveDate: string; createdAtUtc?: string }>(records: readonly T[], effectiveDate: string): T | null {
  const date = effectiveDate.slice(0, 10);
  return records.filter(r => r.isActive && r.effectiveDate.slice(0, 10) <= (date || '9999-12-31'))
    .sort((a, b) => b.effectiveDate.localeCompare(a.effectiveDate) || (b.createdAtUtc ?? '').localeCompare(a.createdAtUtc ?? ''))[0] ?? null;
}

/** The lines on a record as pay lines (a percentage line's stored amount, else worked out on the record's basic). */
function recordLines(record: RecordInForce): PayLine[] {
  return (record.allowances ?? []).map(l => ({ code: l.code, amount: viewMonthly(l, record.basicSalary) ?? 0 }));
}

/** The lines a proposal that does not mention allowances carries: the record's, a percentage worked out again on the proposed basic. */
export function carriedLines(lines: readonly SalaryAllowanceView[], proposedBasic: number): PayLine[] {
  return lines.map(l => ({
    code: l.code,
    amount: l.basis === 'PercentOfBasic' && l.rate != null ? roundMoney(l.rate * proposedBasic) : Number(l.amount ?? 0),
  }));
}

/**
 * Whether a salary proposal lowers the pay, against the record in force.
 *  - `record` null/undefined: nothing in force, never a reduction;
 *  - `allowances` null: the proposal does not mention allowances, so the record's lines are carried;
 *  - a record whose lines are not known (`allowances` not an array) is compared on the six slots only: the server judges the rest.
 * A line being typed that has no amount yet keeps its current amount (no change is assumed until it is a number).
 */
export function proposalReduction(record: RecordInForce | null | undefined, proposed: SlotNumbers, allowances: AllowanceValue): ReductionCheck {
  if (!record) return NO_REDUCTION;
  const known = Array.isArray(record.allowances);
  const now = known ? recordLines(record) : [];
  const nowByCode = byCode(now);
  let after: PayLine[] = [];
  if (known) {
    after = allowances === null
      ? carriedLines(record.allowances!, proposed.basicSalary)
      : allowances.filter(l => l.code.trim()).map(l => ({ code: l.code.trim().toUpperCase(), amount: lineMonthly(l, proposed.basicSalary) ?? nowByCode.get(l.code.trim().toUpperCase()) ?? 0 }));
  }
  return compareReduction({ slots: slotsOf(record), lines: now }, { slots: slotsOf(proposed), lines: after });
}

/** The i18n key naming a pay slot, in words. */
export const SLOT_LABEL_KEYS: Record<PaySlotCode, string> = {
  BASIC: 'Basic salary', HOUSING: 'Housing allowance', TRANSPORT: 'Transport allowance', FOOD: 'Food allowance', MOBILE: 'Mobile allowance', OTHER: 'Other allowance',
};
export const isPaySlot = (item: string): item is PaySlotCode => (PAY_SLOT_CODES as readonly string[]).includes(item.toUpperCase());

/** What stops a consent reference being sent (an i18n key); null when it is fine or blank. */
export function consentReferenceProblem(reference: string): string | null {
  const text = reference.trim();
  if (text.length > CONSENT_REFERENCE_MAX) return 'Keep the agreement reference under 200 characters.';
  // Not "x" or "---": a real reference has at least 3 characters and a letter or a number (Arabic letters and digits count).
  if (text.length > 0 && (text.length < CONSENT_REFERENCE_MIN || !/[\p{L}\p{N}]/u.test(text))) return 'Enter a real reference: at least 3 characters, including a letter or a number.';
  return null;
}

/**
 * The `reductionConsentReference` to send: the trimmed reference, only when the proposal lowers the pay (or the server already asked for it).
 * `undefined` leaves the property out.
 */
export function consentReferenceForRequest(reference: string, needed: boolean): string | undefined {
  const text = reference.trim();
  return needed && text ? text : undefined;
}

/** What stops saving a reduction: no reference, or one that is too long (an i18n key); null when it can go. */
export function reductionBlock(check: Pick<ReductionCheck, 'isReduction'>, reference: string): string | null {
  if (!check.isReduction) return null;
  if (!reference.trim()) return 'Enter the employee’s written agreement reference before saving.';
  return consentReferenceProblem(reference);
}

export interface ReductionRefusal {
  /** `required`: the proposal lowers the pay and carries no reference; `tooLong` / `tooShort`: the reference cannot be kept. */
  kind: 'required' | 'tooLong' | 'tooShort';
  /** The server's sentence (English). */
  message: string;
  items: ReducedItem[];
}

const readItems = (value: unknown): ReducedItem[] => (Array.isArray(value) ? value : [])
  .filter((x): x is { item: string; from: unknown; to: unknown } => !!x && typeof (x as { item?: unknown }).item === 'string')
  .map(x => ({ item: x.item, from: Number(x.from) || 0, to: Number(x.to) || 0 }));

const LEGACY_REQUIRED_SENTENCE = /^Lowering an employee['’]s pay needs their written agreement/;

/**
 * The server's refusal of a reduction without its consent reference: 400 `{ code: "reduction_consent_required", message, reducedItems }` when a
 * proposal is made, or 409 `{ error: "salary_change_stale", code: "reduction_consent_required", reducedItems }` when Finance approves one,
 * or 422 `{ error: "reduction_consent_required", code, message, reducedItems }` from the employee update.
 */
export function reductionRefusal(err: unknown): ReductionRefusal | null {
  const data = (err as { response?: { data?: { error?: unknown; code?: unknown; message?: unknown; reducedItems?: unknown } } })?.response?.data;
  if (!data) return null;
  const codes = [data.code, data.error].filter((c): c is string => typeof c === 'string');
  const message = typeof data.message === 'string' ? data.message : '';
  if (codes.includes('reduction_consent_required')) return { kind: 'required', message, items: readItems(data.reducedItems) };
  // The employee update (PUT /api/employees/{id}) answers 422 with the same code. A server that predates that answered this refusal as a generic
  // 422 `invalid_payroll_change` (the frontend can be deployed first), so its sentence is recognised too; it carries no items.
  if (data.error === 'invalid_payroll_change' && LEGACY_REQUIRED_SENTENCE.test(message)) return { kind: 'required', message, items: [] };
  if (codes.includes('reduction_consent_reference_too_long')) return { kind: 'tooLong', message, items: [] };
  if (codes.includes('reduction_consent_reference_too_short')) return { kind: 'tooShort', message, items: [] };
  return null;
}
