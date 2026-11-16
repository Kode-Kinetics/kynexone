import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * The Saudi Compliance dashboard's action items, GOSI findings and warnings, in the viewer's language.
 *
 * The API (SaudiComplianceDashboardService) sends English sentences, with the count baked into the title. The action's `id`
 * is the contract: a known id is shown from the dictionary (English and Arabic, with the count as a plural), an unknown id keeps
 * what the API sent. The English text a key produces is the sentence the API sends, so the English screen does not change.
 */
const PLURAL_TITLES: Record<string, string> = {
  qiwa_blocked_employees: '{count, plural, one {# employee is blocked from QIWA sync} other {# employees are blocked from QIWA sync}}',
  qiwa_failed_syncs: '{count, plural, one {# QIWA sync attempt failed and needs retrying} other {# QIWA sync attempts failed and need retrying}}',
  wps_missing_iban: '{count, plural, one {# employee has a missing or invalid IBAN} other {# employees have a missing or invalid IBAN}}',
  wps_pending_approvals: '{count, plural, one {# payroll run is awaiting approval} other {# payroll runs are awaiting approval}}',
  gosi_blocked_employees: '{count, plural, one {# employee is blocked from GOSI calculation} other {# employees are blocked from GOSI calculation}}',
  gosi_gcc_pending_confirmation: '{count, plural, one {# GCC employee — contribution rates pending legal confirmation} other {# GCC employees — contribution rates pending legal confirmation}}',
  gosi_variance_detected: '{count, plural, one {# GOSI variance detected in the last payroll run} other {# GOSI variances detected in the last payroll run}}',
};

const PLAIN_TITLES: Record<string, string> = {
  qiwa_feature_disabled: 'QIWA integration is not enabled',
  qiwa_credentials_missing: 'QIWA API credentials are not configured',
  qiwa_not_configured: 'QIWA connection is not configured',
  qiwa_connection_error: 'QIWA connection is in an error state',
  qiwa_simulated: 'Qiwa data check only: nothing is sent to Qiwa',
  gosi_missing_employer_id: 'GOSI employer ID is not set',
};

const DESCRIPTIONS: Record<string, string> = {
  qiwa_feature_disabled: 'The QIWA integration module is not active for this tenant. Enable it to begin syncing employees with the Ministry of Human Resources.',
  qiwa_credentials_missing: 'OAuth2 client credentials have not been saved. Employee sync cannot proceed until credentials are provided.',
  qiwa_not_configured: 'Establishment ID has not been set. QIWA sync requires a valid MOL-issued establishment number.',
  qiwa_connection_error: 'The QIWA integration has encountered an error (status: {status}). Sync will be suspended until this is resolved.',
  qiwa_simulated: 'This server checks your employee records against what Qiwa requires. It does not send anything to Qiwa or MHRSD, so a passed check is not a Qiwa filing.',
  qiwa_blocked_employees: 'These employees are missing required fields (e.g. National ID, Date of Birth, Job Title) and cannot be submitted to QIWA.',
  qiwa_failed_syncs: 'One or more recent employee sync operations to QIWA did not complete successfully. This may indicate a credential or API issue.',
  wps_missing_iban: 'Saudi WPS requires a valid Saudi IBAN (SA + 22 digits) for every employee. Payroll cannot be disbursed via WPS for these employees.',
  wps_pending_approvals: 'Payroll runs that are not yet Locked or Paid have not been submitted through WPS. Delays beyond the 10th of the month may result in non-compliance.',
  gosi_missing_employer_id: 'The company\'s GOSI employer ID is required for contribution filing. Without it, GOSI deductions cannot be attributed correctly.',
  gosi_blocked_employees: 'These employees are missing a GOSI reference number or basic salary and will be excluded from GOSI contribution deductions.',
  gosi_gcc_pending_confirmation: 'GCC national contribution rates are seeded at the Saudi baseline pending bilateral treaty verification. Confirm applicable rates with your legal team before payroll processing.',
  gosi_variance_detected: 'Actual GOSI deductions in the most recent run differ from expected rule-based amounts. This may indicate a rule change that was not applied retroactively.',
};

