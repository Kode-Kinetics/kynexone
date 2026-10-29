import { msg } from '../i18n/translations';

/** Just what the Tax policies list needs to decide which row actions to offer. */
export interface TaxPolicyRowState {
  effectiveTo: string | null;
  status: string;
  usedByPayroll?: boolean;
}

export interface TaxPolicyActions {
  /** Rate, start date, country and company can be changed (false once finalized payroll used the policy). */
  canEditFigures: boolean;
  /** An end date can be set: the policy is still running past today. */
  canRetire: boolean;
  /** The row can be deleted: nothing finalized ever used it. */
  canRemove: boolean;
  /** Dictionary key explaining why Remove is not offered (null when it is). */
  removeBlockedReason: string | null;
}

/**
 * Removal is uniform across Setup: a policy nothing used is deleted; one payroll has used is ended
 * from a date instead, so history is never rewritten. `today` is a yyyy-MM-dd string.
 */
export function taxPolicyActions(p: TaxPolicyRowState, today: string): TaxPolicyActions {
  const used = !!p.usedByPayroll;
  return {
    canEditFigures: !used,
    canRetire: p.status !== 'Archived' && (p.effectiveTo === null || p.effectiveTo > today),
    canRemove: !used,
    removeBlockedReason: used
      ? msg('Payroll has already been finalized under this policy, so it cannot be deleted. End it from a date instead.')
      : null,
  };
}
