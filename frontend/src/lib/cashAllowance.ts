import type { BenefitKind, CashGlDriver, CreateBenefitBody, MatrixComponent } from '../api/entitlements';

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

export interface AddBenefitForm {
  nameEn: string; nameAr: string; kind: BenefitKind;
  glDriverKey: string; eosbIncluded: boolean; eosbReason: string;
}

export const blankAddBenefit: AddBenefitForm = { nameEn: '', nameAr: '', kind: 'Contract', glDriverKey: '', eosbIncluded: true, eosbReason: '' };

/** The fields a problem can belong to; `general` is anything not tied to one field. */
export type AddBenefitField = 'name' | 'glDriver' | 'eosbReason';
export type AddBenefitProblems = Partial<Record<AddBenefitField, string>>;

/** What stops the form being sent, by field (an i18n key each). Mirrors the server, which stays the backstop. */
export function addBenefitProblems(f: AddBenefitForm): AddBenefitProblems {
  const out: AddBenefitProblems = {};
  if (!f.nameEn.trim() && !f.nameAr.trim()) out.name = 'Give the benefit a name.';
  if (f.kind !== 'Cash') return out;
  if (!f.glDriverKey.trim()) out.glDriver = 'Choose the GL account this allowance posts to.';
  if (!f.eosbIncluded) {
    if (!f.eosbReason.trim()) out.eosbReason = 'Say why this allowance is left out of end of service pay.';
    else if (f.eosbReason.trim().length > EOSB_REASON_MAX) out.eosbReason = 'Keep the reason under 500 characters.';
  }
  return out;
}

/** The request to send. A contract benefit carries nothing about pay; a cash allowance carries its GL account and end of service choice. */
export function addBenefitRequest(f: AddBenefitForm): CreateBenefitBody {
  const base = { nameEn: f.nameEn.trim() || null, nameAr: f.nameAr.trim() || null };
  if (f.kind !== 'Cash') return { ...base, kind: 'Contract' };
  return {
    ...base, kind: 'Cash', glDriverKey: f.glDriverKey.trim(), eosbIncluded: f.eosbIncluded,
    eosbExclusionReason: f.eosbIncluded ? null : f.eosbReason.trim(),
  };
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
