import type {
  AllowanceWarning, BenefitKind, CashAllowanceInfo, CashGlDriver, ConfirmAllowanceBody, CorrectAllowanceBody, CreateBenefitBody, EosbExclusionCategory,
  MatrixComponent, StaleRun,
} from '../api/entitlements';

/**
 * Adding a benefit in Benefits by grade: a contract benefit (recorded on the package, not paid) or a monthly cash
 * allowance paid in the salary. The pure rules behind the form: what it needs, what it sends, which GL accounts it may
 * post to and where a server refusal belongs. No React, so it is unit-tested directly. Strings returned are English
 * i18n keys (src/i18n/releaseA/matrix.ts) for t().
 */

/** A customer-added monthly cash allowance (code ALW_*), as opposed to a contract benefit (BEN_*). */
export const isCashAllowance = (c: Pick<MatrixComponent, 'code' | 'isCustom'>) =>
  !!c.isCustom && c.code.toUpperCase().startsWith('ALW_');

export const EOSB_REASON_MAX = 500;
export const BENEFIT_NAME_MAX = 100;

/** Why a monthly allowance can be left out of end of service pay (the English text is the i18n key shown in the select). */
export const EOSB_CATEGORIES: ReadonlyArray<{ value: EosbExclusionCategory; labelKey: string }> = [
  { value: 'Reimbursement', labelKey: 'Reimbursement' },
  { value: 'Discretionary', labelKey: 'Discretionary' },
  { value: 'NonRecurring', labelKey: 'Non-recurring' },
  { value: 'Other', labelKey: 'Other' },
];

/** The end of service choice shared by adding, confirming and correcting an allowance. `eosbReason` is the note. */
export interface EosbChoice { eosbIncluded: boolean; eosbCategory: EosbExclusionCategory | ''; eosbReason: string }
export type EosbField = 'eosbCategory' | 'eosbReason';

/** What stops an end of service choice being sent, by field (an i18n key each). A category is always needed when it is left out; a note only for Other. */
export function eosbProblems(f: EosbChoice): Partial<Record<EosbField, string>> {
  const out: Partial<Record<EosbField, string>> = {};
  if (f.eosbIncluded) return out;
  const note = f.eosbReason.trim();
  if (!f.eosbCategory) out.eosbCategory = 'Choose why this allowance is left out of end of service pay.';
  if (f.eosbCategory === 'Other' && !note) out.eosbReason = 'Say why this allowance is left out of end of service pay.';
  else if (note.length > EOSB_REASON_MAX) out.eosbReason = 'Keep the reason under 500 characters.';
  return out;
}

/** The end of service part of a request: nothing about a category or note when it is included. */
export function eosbRequest(f: EosbChoice): Pick<CreateBenefitBody, 'eosbIncluded' | 'eosbExclusionCategory' | 'eosbExclusionReason'> {
  if (f.eosbIncluded) return { eosbIncluded: true, eosbExclusionCategory: null, eosbExclusionReason: null };
  return { eosbIncluded: false, eosbExclusionCategory: f.eosbCategory || null, eosbExclusionReason: f.eosbReason.trim() || null };
}

export interface AddBenefitForm extends EosbChoice {
  nameEn: string; nameAr: string; kind: BenefitKind;
  glDriverKey: string;
}

export const blankAddBenefit: AddBenefitForm = { nameEn: '', nameAr: '', kind: 'Contract', glDriverKey: '', eosbIncluded: true, eosbCategory: '', eosbReason: '' };

/** The fields a problem can belong to; `general` is anything not tied to one field. */
export type AddBenefitField = 'name' | 'glDriver' | 'eosbCategory' | 'eosbReason';
export type AddBenefitProblems = Partial<Record<AddBenefitField, string>>;

/** What stops the form being sent, by field (an i18n key each). Mirrors the server, which stays the backstop. */
export function addBenefitProblems(f: AddBenefitForm): AddBenefitProblems {
  const out: AddBenefitProblems = {};
  if (!f.nameEn.trim() && !f.nameAr.trim()) out.name = 'Give the benefit a name.';
  if (f.kind !== 'Cash') return out;
  if (!f.glDriverKey.trim()) out.glDriver = 'Choose the GL account this allowance posts to.';
  return { ...out, ...eosbProblems(f) };
}

