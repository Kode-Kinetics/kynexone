/**
 * Setup → Payroll rules (components/payroll/PayrollRulesPanel.tsx). Mirrors CompanyPayrollRules.Evaluate on the server:
 * a meaningless value is stopped here; a value below a statutory minimum is allowed, but needs an acknowledgement.
 * An empty field means "no choice": the platform default applies.
 */
export type PayrollRuleKey =
  | 'rest_day_work_mode' | 'rest_day_multiplier' | 'holiday_multiplier' | 'leave_cashout_on_exit'
  | 'leave_cashout_basis' | 'max_probation_days' | 'proration_basis' | 'absence_deduction_base';

export const PAYROLL_RULE_KEYS: readonly PayrollRuleKey[] = [
  'rest_day_work_mode', 'rest_day_multiplier', 'holiday_multiplier', 'leave_cashout_on_exit',
  'leave_cashout_basis', 'max_probation_days', 'proration_basis', 'absence_deduction_base',
];

export const OVERTIME_FLOOR = 1.5;
export const PROBATION_STATUTORY_DAYS = 90;
export const PROBATION_EXTENDED_DAYS = 180;
export const PROBATION_MAX_DAYS = 365;

export interface PayrollRuleView {
  key: PayrollRuleKey;
  value: string | null;
  isSet: boolean;
  warningCode: string | null;
  warning: string | null;
  acknowledged: boolean;
  acknowledgedAtUtc: string | null;
  acknowledgedBy: string | null;
  acknowledgementNote: string | null;
  /** The day the value in force started; null for a rule saved before dating existed. */
  effectiveFrom?: string | null;
  /** A later dated change already queued for this rule. */
  hasUpcoming?: boolean;
  upcomingValue?: string | null;
  upcomingFrom?: string | null;
}

export type PayrollRuleForm = Record<PayrollRuleKey, string>;
export type PayrollRuleErrors = Partial<Record<PayrollRuleKey, string>>;

export const EMPTY_RULE_FORM: PayrollRuleForm = {
  rest_day_work_mode: '', rest_day_multiplier: '', holiday_multiplier: '', leave_cashout_on_exit: '',
  leave_cashout_basis: '', max_probation_days: '', proration_basis: '', absence_deduction_base: '',
};

export function ruleFormFromViews(views: readonly PayrollRuleView[]): PayrollRuleForm {
  const form = { ...EMPTY_RULE_FORM };
  for (const view of views) if (view.isSet && view.value != null) form[view.key] = view.value;
  return form;
}

const ONE_OF: Partial<Record<PayrollRuleKey, string[]>> = {
  rest_day_work_mode: ['PaidPremium', 'CompensatoryDay'],
  leave_cashout_basis: ['Gross', 'Basic', 'BasicHousing'],
  proration_basis: ['Calendar30', 'CalendarActual'],
  absence_deduction_base: ['Basic', 'Gross'],
  leave_cashout_on_exit: ['true', 'false'],
};

export function validateRuleForm(form: PayrollRuleForm): PayrollRuleErrors {
  const errors: PayrollRuleErrors = {};
  for (const key of PAYROLL_RULE_KEYS) {
    const value = form[key].trim();
    if (value === '') continue;
    const allowed = ONE_OF[key];
    if (allowed && !allowed.includes(value)) errors[key] = 'Choose one of the options.';
    if (key === 'rest_day_multiplier' || key === 'holiday_multiplier') {
      const n = Number(value);
      if (!Number.isFinite(n) || n < 1 || n > 5) errors[key] = 'Enter a multiplier between 1 and 5.';
    }
    if (key === 'max_probation_days') {
      if (!/^\d+$/.test(value) || Number(value) > PROBATION_MAX_DAYS) errors[key] = 'Enter a whole number of days from 0 to 365.';
    }
  }
  return errors;
}

/** The warning code for a value below a statutory minimum, or null. Assumes the value passed validation. */
export function warningFor(key: PayrollRuleKey, raw: string): 'BELOW_OVERTIME_FLOOR' | 'LEAVE_CASHOUT_OFF' | 'PROBATION_OVER_90' | 'PROBATION_OVER_180' | null {
  const value = raw.trim();
  if (value === '') return null;
  if ((key === 'rest_day_multiplier' || key === 'holiday_multiplier') && Number(value) < OVERTIME_FLOOR) return 'BELOW_OVERTIME_FLOOR';
  if (key === 'leave_cashout_on_exit' && value === 'false') return 'LEAVE_CASHOUT_OFF';
  if (key === 'max_probation_days') {
    const days = Number(value);
    if (days > PROBATION_EXTENDED_DAYS) return 'PROBATION_OVER_180';
    if (days > PROBATION_STATUTORY_DAYS) return 'PROBATION_OVER_90';
  }
  return null;
}

export function warningsForForm(form: PayrollRuleForm): { key: PayrollRuleKey; code: string }[] {
  const out: { key: PayrollRuleKey; code: string }[] = [];
  for (const key of PAYROLL_RULE_KEYS) {
    const code = warningFor(key, form[key]);
    if (code) out.push({ key, code });
  }
  return out;
}

/** Only the rules that differ from what is saved; an emptied field clears the rule back to its default. */
export function ruleFormToChanges(form: PayrollRuleForm, initial: PayrollRuleForm): { key: PayrollRuleKey; value: string | null }[] {
  return PAYROLL_RULE_KEYS.filter(key => form[key].trim() !== initial[key].trim())
    .map(key => ({ key, value: form[key].trim() === '' ? null : form[key].trim() }));
}

/** The "key:CODE" strings the acknowledgement echoes, in the order the server compares them (ordinal). */
export function acknowledgedWarningCodes(form: PayrollRuleForm, initial: PayrollRuleForm): string[] {
  return changedWarnings(form, initial).map(w => `${w.key}:${w.code}`).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** The warnings raised by the rules being CHANGED now (an unchanged, previously confirmed value raises none). */
export function changedWarnings(form: PayrollRuleForm, initial: PayrollRuleForm): { key: PayrollRuleKey; code: string }[] {
  const changed = new Set(ruleFormToChanges(form, initial).map(c => c.key));
  return warningsForForm(form).filter(w => changed.has(w.key));
}

/** The server's message in the user's language when it sent both (the "no eligible approver" refusal). */
export function bilingualReason(error: unknown, locale: string, fallback: string): string {
  const data = (error as { response?: { data?: { message?: unknown; messageAr?: unknown } } })?.response?.data;
  const pick = locale === 'ar' ? data?.messageAr : data?.message;
  return typeof pick === 'string' && pick.trim() ? pick.trim() : fallback;
}

/** Today as yyyy-MM-dd (UTC, the server's clock for "not in the past"). */
export function todayIso(now: Date = new Date()): string {
  return now.toISOString().slice(0, 10);
}

/** True when `from` is a valid yyyy-MM-dd that is not before today. */
export function effectiveFromOk(from: string, now: Date = new Date()): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(from) && from >= todayIso(now);
}

/** One-time notice (M1): untaken annual leave is now paid out on exit by default. Remembered per browser. */
export const CASHOUT_NOTICE_KEY = 'zayra.payrollRules.cashOutDefaultNoticeSeen';
