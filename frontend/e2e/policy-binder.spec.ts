import { expect, test, type Page } from '@playwright/test';
import type { CompanyProfile } from '../src/api/setupAssistant';

// Intercepted browser tests: prove the intake/review contract without changing a tenant.
const company = { id: 'binder-company', legalNameEn: 'Binder Fixture LLC', countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true };
const annual = { name: 'Annual leave', leaveTypeCode: 'ANNUAL', annualEntitlementDays: 22, encashmentAllowed: false, encashmentMaxDays: 0, minimumDaysPerRequest: 1, maximumDaysPerRequest: 22, noticeRequiredDays: 14, weekendsIncluded: false, publicHolidaysIncluded: false, appliesOnProbation: false, payrollImpact: 'Full', proratePartialMonths: true, gradeCode: null, departmentCode: null, employmentType: null };
const benefit = (code: string) => ({ code, name: `${code} benefit`, planType: 'Medical', currency: 'SAR', effectiveFrom: '2026-10-13', effectiveTo: null, requiresEnrollment: true, gradeCodes: [] });
const proposal = (field: string, value: unknown, target = 'configuration', start = 0) => ({ target, field, value, sourceQuote: `${field} policy passage ${start}`, sourceStart: start, sourceLength: 20 });

async function boot(page: Page, options: { failSecondChunk?: boolean; slowSecondChunk?: boolean; manyProposals?: boolean; overtimePartial?: boolean } = {}) {
  const calls: { documentId: string; chunkIndex: number }[] = [];
  const previews: CompanyProfile[] = [];
  const errors: string[] = [];
  const uploads: number[] = [];
  const documents: Record<string, unknown>[] = [];
  let uploaded = 0; let failed = false; let slow = options.slowSecondChunk;
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'binder-fixture'); localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({ id: 'binder-reviewer', tenantId: 'binder-tenant', tenantSlug: 'binder-fixture', fullName: 'Binder Reviewer', roles: ['Admin'], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'dashboard.read', 'leave.policy_manage', 'overtime.policy_manage', 'ai.query'], accountType: 'SingleCompany', isGroupScope: false, companies: [{ id: company.id, name: company.legalNameEn, countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/companies') return reply({ items: [company], total: 1 });
    if (path === '/api/ai/policy/documents/upload') {
      uploaded++; uploads.push(request.postDataBuffer()?.length ?? 0);
      const document = { id: `doc-${uploaded}`, originalName: uploaded === 1 ? 'Complete binder.txt' : 'Benefits appendix.txt', status: 'Ready', publicationStatus: 'Draft', contentSha256: `hash-${uploaded}`, chunkCount: 2 };
      documents.push(document); return reply(document);
    }
    if (path === '/api/ai/policy/documents') return reply(documents);
    if (path === '/api/setup-assistant/policy/extract') {
      const body = request.postDataJSON(); calls.push({ documentId: body.documentId, chunkIndex: body.chunkIndex });
      const index = Number(body.chunkIndex);
      if (options.overtimePartial) return reply({ documentId: body.documentId, sourceHash: 'hash-1', provider: 'fixture', proposals: [], issues: [], coverage: [], partialProposals: [{ ...proposal('overtimePolicy', { code: 'OT', name: 'Overtime', hourlyRateBasis: 'BasicSalary', standardMonthlyHours: 208, minimumMinutes: 30, maximumMinutesPerDay: 240, monthlyCapMinutes: 3000, roundingRule: 'NearestMinute', requiresApproval: true, allowCompOffConversion: false, multipliers: [] }), missingFields: ['multipliers'] }], progress: { chunkIndex: 0, chunkCount: 1, sourceStart: 0, sourceLength: 250000, totalCharacters: 250000, nextChunkIndex: null, isComplete: true } });
      if (options.manyProposals) return reply({ documentId: body.documentId, sourceHash: 'hash-1', provider: 'fixture', proposals: [proposal('benefitPlans', Array.from({ length: 60 }, (_, number) => benefit(`PLAN${number}`)))], partialProposals: [], issues: Array.from({ length: 110 }, (_, number) => ({ section: 'Benefits', kind: 'review', message: `Question ${number}` })), coverage: [], message: 'Review all settings.', progress: { chunkIndex: 0, chunkCount: 1, sourceStart: 0, sourceLength: 250000, totalCharacters: 250000, nextChunkIndex: null, isComplete: true } });
      if (slow && index === 1) { slow = false; await new Promise(resolve => setTimeout(resolve, 1500)); }
      if (options.failSecondChunk && !failed && index === 1) { failed = true; return reply({ documentId: body.documentId, sourceHash: 'hash-1', provider: 'fixture', proposals: [], issues: [], coverage: [], message: 'Provider unavailable. Retry this section.', progress: { chunkIndex: 1, chunkCount: 2, sourceStart: 100000, sourceLength: 100000, totalCharacters: 250000, nextChunkIndex: 1, isComplete: false } }); }
      const first = body.documentId === 'doc-1';
      return reply({ documentId: body.documentId, sourceHash: first ? 'hash-1' : 'hash-2', provider: 'fixture',
        proposals: first ? [proposal('branchCity', index === 0 ? 'Dammam' : 'Jeddah', 'profile', index * 100000), proposal('benefitPlans', [benefit(index === 0 ? 'MED' : 'LIFE')], 'configuration', index * 100000 + 50)] : [proposal('leavePolicies', [{ ...annual, name: 'Sick leave', leaveTypeCode: 'SICK', annualEntitlementDays: 30, accrualMethod: 'Yearly' }])],
        partialProposals: first && index === 0 ? [{ ...proposal('leavePolicies', [annual], 'configuration', 500), missingFields: ['leavePolicies[0].accrualMethod'] }] : [],
        issues: [], coverage: [], message: 'Review extracted values.',
        progress: { chunkIndex: index, chunkCount: first ? 2 : 1, sourceStart: index * 100000, sourceLength: 100000, totalCharacters: first ? 250000 : 1000, nextChunkIndex: first && index === 0 ? 1 : null, isComplete: !first || index === 1 } });
    }
    if (path === '/api/setup-assistant/preview') {
      previews.push(request.postDataJSON());
      return reply({ configurationVersion: 1, draft: { branches: [{ code: 'HQ', nameEn: 'HQ', city: 'Jeddah', isHeadOffice: true }], policySources: previews.at(-1)?.configuration?.policySources, policyFieldSources: previews.at(-1)?.configuration?.policyFieldSources }, notes: [], engine: 'Fixture' });
    }
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/features/modules' || path === '/api/notifications' || path === '/api/statutory-rules') return reply([]);
    if (request.method() !== 'GET') throw new Error(`Unexpected write: ${path}`);
    return reply({ items: [], total: 0 });
  });
  await page.goto('/setup?tab=aiSetup&mode=starter');
  await expect(page.getByRole('heading', { name: 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60000 });
  return { calls, previews, errors, uploads };
}

async function uploadBinder(page: Page, multiple = false) {
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles([
    { name: 'Complete binder.txt', mimeType: 'text/plain', buffer: Buffer.from('Approved policy text. '.repeat(12000)) },
    ...(multiple ? [{ name: 'Benefits appendix.txt', mimeType: 'text/plain', buffer: Buffer.from('Benefits appendix') }] : []),
  ]);
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
}

test('processes a binder over 200,000 characters, merges chapters, reviews conflicts and fills incomplete policy fields', async ({ page }, info) => {
  const state = await boot(page);
  await page.getByLabel(/^Industry/).fill('Services');
  await page.getByLabel(/^Legal entity name/).fill('Binder Fixture LLC');
  await uploadBinder(page, true);
  await expect(page.getByRole('status').filter({ hasText: /^Policy extraction finished:/ })).toContainText('Nothing has been applied.');
  expect(state.uploads[0]).toBeGreaterThan(200000);
  expect(state.calls).toEqual([{ documentId: 'doc-1', chunkIndex: 0 }, { documentId: 'doc-1', chunkIndex: 1 }, { documentId: 'doc-2', chunkIndex: 0 }]);
  const partial = page.getByRole('region', { name: 'Partially extracted settings' });
  await expect(partial.getByLabel(/^Annual Entitlement Days/i)).toHaveValue('22');
  await partial.getByLabel(/^Accrual Method/i).selectOption('Monthly');
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await expect(partial.getByLabel(/^Accrual Method/i)).toHaveValue('Monthly');
  await page.getByRole('button', { name: 'Review completed suggestion', exact: true }).click();
  const cities = page.getByRole('checkbox', { name: 'Head office city', exact: true });
  await cities.nth(0).check(); await cities.nth(1).check();
  await expect(cities.nth(0)).not.toBeChecked();
  for (const checkbox of await page.getByRole('checkbox', { name: /^(Benefits|Leave policies)$/ }).all()) await checkbox.check();
  await page.getByRole('button', { name: 'Use selected suggestions (5)', exact: true }).click();
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Jeddah');
  await page.getByRole('navigation', { name: 'Company setup steps' }).getByRole('button', { name: /Review & create/ }).click();
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect.poll(() => state.previews.length).toBe(1);
  const config = state.previews[0].configuration!;
  expect(config.policySources).toHaveLength(2);
  expect(config.benefitPlans?.map(item => item.code)).toEqual(expect.arrayContaining(['MED', 'LIFE']));
  expect(config.leavePolicies?.map(item => item.leaveTypeCode)).toEqual(expect.arrayContaining(['ANNUAL', 'SICK']));
  expect(config.policyFieldSources?.some(source => source.documentId === 'doc-2')).toBe(true);
  expect(config.policyFieldSources?.some(source => source.field === 'leavePolicies' && source.documentId === 'doc-1' && source.userCompleted)).toBe(true);
  expect(config.policyExtraction).toBeUndefined();
  expect(state.errors).toEqual([]);
  await page.screenshot({ path: info.outputPath('reviewed-binder.png'), fullPage: true });
});

test('lets HR complete and correct each missing overtime category without invented rates', async ({ page }) => {
  await boot(page, { overtimePartial: true });
  await uploadBinder(page);
  const partial = page.getByRole('region', { name: 'Partially extracted settings' });
  for (const [index, category] of ['RegularDay', 'Weekend', 'PublicHoliday'].entries()) {
    await partial.getByRole('button', { name: 'Add missing entry', exact: true }).click();
    await expect(partial.getByLabel(/^Multiplier/).nth(index)).toHaveValue('');
    await partial.getByLabel(/^Day Category/).nth(index).selectOption(category);
    await partial.getByLabel(/^Multiplier/).nth(index).fill('1.5');
  }
  await expect(partial.getByRole('button', { name: 'Add missing entry', exact: true })).toBeDisabled();
  await partial.getByRole('button', { name: 'Remove entry 2', exact: true }).click();
  await expect(partial.getByRole('combobox', { name: 'Day Category', exact: true })).toHaveCount(2);
  await expect(partial.getByRole('combobox', { name: 'Day Category', exact: true }).nth(1)).toHaveValue('PublicHoliday');
  await expect(partial.getByRole('button', { name: 'Review completed suggestion', exact: true })).toBeEnabled();
});

test('keeps completed sections when retrying a provider failure after closing the guide', async ({ page }) => {
  const state = await boot(page, { failSecondChunk: true });
  await uploadBinder(page);
  await expect(page.getByRole('alert').filter({ hasText: 'Provider unavailable' })).toBeVisible();
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: /^Policy extraction finished:/ })).toContainText('Nothing has been applied.');
  expect(state.calls.map(call => call.chunkIndex)).toEqual([0, 1, 1]);
  await expect(page.getByRole('checkbox', { name: 'Benefits', exact: true })).toHaveCount(2);
  expect(state.errors).toEqual([]);
});

