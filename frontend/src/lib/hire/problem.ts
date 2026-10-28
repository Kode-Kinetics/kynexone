import type { DuplicateMatch } from '../../api/employees';
import type { HireForm } from './hireForm';
import { FIELD_LIMITS, TOO_LONG_KEY } from './hireValidation';

export interface ServerWarning { code: string; field?: string; message?: string; requiresPermission?: boolean }

export type HireProblem =
  | { kind: 'validation'; fields: Record<string, string[]> }
  | { kind: 'warnings'; warnings: ServerWarning[] }
  | { kind: 'duplicate'; matches: Array<DuplicateMatch & { joinedYear?: number }> }
  | { kind: 'rate_limited'; retryAfterSeconds: number }
  | { kind: 'draft_conflict' }
  | { kind: 'idempotency_mismatch' }
  | { kind: 'override_reason_required' }
  | { kind: 'override_not_permitted' }
  | { kind: 'invalid_reference'; field: string }
  | { kind: 'network' }
  | { kind: 'other'; status?: number; code?: string };

interface ErrLike { response?: { status?: number; data?: Record<string, unknown>; headers?: Record<string, unknown> } }

/** Reads the error contract of 40-ARCHITECTURE 3.1. The UI renders from `code`, never from the English `detail`. */
export function parseHireProblem(err: unknown): HireProblem {
  const r = (err as ErrLike)?.response;
  if (!r) return { kind: 'network' };
  const data = (r.data ?? {}) as Record<string, unknown>;
  const code = typeof data.code === 'string' ? data.code : typeof data.error === 'string' ? data.error : undefined;
  if (r.status === 429) {
    const raw = r.headers?.['retry-after'] ?? r.headers?.['Retry-After'];
    return { kind: 'rate_limited', retryAfterSeconds: Math.max(1, Number(raw) || 30) };
  }
  if (code === 'possible_duplicate') return { kind: 'duplicate', matches: (data.matches as never[]) ?? [] };
  if (code === 'warnings_require_acknowledgement') return { kind: 'warnings', warnings: (data.warnings as ServerWarning[]) ?? [] };
  if (code === 'draft_changed_elsewhere') return { kind: 'draft_conflict' };
  if (code === 'idempotency_key_reuse_mismatch') return { kind: 'idempotency_mismatch' };
  if (code === 'override_reason_required') return { kind: 'override_reason_required' };
  if (code === 'override_not_permitted') return { kind: 'override_not_permitted' };
  if (code === 'invalid_reference') return { kind: 'invalid_reference', field: String(data.field ?? '') };
  if (r.status === 400 && code === 'validation_failed' && data.errors && typeof data.errors === 'object') {
    return { kind: 'validation', fields: data.errors as Record<string, string[]> };
  }
  return { kind: 'other', status: r.status, code };
}

/** Server code -> i18n key. Every code the wizard can receive has an EN and an AR sentence (hireWizard.ts). */
export const SERVER_CODE_KEYS: Record<string, string> = {
  required: 'This field is required.',
  value_too_long: TOO_LONG_KEY,
  invalid_format: 'This value is not in a valid format.',
  id_format_invalid: 'This ID number is not in a valid format.',
  id_checksum_invalid: 'This ID number is not valid. Check the digits.',
  id_prefix_nationality_mismatch: 'This ID number does not match the nationality.',
  nationality_unknown: 'Choose a nationality from the list.',
  dob_future: 'Date of birth cannot be in the future.',
  dob_out_of_range: 'Date of birth cannot be more than 100 years ago.',
  joining_out_of_range: 'The start date is outside the allowed range.',
  probation_order: 'The probation end date must be after the probation start date.',
  negative_amount: 'Amount cannot be negative.',
  amount_overflow: 'Enter an amount of 9,999,999,999.99 or less.',
  iban_invalid: 'This IBAN is not valid. Check the digits and length.',
  iban_checksum_invalid: 'This IBAN is not valid. Check the digits.',
  phone_invalid: 'Enter a valid mobile number.',
  email_invalid: 'Enter a valid email address.',
  photo_url_scheme: 'The photo link must start with https://.',
  list_too_long: 'Too many items were sent.',
  contract_end_before_start: 'The contract end date must be after the start date.',
  contract_end_without_fixed_term: 'A contract end date needs a fixed-term contract type.',
  invalid_reference: 'This selection is not available. Choose another.',
  invalid_company: 'Choose a company you can hire into.',
  override_reason_required: 'Enter a reason of at least 10 characters.',
  override_not_permitted: 'You do not have permission to override this. Ask an HR Manager.',
  idempotency_key_reuse_mismatch: 'This request was already used for different details. Reload and try again.',
  company_country_missing: 'The employing company has no country. Set it in Setup, then try again.',
  work_email_conflict: 'That work email is already in use.',
  cost_centre_reason_required: 'Enter the reason for the cost centre change.',
  possible_duplicate: 'This may be a person who is already on file.',
  warnings_require_acknowledgement: 'Some details need your confirmation before saving.',
  rate_limited: 'Too many checks. Try again in a moment.',
  draft_changed_elsewhere: 'This draft was changed somewhere else. Reload to continue.',
  draft_basis_required: 'This draft needs to be reloaded before it can be saved.',
  dob_under_15: 'The employee is under the minimum working age of 15.',
  under_minimum_age: 'The employee will be under the minimum working age of 15 on the start date.',
  juvenile_15_17: 'The employee is under 18. Juvenile employment rules apply.',
  age_over_70: 'The employee is over 70.',
  joining_backdated_over_30d: 'The start date is more than 30 days in the past.',
  joining_backdated: 'The start date is more than 30 days in the past.',
  joining_over_180d_future: 'The start date is more than 180 days away.',
  probation_over_180: 'The probation period is longer than 180 days.',
  probation_over_limit: 'The probation period is longer than 180 days.',
  id_expired: 'The identity document has expired.',
  id_expiring_90d: 'The identity document expires within 90 days.',
  salary_below_wage_floor: 'The salary is below the wage floor.',
  basic_below_50pct: 'Basic salary is under 50% of the total package.',
  grade_band_out_of_range: 'The basic salary is outside the pay band of the grade.',
  grade_differs_from_designation: 'This grade is not the grade of the chosen designation.',
  mobile_foreign_for_company: 'The mobile number is from a different country than the company.',
  duplicate_probable: 'This name matches someone already on file.',
};

/**
 * The sentence for a server field error. `value_too_long` names the field and its own limit (CD26); every other code
 * keeps its fixed sentence. A field with no known limit gets a limit-free sentence, never a borrowed "150".
 */
export function serverFieldIssue(field: keyof HireForm, code: string): { key: string; params?: Record<string, string> } {
  if (code === 'value_too_long') {
    const lim = FIELD_LIMITS[field];
    if (field === 'englishName') return { key: 'Use 150 characters or fewer.' };
    if (lim) return { key: TOO_LONG_KEY, params: { limit: String(lim.limit), field: lim.label } };
    return { key: 'This value is too long. Shorten it.' };
  }
  return { key: SERVER_CODE_KEYS[code] ?? 'This value is not in a valid format.' };
}
