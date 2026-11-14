import { expect, test } from '@playwright/test';
import { complianceActionText, gosiFindingLabel, gosiWarningText, moduleName } from '../src/lib/complianceCopy';
import { translate } from '../src/i18n/translations';

type P = Record<string, string | number | null | undefined>;
const en = (key: string, params?: P) => translate('en', key, params);
const ar = (key: string, params?: P) => translate('ar', key, params);
const latin = (s: string) => s.match(/[A-Za-z]{4,}/g) ?? [];

// What the API sends for each action item (SaudiComplianceDashboardService.BuildActionItems), English only.
const item = (id: string, title: string, description: string, recommendedAction: string, affectedCount = 0) => ({ id, title, description, recommendedAction, affectedCount });
const SERVER = [
  item('qiwa_feature_disabled', 'QIWA integration is not enabled', 'The QIWA integration module is not active for this tenant. Enable it to begin syncing employees with the Ministry of Human Resources.', 'Contact your platform administrator to activate the QIWA module.'),
  item('qiwa_credentials_missing', 'QIWA API credentials are not configured', 'OAuth2 client credentials have not been saved. Employee sync cannot proceed until credentials are provided.', 'Go to Saudi Compliance → Configure → QIWA and enter your Client ID and Secret from the Qiwa Developer Portal.'),
  item('qiwa_not_configured', 'QIWA connection is not configured', 'Establishment ID has not been set. QIWA sync requires a valid MOL-issued establishment number.', 'Navigate to Saudi Compliance → Configure → QIWA and enter the Establishment ID.'),
  item('qiwa_connection_error', 'QIWA connection is in an error state', 'The QIWA integration has encountered an error (status: ApiError). Sync will be suspended until this is resolved.', 'Review the QIWA configuration for credential or establishment ID issues.'),
  item('qiwa_simulated', 'Qiwa data check only: nothing is sent to Qiwa', 'This server checks your employee records against what Qiwa requires. It does not send anything to Qiwa or MHRSD, so a passed check is not a Qiwa filing.', 'Record contract and employee changes in Qiwa itself, and keep the Qiwa confirmation as evidence before relying on Qiwa status for an inspection.'),
  item('qiwa_blocked_employees', '9 employees are blocked from QIWA sync', 'These employees are missing required fields (e.g. National ID, Date of Birth, Job Title) and cannot be submitted to QIWA.', 'Open the People module and complete the missing QIWA fields for each blocked employee.', 9),
  item('qiwa_failed_syncs', '3 QIWA sync attempts failed and need retrying', 'One or more recent employee sync operations to QIWA did not complete successfully. This may indicate a credential or API issue.', 'Review sync logs and retry failed records. Check credentials if the failure rate is high.', 3),
  item('wps_missing_iban', '2 employees have a missing or invalid IBAN', 'Saudi WPS requires a valid Saudi IBAN (SA + 22 digits) for every employee. Payroll cannot be disbursed via WPS for these employees.', 'Go to People → Employee → Payroll Profile → Payment Details and add the correct IBAN.', 2),
  item('wps_pending_approvals', '2 payroll runs are awaiting approval', 'Payroll runs that are not yet Locked or Paid have not been submitted through WPS. Delays beyond the 10th of the month may result in non-compliance.', 'Navigate to Payroll and lock or approve the pending runs.', 2),
  item('gosi_missing_employer_id', 'GOSI employer ID is not set', "The company's GOSI employer ID is required for contribution filing. Without it, GOSI deductions cannot be attributed correctly.", 'Go to Saudi Compliance → Configure → GOSI and enter the GOSI Employer ID.'),
  item('gosi_blocked_employees', '6 employees are blocked from GOSI calculation', 'These employees are missing a GOSI reference number or basic salary and will be excluded from GOSI contribution deductions.', 'Open the People module and ensure each employee has a GOSI Reference and an active salary structure.', 6),
  item('gosi_gcc_pending_confirmation', '1 GCC employee — contribution rates pending legal confirmation', 'GCC national contribution rates are seeded at the Saudi baseline pending bilateral treaty verification. Confirm applicable rates with your legal team before payroll processing.', 'Review GCC employee GOSI rates under Compliance → GOSI Contribution Rules and update if required.', 1),
  item('gosi_variance_detected', '1 GOSI variance detected in the last payroll run', 'Actual GOSI deductions in the most recent run differ from expected rule-based amounts. This may indicate a rule change that was not applied retroactively.', 'Run the GOSI variance report for the most recent payroll period and investigate discrepancies.', 1),
];

test.describe('compliance action copy', () => {
  for (const s of SERVER) {
    test(`${s.id}: English is the sentence the API sends, Arabic has no English left`, () => {
      const english = complianceActionText(en, s);
      expect(english).toEqual({ title: s.title, description: s.description, recommendedAction: s.recommendedAction });
      const arabic = complianceActionText(ar, s);
      for (const field of ['title', 'description', 'recommendedAction'] as const) {
        expect(arabic[field], `${s.id} ${field}`).not.toBe(s[field]);
        expect(latin(arabic[field].replace(/⁦[^⁩]*⁩/g, '')), `${s.id} ${field}: ${arabic[field]}`).toEqual([]);
      }
    });
  }

  test('an unknown action keeps what the API sent', () => {
    const unknown = item('something_new', 'Something new', 'Details', 'Do it');
    expect(complianceActionText(ar, unknown)).toEqual({ title: 'Something new', description: 'Details', recommendedAction: 'Do it' });
  });

  test('modules and GOSI finding codes have names, never the raw code', () => {
    for (const m of ['QIWA', 'WPS', 'GOSI']) expect(latin(moduleName(ar, m)), m).toEqual([]);
    for (const code of ['GOSI_COHORT_NOT_RECORDED', 'MISSING_GOSI_REFERENCE', 'MISSING_BASIC_SALARY', 'MISSING_NATIONALITY', 'GCC_RULES_PENDING_CONFIRMATION']) {
      expect(gosiFindingLabel(en, code), code).not.toBe(code);
      expect(gosiFindingLabel(en, code), code).not.toMatch(/_/);
      const arabic = gosiFindingLabel(ar, code);
      expect(arabic, code).not.toBe(gosiFindingLabel(en, code));
      expect(latin(arabic), arabic).toEqual([]);
    }
    expect(gosiFindingLabel(en, 'SOME_NEW_CODE')).toBe('SOME_NEW_CODE');
  });

  test('GOSI card warnings read in Arabic', () => {
    const warnings = ['Company GOSI employer ID is not set.', '4 employees are missing a GOSI reference number.', '1 employee is missing a GOSI reference number.', '2 GCC employees — contribution rates pending legal confirmation.'];
    for (const w of warnings) {
      expect(gosiWarningText(en, w), w).toBe(w);
      const arabic = gosiWarningText(ar, w);
      expect(arabic, w).not.toBe(w);
      expect(latin(arabic), arabic).toEqual([]);
    }
    expect(gosiWarningText(ar, 'A warning nobody has translated')).toBe('A warning nobody has translated');
  });
});
