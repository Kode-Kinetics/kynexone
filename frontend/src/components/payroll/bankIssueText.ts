/**
 * A bank-file blocker in plain words. The server's code decides the sentence (so Arabic users read Arabic); an unknown code keeps the
 * server's own message. The code itself is never the first thing shown.
 */
type Translate = (key: string, params?: Record<string, string | number>) => string;

export function bankIssueText(t: Translate, issue: { code?: string; message?: string }): string {
  switch (issue.code) {
    case 'account_missing': return t('No bank account (IBAN) is saved for this employee.');
    case 'account_invalid':
    case 'account_iban_invalid': return t('The employee’s IBAN is not a valid Saudi IBAN (SA and 22 characters).');
    case 'account_internal_not_anb': return t('The employee’s account is not an account this bank can pay internally.');
    case 'bic_missing': return t('The bank code (BIC) is missing for this employee.');
    case 'bic_invalid': return t('The employee’s bank code (BIC) is not valid.');
    case 'employee_address_missing': return t('The employee’s address lines are missing.');
    case 'employee_id_invalid': return t('The employee’s national ID or Iqama number is not in the right form.');
    case 'employee_id_conflict': return t('Two employees in the file share one ID number.');
    case 'net_not_positive': return t('The employee’s net pay is zero or less.');
    case 'bank_change_pending': return t('A change to the employee’s bank details is waiting for approval.');
    case 'bank_account_changed': return t('The employee’s bank account changed after the run was locked.');
    default: return issue.message || issue.code || '';
  }
}