/** The request to send. A contract benefit carries nothing about pay; a cash allowance carries its GL account and end of service choice. */
export function addBenefitRequest(f: AddBenefitForm): CreateBenefitBody {
  const base = { nameEn: f.nameEn.trim() || null, nameAr: f.nameAr.trim() || null };
  if (f.kind !== 'Cash') return { ...base, kind: 'Contract' };
  return { ...base, kind: 'Cash', glDriverKey: f.glDriverKey.trim(), ...eosbRequest(f) };
}

export interface ServerProblem {
  /** The field it belongs to, or `general` for the form as a whole. */
  field: AddBenefitField | 'general';
  /** An i18n key for a refusal we know; otherwise the server's own sentence in `message`. */
  key?: string;
  message?: string;
}

/** Server refusal codes of POST /api/entitlements/components, and where each belongs. */
const serverCodes: Record<string, { field: AddBenefitField; key?: string }> = {
  name_required: { field: 'name', key: 'Give the benefit a name.' },
  name_too_long: { field: 'name', key: 'Keep each name under 100 characters.' },
  name_invalid: { field: 'name', key: 'A name can’t contain control characters.' },
  benefit_exists: { field: 'name', key: 'A benefit with this name already exists. Choose a different name.' },
  gl_driver_required: { field: 'glDriver', key: 'Choose the GL account this allowance posts to.' },
  // The server names the driver and the company where it is not mapped: its own sentence is the useful part.
  gl_driver_invalid: { field: 'glDriver' },
  eosb_category_invalid: { field: 'eosbCategory', key: 'Choose why this allowance is left out of end of service pay.' },
  eosb_reason_required: { field: 'eosbReason', key: 'Say why this allowance is left out of end of service pay.' },
  eosb_reason_too_long: { field: 'eosbReason', key: 'Keep the reason under 500 characters.' },
};

/** Where a failed "add benefit" belongs: the server answers `{ error, message }`. */
export function benefitServerProblem(err: unknown, fallbackMessage: string | null = null): ServerProblem {
  const data = (err as { response?: { data?: { error?: unknown; message?: unknown } } })?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : '';
  const message = typeof data?.message === 'string' && data.message.trim() ? data.message : null;
  const known = serverCodes[code];
  if (known) return known.key ? { field: known.field, key: known.key } : { field: known.field, message: message ?? fallbackMessage ?? undefined };
  return { field: 'general', message: message ?? fallbackMessage ?? undefined };
}

export interface GlChoice {
  key: string; label: string;
  /** False when the server would refuse it for a cash allowance; `reasonKey` (an i18n template with {account}) says why. */
  usable: boolean;
  reasonKey: string | null;
}

/** Why an account cannot carry a cash allowance, per the server's reason code (i18n keys). */
const reasonKeys: Record<string, string> = {
  reserved: '{account} — kept for payroll’s own lines',
  employer_pair: '{account} — an employer cost account',
  unmapped: '{account} — not mapped to an account yet',
  protected_account: '{account} — posts to a protected account',
};

/**
 * The GL accounts a cash allowance may post to, from GET /api/entitlements/components/cash-gl-drivers: the server judges each one by
 * exactly the rule the add applies, so the list needs no finance permission and no second guess here. In the server's order; the
 * Arabic label when there is one and the screen is in Arabic.
 */
export function cashGlChoices(rows: readonly CashGlDriver[], locale: string): GlChoice[] {
  return rows.map(r => ({
    key: r.key,
    label: (locale.startsWith('ar') && r.labelAr?.trim()) || r.label,
    usable: r.usable,
    reasonKey: r.usable ? null : reasonKeys[r.reasonCode ?? ''] ?? '{account} — no usable account',
  }));
}

// ── Finance confirms and corrects an allowance ──────────────────────────────────────────────────────────────────────────────

/** A cash allowance a person without the Finance permission added: it takes no grade value and sits on no salary until Finance confirms it. */
export const isPendingFinance = (c: Pick<MatrixComponent, 'cashAllowance'>) => c.cashAllowance?.financePending === true;

/** What the confirm and correct panels edit: the GL account and the end of service choice, starting from what is in effect. */
export interface AllowanceSettingsForm extends EosbChoice { glDriverKey: string }

