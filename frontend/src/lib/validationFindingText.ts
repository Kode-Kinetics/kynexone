import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * One payroll validation finding in the viewer's language. The API sends an English sentence with the employee, dates,
 * month lists and configuration keys baked in. The review lists name the employee in the row already, so a known code is
 * shown as its own plain sentence, without the month list or the raw setting key. An unknown code keeps the API's text,
 * with any raw `payparameter.*` key replaced by a plain phrase ("payroll setting"), and is shown as sent.
 */
const BY_CODE: Record<string, string> = {
  GOSI_COHORT_NOT_RECORDED: 'GOSI cohort not recorded, so the contribution basis is unverified. Enter the GOSI first-registration date on the employee record; the next run picks it up.',
  HELD_PAY_RELEASE_NOT_PAID: 'Held pay released into this run is not paid by it, because the employee is not in this run. It stays owed. Include the employee in the run and process it again.',
  LEAVE_ARREARS_CARRIED_FORWARD: 'Unpaid-leave arrears from earlier months were not all collected this month, because deductions stay within the company’s share of the wage due. The rest carries to the next run; anything still owed when employment ends is shown on the final settlement.',
  IMPORTED_BANK_DETAILS_UNVERIFIED: 'Bank details came from an employee import and have not been confirmed by a second person. Confirm them on the employee record before a run is locked.',
  WARN_ARREARS_LOOKBACK_TRUNCATED: 'A salary change reaches back further than the arrears lookback, so some finalised periods were not settled by this run. Raise a supplementary run, or widen the arrears lookback in the payroll settings.',
};
const PERMIT_ACTIVE = 'Records required before payroll are missing. Until they are recorded, exporting the WPS or bank file needs an explicit acknowledgement to include this employee.';
const PERMIT_LEFT = 'Records required before payroll are missing. The WPS or bank file will refuse this employee until they are recorded.';

/**
 * Findings whose English sentence carries details (a name, a code, a date) the translation must keep: the server's sentence has a fixed shape, its
 * details are read back out and put into the translated template. A sentence that does not match keeps the API's text.
 */
const WITH_DETAILS: Record<string, { pattern: RegExp; names: string[]; template: string }> = {
  CASH_ALLOWANCE_CHANGE_WAITING_FOR_FINANCE: {
    pattern: /^(.+) \(([A-Z0-9_]+)\): a change is waiting for Finance since (\d{4}-\d{2}-\d{2});/,
    names: ['name', 'code', 'date'],
    template: '{name} ({code}): a change is waiting for Finance since {date}, so this run paid the last confirmed terms. Ask another Finance approver to confirm the change in Benefits by grade.',
  },
  LEGACY_BENEFIT_ENROLMENT_SKIPPED: {
    pattern: /^(.+) \(([^)]*)\): the previous benefits system's enrolment ([0-9a-f-]+) in (.+) \(([A-Za-z]+)\) was not paid\./,
    names: ['name', 'code', 'enrolment', 'plan', 'delivery'],
    template: '{name} ({code}): the previous benefits system’s enrolment in {plan} was not paid (reference {enrolment}). Benefits are now set in Benefits by grade. Review it in the legacy enrolment reconcile report, and end it or move it to the employee’s grade package.',
  },
};
const OVERTIME_UNPAID = 'Approved overtime of {amount} from an earlier pay period has not been paid by any run. Settle it through the overtime record, or override this finding with a written reason if it was settled outside payroll.';

export function findingText(t: T, finding: { code: string; message: string }): string {
  // Late overtime no run pays (rule 11b): the amount owed, in the reader's language. The row already names the employee.
  if (finding.code === 'OVERTIME_PRIOR_PERIOD_UNPAID') {
    const amount = /\(([\d.,]+) owed\)/.exec(finding.message)?.[1];
    if (amount) return t(OVERTIME_UNPAID, { amount });
  }
  if (finding.code === 'READINESS_PAY_BLOCKED') return t(/explicit acknowledgement/.test(finding.message) ? PERMIT_ACTIVE : PERMIT_LEFT);
  const detailed = WITH_DETAILS[finding.code];
  const match = detailed?.pattern.exec(finding.message);
  if (detailed && match) return t(detailed.template, Object.fromEntries(detailed.names.map((name, i) => [name, match[i + 1]])));
  const known = BY_CODE[finding.code];
  if (known) return t(known);
  return finding.message.replace(/payparameter\.[a-z0-9_]+/gi, t('Payroll setting').toLowerCase());
}
