import { expect, test, type Page } from '@playwright/test';
import type { CompanyProfile, PolicyReviewRecord, PolicyReviewRequest } from '../src/api/setupAssistant';

// Isolated UI contract proof: real rendered components, intercepted external APIs.
// These fixtures prove review/navigation behavior, not real database audit persistence or model extraction.
const companyId = '10000000-0000-0000-0000-000000000003';
const company = { id: companyId, legalNameEn: 'Policy Review Fixture LLC', countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const requirementSummary = 'Complete employee induction within 30 days';
const requirementQuote = 'HR must complete employee induction within 30 days of joining.';
const cityQuote = 'The head office is in Dammam.';
const conflictQuote = 'Probation is three months; the confirmation clause says six months.';
const conflictSummary = 'Confirm the conflicting probation period';
const policyText = `${requirementQuote}\n${cityQuote}\n${conflictQuote}`;
const documents = [1, 2].map(version => ({ id: `20000000-0000-0000-0000-00000000000${version}`, originalName: `Policy 3 version ${version}.txt`, mimeType: 'text/plain', fileSizeBytes: Buffer.byteLength(policyText), status: 'Ready', publicationStatus: 'Draft', companyId, contentSha256: String(version).repeat(64), chunkCount: 1 }));

async function boot(page: Page, options: { repeatedCoverage?: boolean; noProposals?: boolean; conflict?: boolean; onlyConflict?: boolean; singleCompany?: boolean; requirementCount?: number } = {}) {
  const writes: string[] = [];
  const reviewSaves: PolicyReviewRequest[] = [];
  const history: PolicyReviewRecord[] = [];
  const previews: CompanyProfile[] = [];
  const pageErrors: string[] = [];
  let uploads = 0;
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'policy-review-fixture'); localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (request.method() !== 'GET') writes.push(path);
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'policy-review-fixture', fullName: 'Policy Reviewer', roles: ['Admin'], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read'], accountType: options.singleCompany ? 'SingleCompany' : 'Group', isGroupScope: !options.singleCompany, companies: [{ id: companyId, name: company.legalNameEn, code: 'REVIEW', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR' });
    if (path === '/api/companies') return reply({ items: [company], total: 1 });
    if (path === '/api/ai/policy/documents/upload') return reply(documents[Math.min(uploads++, 1)]);
    if (path === '/api/ai/policy/documents') return reply(documents.slice(0, uploads));
    if (path === '/api/setup-assistant/policy/extract') {
      const body = request.postDataJSON(); const document = documents.find(doc => doc.id === body.documentId)!;
      return reply({ documentId: document.id, sourceHash: document.contentSha256, provider: 'isolated-fixture',
        proposals: options.noProposals ? [] : [...(options.onlyConflict ? [] : [{ target: 'profile', field: 'branchCity', value: 'Dammam', sourceQuote: cityQuote, sourceStart: requirementQuote.length + 1, sourceLength: cityQuote.length }]), ...(options.conflict ? [{ target: 'profile', field: 'probationMonths', value: 3, sourceQuote: conflictQuote, sourceStart: policyText.indexOf(conflictQuote), sourceLength: conflictQuote.length }] : [])],
        partialProposals: [],
        issues: options.conflict ? [{ section: 'Employment', kind: 'conflict', code: 'policy_conflict', message: conflictSummary, target: 'profile', field: 'probationMonths', sourceQuote: conflictQuote, sourceStart: policyText.indexOf(conflictQuote), sourceLength: conflictQuote.length }] : options.repeatedCoverage ? Array.from({ length: 41 }, () => ({ section: 'Benefits', kind: 'missing', message: 'Benefit grades are outside this onboarding chapter.' })) : [],
        requirements: Array.from({ length: options.requirementCount ?? 1 }, (_, index) => ({ id: `onboarding-induction-${index}`, section: 'Onboarding', summary: index ? `${requirementSummary} — clause ${index + 1}` : requirementSummary, capability: 'onboarding.orientation', status: 'ManualConfigurationRequired', sourceClassification: 'Policy', sourceQuote: requirementQuote, sourceStart: 0, sourceLength: requirementQuote.length, target: 'requirement', field: 'onboarding.orientation', missingFields: [] })),
        coverage: [{ section: 'Onboarding', status: 'Operational requirement found' }, { section: 'Benefits', status: 'Outside this chapter' }],
        message: 'Review the extracted onboarding requirements.',
        progress: { chunkIndex: 0, chunkCount: 1, sourceStart: 0, sourceLength: policyText.length, totalCharacters: policyText.length, nextChunkIndex: null, isComplete: true },
      });
    }
    if (path === '/api/setup-assistant/policy/reviews') {
      if (request.method() === 'GET') return reply(history.filter(entry => entry.documentId === new URL(request.url()).searchParams.get('documentId')));
      const body = request.postDataJSON() as PolicyReviewRequest;
      reviewSaves.push(body);
      const record: PolicyReviewRecord = { ...body, id: `review-${history.length + 1}`, reviewedByUserId: 'reviewer', reviewedAtUtc: '2026-10-13T16:00:00Z' };
      history.push(record); return reply(record);
    }
    if (path === '/api/setup-assistant/preview') {
      previews.push(request.postDataJSON());
      return reply({ configurationVersion: 1, draft: { branches: [{ code: 'HQ', nameEn: 'Head Office', city: 'Dammam', isHeadOffice: true }] }, notes: [], engine: 'Isolated fixture' });
    }
    // policy_intake is ON in this fixture: it is absent from the disabled keys (the real API lists it until the platform team enables it).
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.startsWith('/api/features/') || path === '/api/notifications') return reply([]);
    if (request.method() !== 'GET') return reply({ message: `Unexpected write ${path}` }, 500);
    return reply({ items: [], total: 0 });
  });
  await page.goto(options.singleCompany ? '/setup?tab=aiSetup&path=policyStarter' : `/setup?tab=aiSetup&path=policyStarter&companyId=${companyId}`);
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/^Industry/).fill('Services');
  return { writes, reviewSaves, history, previews, pageErrors };
}

