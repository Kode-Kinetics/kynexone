/**
 * The words of the reconciliation screens. Every one goes through t() with a literal key (the i18n
 * ratchet reads them), so a translator sees whole sentences and Arabic can reorder them.
 */
import type { useT } from '../../hooks/useT';
import type { ReconSectionKind, SignoffSupersededReason } from '../../api/payrollRecon';
import type { SignoffBlocker } from '../../lib/payrollRecon';

type T = ReturnType<typeof useT>;

export function categoryName(t: T, category: string): string {
  switch (category) {
    case 'NEW_JOINER': return t('New joiners');
    case 'REHIRE': return t('Rehires');
    case 'LATE_JOINER': return t('Late joiners');
    case 'REINSTATED': return t('Reinstated');
    case 'TRANSFER_IN': return t('Transferred in');
    case 'LEAVER': return t('Leavers');
    case 'TRANSFER_OUT': return t('Transferred out');
    case 'NOT_PAID': return t('Not paid this month');
    case 'UNEXPLAINED': return t('Unexplained');
    case 'OPENING': return t('Opening headcount');
    case 'CLOSING': return t('Closing headcount');
    case 'FLAGGED': return t('Pay changed beyond the threshold');
    default: return category;
  }
}

export function categoryHint(t: T, category: string): string {
  switch (category) {
    case 'NEW_JOINER': return t('Joined this month and never paid before.');
    case 'REHIRE': return t('Came back after leaving the company earlier.');
    case 'LATE_JOINER': return t('Joined before this month but is paid for the first time. Check why they were missed.');
    case 'REINSTATED': return t('Paid before, missed last month, paid again now (hold, suspension or unpaid leave).');
    case 'TRANSFER_IN': return t('Was paid last month by another company in the group.');
    case 'LEAVER': return t('Paid last month and not this month because they left.');
    case 'TRANSFER_OUT': return t('Paid last month here, now employed by another company in the group.');
    case 'NOT_PAID': return t('Still employed but left out of this run. Each needs a written reason at sign-off.');
    case 'UNEXPLAINED': return t('Paid last month and not this month, with no reason on record. Each needs a written reason at sign-off.');
    default: return '';
  }
}

export function driverName(t: T, key: string): string {
  switch (key) {
    case 'package': return t('Package changes');
    case 'proration': return t('Proration');
    case 'variable': return t('Variable pay');
    case 'arrears': return t('Arrears');
    case 'attendance': return t('Attendance and leave');
    case 'loans': return t('Loans and recoveries');
    case 'statutory': return t('Statutory and tax');
    case 'other': return t('Other');
    default: return key;
  }
}

export function groupName(t: T, group: 'adds' | 'exits' | 'continuing'): string {
  switch (group) {
    case 'adds': return t('Added');
    case 'exits': return t('Left');
    case 'continuing': return t('Continuing staff');
  }
}

export function sectionName(t: T, kind: ReconSectionKind): string {
  switch (kind) {
    case 'Earning': return t('Earnings');
    case 'Deduction': return t('Deductions');
    case 'Employer': return t('Employer cost (not part of net pay)');
  }
}

export function fsCellName(t: T, code: string): string {
  switch (code) {
    case 'LEAVER_IN_BRIDGE': return t('The leaver is in this month’s headcount bridge');
    case 'LEFT_EARLIER': return t('Left in an earlier period');
    case 'FINAL_WAGE_MONTH': return t('Final wage month is in this run');
    case 'OTHER': return t('Other');
    default: return code;
  }
}

export function fsComponentName(t: T, code: string): string {
  switch (code) {
    case 'EOSB_GRATUITY': return t('End-of-service gratuity');
    case 'LEAVE_ENCASHMENT': return t('Leave encashment');
    case 'NOTICE_PAY': return t('Notice pay');
    case 'SETTLEMENT_OTHER': return t('Other settlement earnings');
    case 'NOTICE_SHORTFALL': return t('Notice shortfall');
    case 'SETTLEMENT_DED_OTHER': return t('Other settlement deductions');
    default: return code;
  }
}

export function supersededText(t: T, reason: SignoffSupersededReason | null | undefined, name: string): string {
  switch (reason) {
    case 'Reprocessed': return t('The earlier sign-off by {name} no longer counts: the run was processed again.', { name });
    case 'Reopened': return t('The earlier sign-off by {name} no longer counts: the run was reopened.', { name });
    case 'SentBack': return t('The earlier sign-off by {name} no longer counts: the run was sent back.', { name });
    case 'Voided': return t('The earlier sign-off by {name} no longer counts: the run was voided.', { name });
    case 'Withdrawn': return t('The earlier sign-off by {name} was withdrawn.', { name });
    case 'PriorChanged': return t('The earlier sign-off by {name} no longer counts: the month it was compared with changed.', { name });
    default: return t('The earlier sign-off by {name} no longer counts.', { name });
  }
}

export function scopeReasonText(t: T, reason: string): string {
  switch (reason) {
    case 'signoff_requires_organization_scope': return t('Sign-off needs access to the whole company.');
    case 'department_scope': return t('Your access is limited to some departments.');
    default: return reason;
  }
}

export function issueText(t: T, code: string | undefined, message: string | undefined): string {
  if (message) return message;
  switch (code) {
    case 'duplicateSlip': return t('An employee has more than one payslip in this run.');
    case 'lineRegister': return t('The payslip lines do not agree with the payslip register.');
    default: return code ?? t('An integrity check failed.');
  }
}

export function blockerText(t: T, blocker: SignoffBlocker, ctx: { min: number; flagged: number | null }): string {
  switch (blocker) {
    case 'already_signed': return t('This reconciliation is already signed off.');
    case 'no_permission': return t('You do not have permission to sign off payroll.');
    case 'run_status': return t('A run can be signed off only while it is Processed or waiting for finance review.');
    case 'not_applicable': return t('Off-cycle runs are not reconciled, so there is nothing to sign off.');
    case 'scope': return t('Sign-off needs a view of the whole company. Your access covers only part of it, so every figure here is a partial view.');
    case 'integrity': return t('The reconciliation does not tie, so it cannot be signed off. See the checks above.');
    case 'no_stamp': return t('This reconciliation carries no version stamp, so a sign-off cannot be recorded against it. Reload the page.');
    case 'employees_unknown': return t('The employees who need a written reason could not be listed, so sign-off is withheld. Retry.');
    case 'explanations': return t('Each item and employee above needs a written reason of at least {min} characters.', { min: ctx.min });
    case 'flagged_unknown': return t('The number of employees whose pay changed could not be read, so sign-off is withheld. Retry.');
    case 'acknowledge': return t('Confirm that you have reviewed the {count, plural, one {# employee} other {# employees}} whose pay changed by more than the threshold.', { count: ctx.flagged ?? 0 });
  }
}
