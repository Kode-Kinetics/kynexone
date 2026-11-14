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
  IMPORTED_BANK_DETAILS_UNVERIFIED: 'Bank details came from an employee import and have not been confirmed by a second person. Confirm them on the employee record before a run is locked.',
  WARN_ARREARS_LOOKBACK_TRUNCATED: 'A salary change reaches back further than the arrears lookback, so some finalised periods were not settled by this run. Raise a supplementary run, or widen the arrears lookback in the payroll settings.',
};
const PERMIT_ACTIVE = 'Records required before payroll are missing. Until they are recorded, exporting the WPS or bank file needs an explicit acknowledgement to include this employee.';
const PERMIT_LEFT = 'Records required before payroll are missing. The WPS or bank file will refuse this employee until they are recorded.';

export function findingText(t: T, finding: { code: string; message: string }): string {
  if (finding.code === 'READINESS_PAY_BLOCKED') return t(/explicit acknowledgement/.test(finding.message) ? PERMIT_ACTIVE : PERMIT_LEFT);
  const known = BY_CODE[finding.code];
  if (known) return t(known);
  return finding.message.replace(/payparameter\.[a-z0-9_]+/gi, t('Payroll setting').toLowerCase());
}