async function extract(page: Page, replacing = false) {
  if (!replacing) await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: replacing ? documents[1].originalName : documents[0].originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Reading policy…', exact: true })).toHaveCount(0);
}

async function openEvidence(page: Page) {
  await page.getByText(/^Policy source and gaps/).click();
  return page.getByRole('region', { name: 'Policy source and gaps', exact: true });
}

test('zero mapped values prominently reports no prepared forms instead of successful setup', async ({ page }) => {
  const state = await boot(page, { noProposals: true, requirementCount: 26 });
  await extract(page);
  await expect(page.getByRole('status').filter({ hasText: 'No form values prepared' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry extraction', exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Policy extraction finished:' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Use selected suggestions|Review prefilled forms/ })).toHaveCount(0);
  const evidence = await openEvidence(page);
  await expect(evidence).toContainText(requirementSummary);
  await expect(evidence.getByRole('combobox')).toHaveCount(0);
  await expect(evidence.getByRole('textbox')).toHaveCount(0);
  await expect(evidence.getByRole('button', { name: 'Save review decision', exact: true })).toHaveCount(0);
  expect(state.reviewSaves).toEqual([]);
});

test('zero-value checkpoint remains needs-attention after reload and retry stays explicit', async ({ page }) => {
  const state = await boot(page, { noProposals: true });
  await extract(page);
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.reload();
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'No form values prepared' })).toBeVisible();
  expect(state.writes.filter(path => path === '/api/setup-assistant/policy/extract')).toHaveLength(1);
  await page.getByRole('button', { name: 'Retry extraction', exact: true }).click();
  await expect.poll(() => state.writes.filter(path => path === '/api/setup-assistant/policy/extract').length).toBe(2);
  expect(state.writes).not.toContain('/api/setup-assistant/apply');
});

