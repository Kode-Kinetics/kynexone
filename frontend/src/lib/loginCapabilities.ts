/** Claims shown before authentication must describe availability, not imply a
 * tenant has configured or certified an integration. */
export const LOGIN_CAPABILITIES = [
  'Bank payroll files with WPS fields (ANB)',
  'GOSI & social-insurance workflows',
  'EOSB gratuity calculations',
  'Qiwa data checks & Mudad evidence tracking',
  'Shift & roster planning',
  'Overtime & time-off',
  'Loans & advances',
  'Payslip designer',
  'Performance & calibration',
  'Recruitment & onboarding',
  'Org chart',
  'Employee self-service',
  'Multi-company & multi-currency',
  'Approval workflows',
  'Saudization tracking',
  'Hijri-aware date support',
  'Document & visa compliance',
  'Bank file generation',
  'Role-based access',
  'Audit trails',
] as const;

export const LOGIN_PREVIEW_DISCLOSURE = 'Illustrative sample data — not your workspace';