test('cancels in-flight processing, resumes at its section and detaches a source without deleting its document', async ({ page }) => {
  const state = await boot(page, { slowSecondChunk: true });
  await uploadBinder(page);
  await expect.poll(() => state.calls.length).toBe(2);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Extract from policy', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: /^Policy extraction finished:/ })).toContainText('Nothing has been applied.');
  expect(state.calls.map(call => call.chunkIndex)).toEqual([0, 1, 1]);
  await page.getByRole('button', { name: '1. Policy documents', exact: true }).click();
  await page.getByRole('button', { name: 'Remove Complete binder.txt', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Policy binder documents' }).getByRole('listitem')).toHaveCount(0);
  await expect(page.getByText('Source removed from this draft. The library document and reviewed form values are kept.', { exact: true })).toBeVisible();
  expect(state.errors).toEqual([]);
});

test('replaces a source and can reattach the existing library document without another upload', async ({ page }) => {
  const state = await boot(page);
  await uploadBinder(page);
  await expect(page.getByRole('status').filter({ hasText: /^Policy extraction finished:/ })).toContainText('Nothing has been applied.');
  await page.getByRole('checkbox', { name: 'Head office city', exact: true }).nth(0).check();
  await page.getByRole('button', { name: 'Use selected suggestions (1)', exact: true }).click();
  await page.getByRole('button', { name: '1. Policy documents', exact: true }).click();
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Replace Complete binder.txt', exact: true }).click();
  await (await chooser).setFiles({ name: 'Replacement.txt', mimeType: 'text/plain', buffer: Buffer.from('Replacement approved policy') });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('button', { name: '1. Policy documents', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Policy binder documents' }).getByRole('listitem')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Remove Benefits appendix.txt', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Choose from policy library', exact: true }).click();
  await page.getByRole('button', { name: 'Add to binder', exact: true }).nth(0).click();
  await expect(page.getByRole('list', { name: 'Policy binder documents' }).getByRole('listitem')).toHaveCount(2);
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Dammam');
  expect(state.uploads).toHaveLength(2);
  expect(state.errors).toEqual([]);
});

test('bounds large binder review pages while retaining choices on earlier pages', async ({ page }) => {
  await boot(page, { manyProposals: true });
  await uploadBinder(page);
  const choices = page.getByRole('checkbox', { name: 'Benefits', exact: true });
  await expect(choices).toHaveCount(25);
  await choices.first().check();
  const questions = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: 'Coverage notes — not verified policy requirements' }) });
  await questions.locator('summary').click();
  await expect(questions.getByRole('listitem')).toHaveCount(50);
  await page.getByRole('button', { name: 'Next questions', exact: true }).click();
  await page.getByRole('button', { name: 'Next questions', exact: true }).click();
  await expect(questions.getByRole('listitem')).toHaveCount(10);
  await page.getByRole('button', { name: 'Next settings', exact: true }).click();
  await expect(choices).toHaveCount(25);
  await page.getByRole('button', { name: 'Next settings', exact: true }).click();
  await expect(choices).toHaveCount(10);
  await choices.first().check();
  await expect(page.getByRole('button', { name: 'Use selected suggestions (2)', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Previous settings', exact: true }).click();
  await page.getByRole('button', { name: 'Previous settings', exact: true }).click();
  await expect(choices.first()).toBeChecked();
});
