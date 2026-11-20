// Benefit utilisation (request → Finance decision, the loan flow): pure helpers shared by the HR/Finance screen, the request dialog
// and "My benefits".
// Every sentence is an i18n key in src/i18n/releaseA/benefitUsage.ts; values go into {placeholders} after translation.
// Pinned by unit/benefitUsage.spec.ts.

import type { UsageMeasure, UsageStatus, UtilisationRow } from '../api/benefitUsage';
import { money } from '../components/entitlements/packageFormat';
import { createFormatter } from './format';

export type Translate = (key: string) => string;

export function fill(template: string, values: Record<string, string | number>): string {
  return Object.entries(values).reduce((acc, [k, v]) => acc.split(`{${k}}`).join(String(v)), template);
}

/** A fresh id for a new request: the server treats a retry with the same id as the same record. */
export function newUsageId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/** The status as a label key, and its badge tone. Words always, never colour alone. */
export const STATUS_KEYS: Record<UsageStatus, string> = {
  Pending: 'Waiting for Finance',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Cancelled: 'Withdrawn',
};
export const STATUS_TONES: Record<UsageStatus, 'neutral' | 'success' | 'warning' | 'danger'> = {
  Pending: 'warning', Approved: 'success', Rejected: 'danger', Cancelled: 'neutral',
};
export const statusKey = (status: string): string => STATUS_KEYS[status as UsageStatus] ?? status;

/** The `i` texts of the dialog's traveller / child field (translation keys). */
export const INFO_KEYS = {
  child: 'Each child has the per-child amount a year. Children beyond the grade’s child limit cannot be named until a named child’s request is withdrawn.',
  traveller: 'Each traveller has their own tickets a period. Family members beyond the grade’s limit cannot be named this period.',
  evidence: 'The invoice, receipt or booking number you checked. Finance sees it when deciding.',
  /** Shown under the page title: approving a use is a record, never a payment. */
  paysNothing: 'Approving a use only records it against the entitlement and pays nothing. Cash benefits are paid through payroll; loans and bonuses have their own payment batches.',
};

/** The server's refusal codes, in words the person can act on. */
export const REFUSAL_KEYS: Record<string, string> = {
  usage_not_allowed: 'You do not have the authority for this step.',
  usage_not_found: 'This record no longer exists. Refresh the page.',
  usage_wrong_company: 'This employee or record is outside your companies.',
  usage_no_entitlement: 'The employee has no fixed entitlement to this benefit on that date.',
  usage_not_consumable: 'This benefit is not used up by a count or an amount.',
  usage_entitlement_unverified: 'HR must confirm this benefit against the signed contract before it can be requested.',
  usage_period_closed: 'That benefit period has ended. Choose a date in an open period.',
  usage_over_entitlement: 'That is more than is left for this period.',
  usage_wrong_measure: 'Request a number for a counted benefit and an amount for money, more than zero.',
  usage_currency_mismatch: 'Request the amount in the company currency.',
  usage_evidence_required: 'Give the invoice, receipt or booking reference you checked.',
  usage_reason_required: 'Say why.',
  usage_maker_cannot_approve: 'You requested this, so another person must decide it.',
  usage_already_decided: 'This request was already decided. Refresh the page.',
  usage_id_conflict: 'This form was already sent with different details. Close it and start again.',
  usage_dependant_required: 'Choose the child this claim is for.',
  usage_dependant_invalid: 'Choose a family member this benefit covers.',
  usage_dependant_cap: 'The grade’s limit of dependants for this period is reached. Use a child or traveller already named.',
  usage_academic_year_required: 'Give the academic year the fees are for.',
  usage_text_too_long: 'One of the texts is too long.',
  subject_cannot_decide: 'This is your own benefit, so another person must act on it.',
};

export function refusalKey(error: unknown): string {
  const code = (error as { response?: { data?: { error?: string } } })?.response?.data?.error;
  return (code && REFUSAL_KEYS[code]) || 'The change could not be saved. Try again.';
}

/** Which benefit-specific fields the dialog asks for. */
export function usageKind(benefitCode: string): 'ticket' | 'education' | 'other' {
  const code = benefitCode.toUpperCase();
  return code === 'AIR_TICKET' ? 'ticket' : code === 'EDUCATION' ? 'education' : 'other';
}

export interface UsageForm {
  benefitCode: string;
  usageDate: string;
  quantity: string;
  dependantId: string;
  academicYear: string;
  providerName: string;
  evidenceReference: string;
  note: string;
}

export type UsageField = 'benefitCode' | 'usageDate' | 'quantity' | 'dependantId' | 'academicYear' | 'evidenceReference';
export type UsageProblems = Partial<Record<UsageField, string>>;

export const emptyUsageForm = (today: string): UsageForm => ({
  benefitCode: '', usageDate: today, quantity: '', dependantId: '', academicYear: '', providerName: '', evidenceReference: '', note: '',
});

/** What is left for the person or child chosen (the whole row when the benefit is the employee's alone). */
export function leftFor(row: UtilisationRow, dependantId: string): number {
  if (row.beneficiaries.length === 0) return row.remaining;
  const who = row.beneficiaries.find((b) => (b.dependantId ?? '') === dependantId);
  return who ? who.remaining : 0;
}

/** Field problems as i18n keys, checked before sending; the server re-checks everything. */
export function usageProblems(form: UsageForm, row: UtilisationRow | undefined): UsageProblems {
  const problems: UsageProblems = {};
  if (!row) { problems.benefitCode = 'Choose a benefit.'; return problems; }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(form.usageDate)) problems.usageDate = 'Choose a date.';
  else if (form.usageDate < row.periodStart || form.usageDate > row.periodEnd) problems.usageDate = 'Choose a date inside this benefit period.';
  const value = Number(form.quantity);
  const kind = usageKind(row.benefitCode);
  if (!form.quantity.trim() || !Number.isFinite(value) || value <= 0) problems.quantity = row.measure === 'Units' ? 'Enter how many, more than zero.' : 'Enter an amount more than zero.';
  else if (row.measure === 'Units' && !Number.isInteger(value)) problems.quantity = 'Enter a whole number.';
  else if (row.measure === 'Amount' && Math.round(value * 100) !== value * 100) problems.quantity = 'Use at most two decimals.';
  else if (value > leftFor(row, form.dependantId)) problems.quantity = 'That is more than is left for this period.';
  if (kind === 'education' && !form.dependantId) problems.dependantId = 'Choose the child this claim is for.';
  if (kind === 'education' && !form.academicYear.trim()) problems.academicYear = 'Give the academic year the fees are for.';
  if (!form.evidenceReference.trim()) problems.evidenceReference = 'Give the invoice, receipt or booking reference you checked.';
  return problems;
}

/** A count or an amount in the viewer's language, with the app's own formatter (the same as My package). */
export function measureText(value: number, measure: UsageMeasure, currency: string | null, locale: string): string {
  if (measure === 'Amount') return money(value, { locale, currency: currency || 'SAR' });
  return createFormatter({ locale, timeZone: 'UTC', calendarSystem: 'Gregorian', currency: null }).number(value, { maximumFractionDigits: 0 });
}

export { date as calendarDate } from '../components/entitlements/packageFormat';