const ACTIONS: Record<string, string> = {
  qiwa_feature_disabled: 'Contact your platform administrator to activate the QIWA module.',
  qiwa_credentials_missing: 'Go to Saudi Compliance → Configure → QIWA and enter your Client ID and Secret from the Qiwa Developer Portal.',
  qiwa_not_configured: 'Navigate to Saudi Compliance → Configure → QIWA and enter the Establishment ID.',
  qiwa_connection_error: 'Review the QIWA configuration for credential or establishment ID issues.',
  qiwa_simulated: 'Record contract and employee changes in Qiwa itself, and keep the Qiwa confirmation as evidence before relying on Qiwa status for an inspection.',
  qiwa_blocked_employees: 'Open the People module and complete the missing QIWA fields for each blocked employee.',
  qiwa_failed_syncs: 'Review sync logs and retry failed records. Check credentials if the failure rate is high.',
  wps_missing_iban: 'Go to People → Employee → Payroll Profile → Payment Details and add the correct IBAN.',
  wps_pending_approvals: 'Navigate to Payroll and lock or approve the pending runs.',
  gosi_missing_employer_id: 'Go to Saudi Compliance → Configure → GOSI and enter the GOSI Employer ID.',
  gosi_blocked_employees: 'Open the People module and ensure each employee has a GOSI Reference and an active salary structure.',
  gosi_gcc_pending_confirmation: 'Review GCC employee GOSI rates under Compliance → GOSI Contribution Rules and update if required.',
  gosi_variance_detected: 'Run the GOSI variance report for the most recent payroll period and investigate discrepancies.',
};

export interface ComplianceActionLike { id: string; title: string; description: string; recommendedAction: string; affectedCount: number }

export function complianceActionText(t: T, item: ComplianceActionLike): { title: string; description: string; recommendedAction: string } {
  const title = PLURAL_TITLES[item.id] ? t(PLURAL_TITLES[item.id], { count: item.affectedCount }) : PLAIN_TITLES[item.id] ? t(PLAIN_TITLES[item.id]) : item.title;
  const status = /\(status: ([^)]+)\)/.exec(item.description)?.[1] ?? '';
  const known = DESCRIPTIONS[item.id];
  const description = known && (item.id !== 'qiwa_connection_error' || status) ? t(known, { status }) : item.description;
  const recommendedAction = ACTIONS[item.id] ? t(ACTIONS[item.id]) : item.recommendedAction;
  return { title, description, recommendedAction };
}

/** The module a finding belongs to ("GOSI", "QIWA", "WPS"). Any other module name is shown as sent. */
export function moduleName(t: T, module: string): string {
  switch (module.toUpperCase()) {
    case 'QIWA': return t('QIWA');
    case 'WPS': return t('WPS');
    case 'GOSI': return t('GOSI');
    default: return module;
  }
}

const GOSI_FINDINGS: Record<string, string> = {
  GOSI_COHORT_NOT_RECORDED: 'GOSI first-registration date not recorded',
  MISSING_GOSI_REFERENCE: 'GOSI reference number missing',
  MISSING_BASIC_SALARY: 'Basic salary missing',
  MISSING_NATIONALITY: 'Nationality not recorded',
  GCC_RULES_PENDING_CONFIRMATION: 'GCC contribution rates pending confirmation',
};

/** A GOSI readiness finding code as a plain phrase. An unknown code is shown as sent, never guessed. */
export function gosiFindingLabel(t: T, code: string): string {
  return GOSI_FINDINGS[code] ? t(GOSI_FINDINGS[code]) : code;
}

/** The GOSI card's warning sentences. An unknown one is shown as sent. */
export function gosiWarningText(t: T, warning: string): string {
  if (warning === 'Company GOSI employer ID is not set.') return t(warning);
  const missing = /^(\d+) employees? (?:is|are) missing a GOSI reference number\.$/.exec(warning);
  if (missing) return t('{count, plural, one {# employee is missing a GOSI reference number.} other {# employees are missing a GOSI reference number.}}', { count: Number(missing[1]) });
  const gcc = /^(\d+) GCC employees? — contribution rates pending legal confirmation\.$/.exec(warning);
  if (gcc) return t('{count, plural, one {# GCC employee — contribution rates pending legal confirmation.} other {# GCC employees — contribution rates pending legal confirmation.}}', { count: Number(gcc[1]) });
  return warning;
}