export function settingsFromInfo(info: Pick<CashAllowanceInfo, 'glDriverKey' | 'eosbIncluded' | 'eosbExclusionCategory' | 'eosbExclusionReason'> | null | undefined): AllowanceSettingsForm {
  return {
    glDriverKey: info?.glDriverKey ?? '', eosbIncluded: info?.eosbIncluded ?? true,
    eosbCategory: info?.eosbIncluded === false ? info.eosbExclusionCategory ?? '' : '', eosbReason: info?.eosbIncluded === false ? info.eosbExclusionReason ?? '' : '',
  };
}

export type ConfirmField = 'glDriver' | EosbField;
export type ConfirmProblems = Partial<Record<ConfirmField, string>>;

/** What stops a confirmation being sent (the server stays the backstop). */
export function confirmProblems(f: AllowanceSettingsForm): ConfirmProblems {
  return { ...(f.glDriverKey.trim() ? {} : { glDriver: 'Choose the GL account this allowance posts to.' }), ...eosbProblems(f) };
}

/** The confirmation to send: the account and end of service choice as shown. */
export function confirmRequest(f: AllowanceSettingsForm): ConfirmAllowanceBody {
  return { glDriverKey: f.glDriverKey.trim(), ...eosbRequest(f) };
}

const sameEosb = (a: EosbChoice, b: EosbChoice) => a.eosbIncluded === b.eosbIncluded
  && (a.eosbIncluded || (a.eosbCategory === b.eosbCategory && a.eosbReason.trim() === b.eosbReason.trim()));

/** Whether the form differs from what is in effect, by part: the GL account, the end of service choice. */
export function settingsChanges(now: AllowanceSettingsForm, next: AllowanceSettingsForm) {
  return { gl: now.glDriverKey.trim() !== next.glDriverKey.trim(), eosb: !sameEosb(now, next) };
}

export interface CorrectForm extends AllowanceSettingsForm { effectiveFrom: string; reason: string }
export type CorrectField = ConfirmField | 'effectiveFrom' | 'reason' | 'change';
export type CorrectProblems = Partial<Record<CorrectField, string>>;

const MONTH_START = /^\d{4}-\d{2}-01$/;

/** What stops a correction being sent. `change` is "nothing differs from what is in effect". */
export function correctProblems(f: CorrectForm, now: AllowanceSettingsForm): CorrectProblems {
  const out: CorrectProblems = {};
  if (!MONTH_START.test(f.effectiveFrom)) out.effectiveFrom = 'Choose the month the change starts from.';
  if (!f.reason.trim()) out.reason = 'Say why you are changing it.';
  const diff = settingsChanges(now, f);
  if (!diff.gl && !diff.eosb) out.change = 'Change the GL account or the end of service setting, or cancel.';
  if (diff.gl && !f.glDriverKey.trim()) out.glDriver = 'Choose the GL account this allowance posts to.';
  if (diff.eosb) Object.assign(out, eosbProblems(f));
  return out;
}

/** The correction to send: only the parts that changed. Call only when `correctProblems` is empty. */
export function correctRequest(f: CorrectForm, now: AllowanceSettingsForm): CorrectAllowanceBody {
  const diff = settingsChanges(now, f);
  return {
    effectiveFrom: f.effectiveFrom, reason: f.reason.trim(),
    ...(diff.gl ? { glDriverKey: f.glDriverKey.trim() } : {}),
    ...(diff.eosb ? eosbRequest(f) : {}),
  };
}

/** Where a refusal of confirm or correct belongs. */
export interface FinanceProblem {
  field: CorrectField | 'general';
  /** An i18n key for a refusal we know; otherwise the server's own sentence in `message`. */
  key?: string;
  message?: string;
  /** The person who added the allowance cannot also confirm it: the Confirm action is withdrawn for them. */
  makerBlocked?: boolean;
  /** `already_confirmed` / `not_confirmed` / `not_a_cash_allowance`: the screen is out of date; reload it. */
  stale?: boolean;
  /** `period_committed`: the first month that can still be changed (yyyy-MM-01). */
  earliestEffectiveFrom?: string;
}

