import { expect, test } from '@playwright/test';
import { boot, movedSectionSpecs } from './fixture';

// The module pages keep their daily work, so the pages that now link to Setup load their own data from the intercepted
// API; a plain `[]` is not a valid reply for these.
const NO_ENGAGEMENT_OPTIONS = {
  companies: [], departments: [], designations: [], grades: [], gradeBands: [], tenureBands: [],
  modes: [{ value: 'Anonymous', noticeEn: 'Answers are anonymous.', noticeAr: 'الإجابات مجهولة.' }],
};
const SAUDI_DASHBOARD = {
  overall: { complianceScore: 80, urgentActionCount: 0, lastEvaluatedAt: '2026-10-01T00:00:00Z', enabledModules: ['Qiwa', 'GOSI', 'WPS'], scoreBreakdown: [] },
  qiwa: {
    featureEnabled: true, credentialConfigured: false, connectionStatus: 'NotConfigured', lastConnectedAt: null, totalEmployees: 0, readyForSync: 0,
    blockedFromSync: 0, readinessPercent: null, failedSyncCount: 0, lastSuccessfulSync: null, blockedEmployees: [],
  },
  wps: { lastRunStatus: null, lastRunPeriod: null, pendingApprovals: 0, missingIbanCount: 0, exportHistoryCount: 0, lastExportDate: null, lastExportStatus: null, blockingIssues: [] },
  gosi: {
    employeesMissingGosiRef: 0, employeesMissingGosiEmployerId: 0, gosiEmployerIdConfigured: false, readyCount: 0, blockedCount: 0, warningCount: 0,
    readinessPercent: null, gccEmployeeCount: 0, varianceCount: 0, warnings: [], blockedEmployees: [],
  },
  actionItems: [],
};

// Talent & employee services, and Payroll & compliance: templates, rules and statutory settings.
movedSectionSpecs([
  { id: 'onboardingChecklists', label: 'Onboarding checklists', group: 'Talent & employee services', old: '/setup?tab=onboardingChecklists',
    ready: page => page.getByRole('button', { name: /checklist|template/i }).first(),
    mocks: {
      '/api/recruitment/onboarding/summary': { total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 },
      '/api/recruitment/onboarding/tasks': { items: [], total: 0, page: 1, pageSize: 25 },
    },
    // The onboarding task list and generating a joiner's tasks stay; the checklist editor is the Setup section.
    module: { path: '/recruitment/onboarding', goneTab: 'Checklist Templates', door: 'Onboarding settings' } },
  { id: 'performanceScorecards', label: 'Review scorecards', group: 'Talent & employee services', old: '/performance?tab=templates',
    ready: page => page.getByRole('button', { name: /New|Create/i }).first(),
    module: { path: '/performance', goneTab: 'Scorecards', door: 'Performance settings' } },
  { id: 'letterTemplates', label: 'Letter templates', group: 'Talent & employee services', old: '/hr-letters?tab=templates',
    ready: page => page.getByRole('button', { name: 'Restore missing defaults' }),
    mocks: { '/api/hr-letters/templates': { items: [], knownTokens: [] } },
    module: { path: '/hr-letters', goneTab: 'Templates', door: 'Letter settings' } },
  { id: 'engagementPrivacy', label: 'Survey privacy', group: 'Talent & employee services', old: '/engagement?tab=settings',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    mocks: {
      '/api/engagement/settings': { minCohortSize: 5, minCohortFloor: 3, freeTextRetentionMonths: 24 },
      '/api/engagement/options': NO_ENGAGEMENT_OPTIONS,
    },
    module: { path: '/engagement', goneTab: 'Privacy settings', door: 'Engagement settings' } },
  { id: 'taxPolicies', label: 'Tax policies', group: 'Payroll & compliance', old: '/tax-policies',
    ready: page => page.getByRole('button', { name: /policy/i }).first() },
  { id: 'complianceProfiles', label: 'Compliance profiles', group: 'Payroll & compliance', old: '/compliance-profiles',
    ready: page => page.getByRole('combobox').first() },
  { id: 'saudiCompliance', label: 'Saudi compliance', group: 'Payroll & compliance', old: '/saudi-compliance?tab=configure',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    mocks: { '/api/saudi-compliance/dashboard': SAUDI_DASHBOARD },
    module: { path: '/saudi-compliance', goneTab: 'Configure', door: 'Saudi compliance settings' } },
  { id: 'countryPayrollKeys', label: 'Country payroll keys', group: 'Payroll & compliance', old: '/tenant-admin?tab=country-rules',
    ready: page => page.getByRole('button', { name: /Add/i }).first() },
]);

test.describe('Talent & compliance: what the move changes beyond the sections', () => {
  test('an accepted policy extraction lands in Setup → Onboarding checklists and opens the draft', async ({ page }) => {
    const { errors } = await boot(page, { url: '/setup?tab=onboardingChecklists&policyDraft=1' });
    await expect(page.locator('#setup-section-heading')).toHaveText('Onboarding checklists', { timeout: 30_000 });
    // No draft was saved for this browser, so the editor says so: proof it read policyDraft=1 from Setup's address.
    await expect(page.locator('[data-module-section="onboardingChecklists"]')
      .getByText('No policy task draft is available for this signed-in user and selected company.', { exact: false })).toBeVisible({ timeout: 15_000 });
    expect(errors, 'No application runtime errors').toEqual([]);
  });

  test('onboarding keeps generating a joiner\'s tasks and no longer offers the checklist editor', async ({ page }) => {
    await boot(page, { url: '/recruitment/onboarding', mocks: {
      '/api/recruitment/onboarding/summary': { total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 },
      '/api/recruitment/onboarding/tasks': { items: [], total: 0, page: 1, pageSize: 25 },
    } });
    await expect(page.getByRole('button', { name: 'Generate Tasks From Checklist', exact: true })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Checklist code', { exact: true })).toHaveCount(0);
  });

  test('the Saudi compliance action centre link opens the part it names, and focuses it', async ({ page }) => {
    const { errors } = await boot(page, { url: '/saudi-compliance?tab=configure&section=gosi' });
    await expect(page).toHaveURL(/\/setup\?tab=saudiCompliance&section=gosi$/);
    const part = page.locator('[data-module-section="saudiCompliance"] [data-config-section]');
    await expect(part).toHaveAttribute('data-config-section', 'gosi', { timeout: 30_000 });
    await expect(part).toBeFocused();
    expect(errors, 'No application runtime errors').toEqual([]);
  });

  test('an unknown section name opens the first part rather than failing', async ({ page }) => {
    await boot(page, { url: '/setup?tab=saudiCompliance&section=nonsense' });
    await expect(page.locator('[data-module-section="saudiCompliance"] [data-config-section]')).toHaveAttribute('data-config-section', 'qiwa', { timeout: 30_000 });
  });

  test('letters without a template send HR to Setup → Letter templates', async ({ page }) => {
    await boot(page, { url: '/hr-letters', mocks: {
      '/api/hr-letters/types': [{ type: 'SalaryCertificate', nameEn: 'Salary certificate', nameAr: 'شهادة راتب', isConfigured: false }],
    } });
    const link = page.getByRole('link', { name: 'Restore the bilingual defaults in Setup → Letter templates.', exact: true }).first();
    await expect(link).toBeVisible({ timeout: 30_000 });
    await link.click();
    await expect(page).toHaveURL(/\/setup\?tab=letterTemplates/);
  });
});
