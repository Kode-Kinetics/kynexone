import { movedSectionSpecs } from './fixture';

// Talent & employee services, and Payroll & compliance: templates, rules and statutory settings.
movedSectionSpecs([
  { id: 'onboardingChecklists', label: 'Onboarding checklists', group: 'Talent & employee services', old: '/setup?tab=onboardingChecklists',
    ready: page => page.getByRole('button', { name: /checklist|template/i }).first() },
  { id: 'performanceScorecards', label: 'Review scorecards', group: 'Talent & employee services', old: '/performance?tab=templates',
    ready: page => page.getByRole('button', { name: /New|Create/i }).first(),
    module: { path: '/performance', goneTab: 'Scorecards', door: 'Performance settings' } },
  { id: 'letterTemplates', label: 'Letter templates', group: 'Talent & employee services', old: '/hr-letters?tab=templates',
    ready: page => page.getByRole('button', { name: /template/i }).first(),
    module: { path: '/hr-letters', goneTab: 'Templates', door: 'Letter settings' } },
  { id: 'engagementPrivacy', label: 'Survey privacy', group: 'Talent & employee services', old: '/engagement?tab=settings',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    module: { path: '/engagement', goneTab: 'Privacy settings', door: 'Engagement settings' } },
  { id: 'taxPolicies', label: 'Tax policies', group: 'Payroll & compliance', old: '/tax-policies',
    ready: page => page.getByRole('button', { name: /policy/i }).first() },
  { id: 'complianceProfiles', label: 'Compliance profiles', group: 'Payroll & compliance', old: '/compliance-profiles',
    ready: page => page.getByRole('combobox').first() },
  { id: 'saudiCompliance', label: 'Saudi compliance', group: 'Payroll & compliance', old: '/saudi-compliance?tab=configure',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    module: { path: '/saudi-compliance', goneTab: 'Configure', door: 'Saudi compliance settings' } },
  { id: 'countryPayrollKeys', label: 'Country payroll keys', group: 'Payroll & compliance', old: '/tenant-admin?tab=country-rules',
    ready: page => page.getByRole('button', { name: /Add/i }).first() },
]);