const financeCodes: Record<string, Omit<FinanceProblem, 'message'> & { useServerMessage?: boolean }> = {
  gl_driver_required: { field: 'glDriver', key: 'Choose the GL account this allowance posts to.' },
  gl_driver_invalid: { field: 'glDriver', useServerMessage: true },
  eosb_category_invalid: { field: 'eosbCategory', key: 'Choose why this allowance is left out of end of service pay.' },
  eosb_reason_required: { field: 'eosbReason', key: 'Say why this allowance is left out of end of service pay.' },
  eosb_reason_too_long: { field: 'eosbReason', key: 'Keep the reason under 500 characters.' },
  effective_from_required: { field: 'effectiveFrom', key: 'Choose the month the change starts from.' },
  effective_from_not_month_start: { field: 'effectiveFrom', key: 'A change starts on the first day of a month. Choose a month.' },
  reason_required: { field: 'reason', key: 'Say why you are changing it.' },
  reason_too_long: { field: 'reason', key: 'Shorten the reason.' },
  nothing_to_change: { field: 'change', key: 'Change the GL account or the end of service setting, or cancel.' },
  group_scope_required: { field: 'general', key: 'Only someone who can reach every company can do this.' },
  finance_required: { field: 'general', key: 'Only Finance can do this.' },
  maker_cannot_confirm: { field: 'general', key: 'You added this allowance, so someone else in Finance must confirm it.', makerBlocked: true },
  not_a_cash_allowance: { field: 'general', key: 'This benefit is not a monthly cash allowance.', stale: true },
  already_confirmed: { field: 'general', key: 'Finance has already confirmed this allowance.', stale: true },
  not_confirmed: { field: 'general', key: 'This allowance has not been confirmed yet. Confirm it first.', stale: true },
  later_version_exists: { field: 'effectiveFrom', key: 'A later change to this allowance already exists. Choose a month after it.' },
  period_committed: { field: 'effectiveFrom', key: 'A payroll month from that date has already been approved or locked.' },
};

/** Where a failed confirm or correct belongs: the server answers `{ error, message }` (and `earliestEffectiveFrom` for a committed period). */
export function financeServerProblem(err: unknown, fallbackMessage: string | null = null): FinanceProblem {
  const data = (err as { response?: { data?: { error?: unknown; code?: unknown; message?: unknown; earliestEffectiveFrom?: unknown } } })?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : typeof data?.code === 'string' ? data.code : '';
  const message = typeof data?.message === 'string' && data.message.trim() ? data.message : null;
  const known = financeCodes[code];
  const earliest = typeof data?.earliestEffectiveFrom === 'string' && /^\d{4}-\d{2}-\d{2}/.test(data.earliestEffectiveFrom) ? data.earliestEffectiveFrom.slice(0, 10) : undefined;
  if (!known) return { field: 'general', message: message ?? fallbackMessage ?? undefined };
  const { useServerMessage, ...rest } = known;
  return {
    ...rest, ...(useServerMessage ? { message: message ?? fallbackMessage ?? undefined } : {}),
    ...(code === 'period_committed' && earliest ? { earliestEffectiveFrom: earliest } : {}),
  };
}

/** The first day of each month from `back` months before `today` to `ahead` months after, plus `extra` (a month the server named), in order. */
export function correctionMonths(today: string, back: number, ahead: number, extra?: string): string[] {
  const [y, m] = today.split('-').map(Number);
  const out: string[] = [];
  for (let i = -back; i <= ahead; i++) {
    const index = (y * 12 + (m - 1)) + i;
    out.push(`${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}-01`);
  }
  if (extra && MONTH_START.test(extra) && !out.includes(extra)) out.push(extra);
  return out.sort();
}

/** The draft runs a correction left calculated with the old setting, as "June 2026, July 2026" (months given by `monthName`). */
export const staleRunMonths = (runs: readonly StaleRun[], monthName: (iso: string) => string) =>
  [...runs].sort((a, b) => a.year - b.year || a.month - b.month)
    .map(r => monthName(`${r.year}-${String(r.month).padStart(2, '0')}-01`)).join(', ');

/** The cautions to show after an add, confirm or correct (non-blocking), de-duplicated and without blanks. */
export function allowanceWarnings(list: readonly AllowanceWarning[] | null | undefined): string[] {
  return [...new Set((list ?? []).map(w => w.message?.trim()).filter((m): m is string => !!m))];
}
