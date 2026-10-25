import { movedSectionSpecs } from './fixture';

// Pay & benefits: Benefits and Loans keep enrolments, loans, advances and bonus batches; their configuration is in Setup.
movedSectionSpecs([
  { id: 'benefitPlans', label: 'Benefit plans', group: 'Pay & benefits', old: '/benefits?tab=plans',
    ready: page => page.getByRole('button', { name: /New plan|Add plan|Create.*plan/i }).first(),
    module: { path: '/benefits', goneTab: 'Plans', door: 'Benefit settings' } },
  { id: 'loanTypes', label: 'Loan types', group: 'Pay & benefits', old: '/loans?tab=loanTypes',
    ready: page => page.getByRole('button', { name: /New loan type|Add loan type|Add Type/i }).first(),
    module: { path: '/loans', goneTab: 'Loan Types', door: 'Loan settings' } },
  { id: 'loanPolicies', label: 'Loan policies', group: 'Pay & benefits', old: '/loans?tab=loanPolicies',
    ready: page => page.getByRole('button', { name: /policy/i }).first(),
    module: { path: '/loans', goneTab: 'Loan Policies', door: 'Loan settings' } },
  { id: 'advancePolicy', label: 'Salary advance policy', group: 'Pay & benefits', old: '/loans?tab=advancePolicy',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    module: { path: '/loans', goneTab: 'Advance Policy', door: 'Loan settings' } },
  { id: 'bonusTypes', label: 'Bonus types', group: 'Pay & benefits', old: '/loans?tab=bonusTypes',
    ready: page => page.getByRole('button', { name: /bonus type/i }).first(),
    module: { path: '/loans', goneTab: 'Bonus Types', door: 'Loan settings' } },
  { id: 'payslipTemplates', label: 'Payslip templates', group: 'Pay & benefits', old: '/payroll/templates',
    ready: page => page.getByRole('button', { name: /New Template/i }) },
]);