test('source evidence remains read-only while a supported field can populate its existing form', async ({ page }) => {
  const state = await boot(page);
  await extract(page);
  const evidence = await openEvidence(page);
  await evidence.getByText(requirementSummary, { exact: true }).click();
  await expect(evidence).toContainText(requirementQuote);
  await expect(page.getByRole('combobox', { name: 'Review disposition', exact: true })).toHaveCount(0);
  await expect(page.getByRole('textbox', { name: 'Review comment', exact: true })).toHaveCount(0);
  await expect(page.getByText('Comment or record review', { exact: true })).toHaveCount(0);
  await page.getByRole('checkbox', { name: 'Head office city', exact: true }).check();
  await page.getByRole('button', { name: 'Use selected suggestions (1)', exact: true }).click();
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Dammam');
  expect(state.reviewSaves).toEqual([]);
  expect(state.writes).not.toContain('/api/setup-assistant/apply');
  expect(state.pageErrors).toEqual([]);
});

test('real conflict blocks only its affected field without a disposition bypass', async ({ page }) => {
  const state = await boot(page, { conflict: true });
  await extract(page);
  const evidence = await openEvidence(page);
  await evidence.getByText(conflictSummary, { exact: true }).click();
  await expect(evidence).toContainText(conflictQuote);
  await expect(page.getByRole('checkbox', { name: 'Probation (months)', exact: true })).toBeDisabled();
  await expect(page.getByRole('checkbox', { name: 'Head office city', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Save review decision', exact: true })).toHaveCount(0);
  await page.getByRole('checkbox', { name: 'Head office city', exact: true }).check();
  await page.getByRole('button', { name: 'Use selected suggestions (1)', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'Probation (months)', exact: true })).toBeDisabled();
  expect(state.reviewSaves).toEqual([]);
});

test('copied setup values remain prepared after checkpoint reload without claiming pending suggestions', async ({ page }) => {
  await boot(page);
  await extract(page);
  await page.getByRole('checkbox', { name: 'Head office city', exact: true }).check();
  await page.getByRole('button', { name: 'Use selected suggestions (1)', exact: true }).click();
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.reload();
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Reviewed policy values are already copied into your setup draft.' })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'No form values prepared' })).toHaveCount(0);
  await expect(page.getByRole('checkbox', { name: 'Head office city', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Close guide', exact: true }).click();
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Dammam');
});

test('blocked-only proposals are not counted as usable prepared values', async ({ page }) => {
  await boot(page, { conflict: true, onlyConflict: true });
  await extract(page);
  await expect(page.getByRole('status').filter({ hasText: 'No form values prepared' })).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Probation (months)', exact: true })).toBeDisabled();
});

test('repeated unrelated coverage stays compact without turning into required questions', async ({ page }) => {
  await boot(page, { repeatedCoverage: true });
  await extract(page);
  await page.getByText(/^Coverage notes — not verified policy requirements/).click();
  await expect(page.getByText('Benefits: Benefit grades are outside this onboarding chapter.', { exact: false })).toHaveCount(1);
  await expect(page.getByRole('checkbox', { name: 'Head office city', exact: true })).toBeEnabled();
  await openEvidence(page);
  await expect(page.getByRole('textbox', { name: 'Follow-up owner', exact: true })).toHaveCount(0);
});

test('existing review history is retained locally without new review writes or automatic activation', async ({ page }) => {
  const state = await boot(page);
  await extract(page);
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  const key = await page.evaluate(() => Object.keys(localStorage).find(key => key.startsWith('kynexone-setup-draft:'))!);
  // Seed after navigation: the current page legitimately saves its in-memory draft on exit.
  await page.addInitScript(key => {
    const draft = JSON.parse(localStorage.getItem(key)!);
    draft.profile.configuration.policyReview = { drafts: {}, history: [{ id: 'existing-history', companyId: draft.profile.companyId, itemKey: 'old-review', comment: 'Retain this historical decision.', disposition: 'NeedsClarification' }] };
    localStorage.setItem(key, JSON.stringify(draft));
  }, key);
  await page.reload();
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await openEvidence(page);
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  const history = await page.evaluate(key => JSON.parse(localStorage.getItem(key)!).profile.configuration.policyReview.history, key);
  expect(history).toMatchObject([{ id: 'existing-history', comment: 'Retain this historical decision.' }]);
  expect(state.reviewSaves).toEqual([]);
  expect(state.writes).not.toContain('/api/setup-assistant/apply');
});
