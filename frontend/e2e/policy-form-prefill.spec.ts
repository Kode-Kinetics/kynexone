import { expect, test, type Page } from '@playwright/test';

// Real components and navigation; intercepted external APIs isolate all writes from real tenants.
const companyId = '10000000-0000-0000-0000-000000000011';
const documentId = '20000000-0000-0000-0000-000000000011';
const checklistId = '30000000-0000-0000-0000-000000000011';
const company = { id: companyId, legalNameEn: 'Policy Form Fixture LLC', countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const secondCompany = { ...company, id: '10000000-0000-0000-0000-000000000012', legalNameEn: 'Other Group Company LLC' };
const taskQuote = 'HR arranges contract signing and bank account support; additional medical if needed.';
const orientationQuote = 'HR develops orientation including health and safety and ERP training.';
const policyText = `${taskQuote}\n${orientationQuote}`;
const document = { id: documentId, originalName: 'Policy 3 fixture.txt', mimeType: 'text/plain', fileSizeBytes: Buffer.byteLength(policyText), status: 'Ready', publicationStatus: 'Draft', companyId, contentSha256: 'a'.repeat(64), chunkCount: 1 };
const drafts = [
  { id: 'contract-task', taskTitle: 'Arrange contract signing', taskDescription: 'HR arranges contract signing.', category: 'Document', assignedToName: 'HR', dueOffsetDays: null, isMandatory: true, condition: null, sourceQuote: taskQuote, sourceStart: 0, sourceLength: taskQuote.length },
  { id: 'medical-task', taskTitle: 'Arrange additional medical', taskDescription: 'Arrange additional medical if needed.', category: 'General', assignedToName: null, dueOffsetDays: null, isMandatory: null, condition: 'Only if an additional medical is needed.', sourceQuote: taskQuote, sourceStart: 0, sourceLength: taskQuote.length },
  { id: 'orientation-task', taskTitle: 'Provide orientation training', taskDescription: 'Cover health and safety and ERP training.', category: 'Training', assignedToName: 'HR', dueOffsetDays: null, isMandatory: true, condition: null, sourceQuote: orientationQuote, sourceStart: taskQuote.length + 1, sourceLength: orientationQuote.length },
];

async function boot(page: Page, options: { group?: boolean; workspace?: boolean; delayCompanies?: boolean } = {}) {
  const writes: { path: string; body: any }[] = [];
  const savedTasks: any[] = [];
  const errors: string[] = [];
  let templateReads = 0;
  let releaseCompanies = () => {};
  const companyGate = options.delayCompanies ? new Promise<void>(resolve => { releaseCompanies = resolve; }) : Promise.resolve();
  const identity = { tenantId: 'prefill-tenant', userId: 'prefill-user', readOnly: false, writerRole: 'Admin' };
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'prefill-fixture'); localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (request.method() !== 'GET') writes.push({ path, body: request.headers()['content-type']?.includes('application/json') ? request.postDataJSON() : null });
    if (path === '/api/auth/me') return reply({ id: identity.userId, tenantId: identity.tenantId, tenantSlug: identity.tenantId, fullName: 'Prefill Reviewer', roles: identity.readOnly ? ['HR Officer'] : [identity.writerRole], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'recruitment.read', ...(!identity.readOnly ? ['recruitment.write'] : [])], accountType: options.group ? 'Group' : 'SingleCompany', isGroupScope: !!options.group, companies: (options.group ? [company, secondCompany] : [company]).map(item => ({ id: item.id, name: item.legalNameEn, countryCode: 'SA', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR' });
    if (path === '/api/companies') { await companyGate; return reply({ items: options.group ? [company, secondCompany] : [company], total: options.group ? 2 : 1 }); }
    if (path === '/api/ai/policy/documents/upload') return reply(document);
    if (path === '/api/ai/policy/documents') return reply([document]);
    if (path === '/api/setup-assistant/policy/extract' && request.postDataJSON().useAi === false) return reply({ documentId, sourceHash: document.contentSha256, provider: 'local-rules', proposals: [], issues: [], coverage: [], onboardingTaskDrafts: drafts, message: 'Local mapping complete.', progress: { chunkIndex: 0, chunkCount: 1, sourceStart: 0, sourceLength: policyText.length, totalCharacters: policyText.length, nextChunkIndex: null, isComplete: true } });
    if (path === '/api/setup-assistant/policy/extract') return reply({ documentId, sourceHash: document.contentSha256, provider: 'isolated-fixture', proposals: [], partialProposals: [], requirements: [], issues: [], coverage: [], onboardingTaskDrafts: drafts, message: 'Review prepared form values.', progress: { chunkIndex: 0, chunkCount: 1, sourceStart: 0, sourceLength: policyText.length, totalCharacters: policyText.length, nextChunkIndex: null, isComplete: true } });
    if (path === '/api/setup-assistant/policy/reviews') return reply([]);
    if (path === '/api/recruitment/onboarding/checklists') return reply([{ id: checklistId, tenantId: identity.tenantId, code: 'QA-POLICY3', name: 'Existing fixture checklist', description: 'Isolated test checklist', applicableTo: 'All', departmentName: '', isActive: true }]);
    if (path === `/api/recruitment/onboarding/checklists/${checklistId}/template-tasks`) {
      if (request.method() === 'GET') { templateReads++; return reply(savedTasks); }
      const body = request.postDataJSON();
      const index = savedTasks.findIndex(task => task.taskTitle === body.taskTitle);
      const row = { ...body, id: index < 0 ? `saved-${savedTasks.length + 1}` : savedTasks[index].id, tenantId: identity.tenantId, checklistId, isActive: true, createdAtUtc: '2026-10-13T16:00:00Z' };
      if (index < 0) savedTasks.push(row); else savedTasks[index] = row;
      return reply(row);
    }
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, mandatory: 0, mandatoryCompleted: 0, completionPct: 0 });
    // policy_intake is ON in this fixture: it is absent from the disabled keys (the real API lists it until the platform team enables it).
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.startsWith('/api/features/') || path === '/api/notifications') return reply([]);
    if (request.method() !== 'GET') return reply({ message: `Unexpected write ${path}` }, 500);
    return reply({ items: [], total: 0 });
  });
  if (options.workspace) {
    await page.goto('/setup?tab=aiSetup&area=onboarding&setting=onboardingChecklists');
    // A deep link to one setting opens the focused editor: the area is its context line and the
    // setting itself is the heading.
    const focused = page.getByRole('region', { name: 'Onboarding checklists', exact: true });
    await expect(focused.getByRole('heading', { name: 'Onboarding checklists', exact: true })).toBeVisible();
    await expect(focused.getByText('Onboarding and confirmation', { exact: true })).toBeVisible();
    return { writes, savedTasks, errors, identity, releaseCompanies, get templateReads() { return templateReads; } };
  }
  await page.goto(options.group ? `/setup?tab=aiSetup&path=policyStarter&companyId=${companyId}` : '/setup?tab=aiSetup&path=policyStarter');
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible({ timeout: 30_000 });
  if (!options.group) await page.getByLabel(/^Legal entity name/).fill(company.legalNameEn);
  await page.getByLabel(/^Industry/).fill('Services');
  return { writes, savedTasks, errors, identity, releaseCompanies, get templateReads() { return templateReads; } };
}

// Catches the old wizard detour or implicit saves instead of prefilling the canonical form in place.
test('workspace prepares usable forms locally with AI unavailable and without consent', async ({ page }, info) => {
  const state = await boot(page, { workspace: true });
  await page.route('**/api/setup-assistant/policy/extract', route => route.request().postDataJSON().useAi ? route.fulfill({ status: 429, json: { message: 'Provider throttled' } }) : route.fallback());
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Locally prepared form values', exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath('local-preparation-desktop.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  const closeNavigation = page.getByRole('button', { name: 'Close navigation', exact: true });
  await expect(closeNavigation).not.toBeInViewport();
  await expect(page.getByRole('button', { name: 'Review local form values', exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath('local-preparation-mobile.png'), fullPage: true });
  expect(await page.evaluate(() => window.document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.setViewportSize({ width: 1440, height: 1000 });
  expect(state.writes.filter(write => write.path.endsWith('/policy/extract') && write.body?.useAi)).toHaveLength(0);
  await page.getByRole('button', { name: 'Improve with AI (optional)', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Provider throttled' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review local form values', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Review local form values', exact: true }).click();
  await page.getByTitle('Onboarding checklist', { exact: true }).selectOption(checklistId);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Arrange contract signing');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('');
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
});

test('workspace restores local preparation after reload without calling AI', async ({ page }) => {
  const state = await boot(page, { workspace: true });
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Locally prepared form values', exact: true })).toBeVisible();
  const calls = state.writes.filter(write => write.path.endsWith('/policy/extract')).length;
  await page.reload();
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Review local form values', exact: true })).toBeVisible();
  expect(state.writes.filter(write => write.path.endsWith('/policy/extract'))).toHaveLength(calls);
  expect(state.writes.filter(write => write.body?.useAi === true || write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
  expect(state.errors).toEqual([]);
});

test('workspace local failure is retryable and unmapped text still permits manual input', async ({ page }) => {
  const state = await boot(page, { workspace: true });
  let fail = true;
  await page.route('**/api/setup-assistant/policy/extract', route => route.fulfill(fail
    ? { status: 503, json: { message: 'Local preparation temporarily unavailable' } }
    : { json: { documentId, sourceHash: document.contentSha256, provider: 'local-rules', proposals: [], issues: [], coverage: [], onboardingTaskDrafts: [] } }));
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Local preparation failed. Your document is retained. Retry or enter tasks manually.' })).toBeVisible();
  fail = false;
  await page.getByRole('button', { name: 'Retry local preparation', exact: true }).click();
  await expect(page.getByText('No supported onboarding tasks were mapped locally. Enter tasks manually or optionally use AI; no policy values have been guessed.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review local form values', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Enter manually', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('Manual review task');
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
  expect(state.errors).toEqual([]);
});

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
test(`workspace policy intake reviews and saves in the existing onboarding form without leaving the area (${viewport.width})`, async ({ page }, info) => {
  await page.setViewportSize(viewport);
  const state = await boot(page, { workspace: true });
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('button', { name: 'Improve with AI (optional)', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await page.getByRole('button', { name: 'Review prefilled forms', exact: true }).click();
  await expect(page).toHaveURL(url => url.pathname === '/setup' && url.searchParams.get('area') === 'onboarding' && url.searchParams.get('setting') === 'onboardingChecklists' && !url.searchParams.has('mode'));
  await page.getByTitle('Onboarding checklist', { exact: true }).selectOption(checklistId);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Arrange contract signing');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('');
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
  await page.getByTitle('Due offset days', { exact: true }).fill('2');
  await page.getByRole('button', { name: 'Save Template Task', exact: true }).click();
  await expect(page.getByText('1. Arrange contract signing', { exact: true })).toBeVisible();
  expect(state.savedTasks[0]).toMatchObject({ taskTitle: 'Arrange contract signing', dueOffsetDays: 2 });
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(1);
  expect(state.errors).toEqual([]);
  await page.screenshot({ path: info.outputPath('onboarding-in-area.png'), fullPage: true });
});
}

test('workspace delayed company details do not erase manual task input', async ({ page }) => {
  const state = await boot(page, { workspace: true, delayCompanies: true });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Keep my manual task');
  state.releaseCompanies();
  await expect(page.getByRole('button', { name: 'Fill from policy', exact: true })).toBeEnabled();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Keep my manual task');
  expect(state.writes).toHaveLength(0);
});

test('workspace manual edits survive switching to policy intake and back without a write', async ({ page }) => {
  const state = await boot(page, { workspace: true });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Unsaved manual task');
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByRole('button', { name: 'Enter manually', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Unsaved manual task');
  expect(state.writes).toHaveLength(0);
});

test('workspace removed sources cannot reuse an old extraction and remain removed after reload', async ({ page }) => {
  const state = await boot(page, { workspace: true });
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('button', { name: 'Improve with AI (optional)', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Review prefilled forms', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '1. Policy documents', exact: true }).click();
  await page.getByRole('button', { name: `Remove ${document.originalName}`, exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Review prefilled forms', exact: true })).toHaveCount(0);
  await page.reload();
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Policy binder documents' }).locator('li')).toHaveCount(0);
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
});

test('workspace upload failure stays recoverable in place and never writes checklist records', async ({ page }) => {
  const state = await boot(page, { workspace: true });
  await page.route('**/api/ai/policy/documents/upload', route => route.fulfill({ status: 503, json: { message: 'Upload temporarily unavailable' } }));
  await page.getByRole('button', { name: 'Fill from policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Upload failed. Retry the remaining files.' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload and review', exact: true })).toBeEnabled();
  await expect(page).not.toHaveURL(/mode=starter/);
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
});

async function extractAndOpen(page: Page) {
  await page.getByRole('button', { name: 'Add your HR policy', exact: true }).click();
  await page.getByLabel('Upload a document for extraction', { exact: true }).setInputFiles({ name: document.originalName, mimeType: 'text/plain', buffer: Buffer.from(policyText) });
  await page.getByRole('button', { name: 'Upload and review', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Allow the configured AI service to read this document and propose setup settings.', exact: true }).check();
  await page.getByRole('button', { name: 'Extract from policy', exact: true }).click();
  await page.getByRole('button', { name: 'Review prefilled forms', exact: true }).click();
  await expect(page).toHaveURL(/\/setup\?tab=onboardingChecklists&policyDraft=1/); // Drafts open in Setup → Onboarding checklists (2026-10-24).
  await expect(page.getByRole('region', { name: 'Policy task drafts', exact: true })).toBeVisible();
  await page.getByTitle('Onboarding checklist', { exact: true }).selectOption(checklistId);
}

// Catches a handoff that stays as a questionnaire or writes records while merely opening a draft.
test('resumed extraction opens existing forms without another AI call despite reordered checkpoint keys', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.evaluate(() => {
    const key = Object.keys(localStorage).find(key => key.startsWith('kynexone-setup-draft:'))!;
    const draft = JSON.parse(localStorage.getItem(key)!);
    const reorder = (v: any): any => Array.isArray(v) ? v.map(reorder) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).reverse().map(([k, value]) => [k, reorder(value)])) : v;
    draft.profile.configuration.policyExtraction.setupSignature = JSON.stringify(reorder(JSON.parse(draft.profile.configuration.policyExtraction.setupSignature)));
    localStorage.setItem(key, JSON.stringify(draft));
  });
  await page.goto('/setup?tab=aiSetup&path=policyStarter');
  await page.getByRole('button', { name: 'Review policy text', exact: true }).click();
  await page.getByRole('button', { name: '2. Prepare form values', exact: true }).click();
  await page.getByRole('button', { name: 'Review prefilled forms', exact: true }).click();
  await expect(page).toHaveURL(/\/setup\?tab=onboardingChecklists&policyDraft=1/); // Drafts open in Setup → Onboarding checklists (2026-10-24).
  expect(state.writes.filter(write => write.path.endsWith('/policy/extract'))).toHaveLength(1);
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toHaveLength(0);
});

test('policy extraction opens actual existing task controls without saving or inventing a deadline', async ({ page }, info) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Arrange contract signing');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Save Template Task', exact: true })).toBeDisabled();
  expect(state.writes.map(write => write.path)).toEqual(['/api/ai/policy/documents/upload', '/api/setup-assistant/policy/extract']);
  expect(state.errors).toEqual([]);
  await page.screenshot({ path: info.outputPath('existing-form-prefill.png'), fullPage: true });
});

// Catches replacing the existing manual endpoint with an automatic or non-persistent apply action.
test('explicit manual save uses existing template endpoint and reads saved values back', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  // Setup → Onboarding checklists opens for Admin and HR Manager, the roles the template endpoint accepts, so a
  // different writer than the Admin who extracted is an HR Manager (a custom role is refused by the API and the section).
  state.identity.writerRole = 'HR Manager';
  await page.reload();
  await page.getByTitle('Onboarding checklist', { exact: true }).selectOption(checklistId);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await page.getByTitle('Due offset days', { exact: true }).fill('2');
  const readsBeforeSave = state.templateReads;
  await page.getByRole('button', { name: 'Save Template Task', exact: true }).click();
  await expect.poll(() => state.templateReads).toBeGreaterThan(readsBeforeSave);
  await expect(page.getByText('1. Arrange contract signing', { exact: true })).toBeVisible();
  const businessWrites = state.writes.filter(write => write.path.startsWith('/api/recruitment/'));
  expect(businessWrites).toHaveLength(1);
  expect(businessWrites[0]).toMatchObject({ path: `/api/recruitment/onboarding/checklists/${checklistId}/template-tasks`, body: { taskTitle: 'Arrange contract signing', taskDescription: 'HR arranges contract signing.', category: 'Document', dueOffsetDays: 2, isMandatory: true } });
  expect(state.writes.some(write => write.path.endsWith('/tasks/bulk') || write.path.endsWith('/apply'))).toBe(false);
  await page.reload();
  await expect(page.getByRole('region', { name: 'Policy task drafts', exact: true })).toBeVisible();
  await page.getByTitle('Onboarding checklist', { exact: true }).selectOption(checklistId);
  await expect(page.getByText('1. Arrange contract signing', { exact: true })).toBeVisible();
  expect(state.errors).toEqual([]);
});

// Catches applying a conditional medical duty to every employee without an explicit HR choice.
test('conditional source is visible and blocks save until applicability is confirmed', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByRole('combobox', { name: 'Policy task draft', exact: true }).selectOption('medical-task');
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByText('Only if an additional medical is needed.', { exact: true })).toBeVisible();
  await page.locator('summary').filter({ hasText: 'Policy source' }).click();
  await expect(page.getByText(taskQuote, { exact: true })).toBeVisible();
  await expect(page.locator('summary').filter({ hasText: 'Policy source' })).toContainText(document.originalName);
  await page.getByTitle('Due offset days', { exact: true }).fill('1');
  await page.getByTitle('Mandatory task', { exact: true }).selectOption('Optional');
  const save = page.getByRole('button', { name: 'Save Template Task', exact: true });
  await expect(save).toBeDisabled();
  await page.getByRole('checkbox', { name: 'I confirm this task applies to the selected checklist; conditions are not enforced automatically.', exact: true }).check();
  await expect(save).toBeEnabled();
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
});

// Catches a same-title existing task being overwritten without review, or losing an assigned user.
test('matching existing task keeps an unstated deadline and requires explicit update confirmation', async ({ page }) => {
  const state = await boot(page);
  state.savedTasks.push({ id: 'existing-task', tenantId: 'prefill-tenant', checklistId, taskTitle: 'Arrange contract signing', taskDescription: 'Current contract procedure', category: 'Document', assignedToName: 'HR', assignedToUserId: '40000000-0000-0000-0000-000000000011', dueOffsetDays: 7, orderIndex: 3, isMandatory: false, isActive: true });
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('7');
  await expect(page.getByText(/Current: Current contract procedure/)).toBeVisible();
  const save = page.getByRole('button', { name: 'Save Template Task', exact: true });
  await expect(save).toBeDisabled();
  await page.getByRole('checkbox', { name: 'I reviewed the existing task values and want to update this matching task.', exact: true }).check();
  await save.click();
  await expect(page.getByRole('status').filter({ hasText: 'Template task saved and verified.' })).toBeVisible();
  const written = state.writes.find(write => write.path.endsWith('/template-tasks'));
  expect(written?.body).toMatchObject({ dueOffsetDays: 7, assignedToUserId: '40000000-0000-0000-0000-000000000011' });
  expect(state.savedTasks).toHaveLength(1);
  expect(state.savedTasks[0].id).toBe('existing-task');
});

// Catches losing an edited policy task when selecting another draft or resuming the route.
test('dirty per-task edits survive switching drafts and reloading', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await page.getByPlaceholder('Task title', { exact: true }).fill('HR-reviewed contract signing');
  await page.getByTitle('Due offset days', { exact: true }).fill('4');
  const selector = page.getByRole('combobox', { name: 'Policy task draft', exact: true });
  await selector.selectOption('orientation-task');
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Provide orientation training');
  await selector.selectOption('contract-task');
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('HR-reviewed contract signing');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('4');
  await page.reload();
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('HR-reviewed contract signing');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('4');
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
});

// Catches policy prefill silently replacing HR's unrelated unsaved manual task.
test('declining replacement preserves a dirty manual task form', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByPlaceholder('Task title', { exact: true }).fill('Unrelated manual task');
  await page.getByTitle('Due offset days', { exact: true }).fill('9');
  let confirmation = '';
  page.once('dialog', async dialog => { confirmation = dialog.message(); await dialog.dismiss(); });
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  expect(confirmation).toContain('unsaved values');
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Unrelated manual task');
  await expect(page.getByTitle('Due offset days', { exact: true })).toHaveValue('9');
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
});

// Catches a browser-storage quota failure silently dropping edits on the next draft switch.
test('local storage failure does not lose the visible edited task when switching drafts', async ({ page }) => {
  await boot(page);
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await page.evaluate(() => {
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = function (key: string, value: string) {
      if (key.startsWith('kynexone-policy-form-draft:')) throw new DOMException('Test quota exceeded', 'QuotaExceededError');
      return original.call(this, key, value);
    };
  });
  await page.getByPlaceholder('Task title', { exact: true }).fill('Unsaved in browser storage, retained in form');
  const selector = page.getByRole('combobox', { name: 'Policy task draft', exact: true });
  await selector.selectOption('orientation-task');
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await selector.selectOption('contract-task');
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Unsaved in browser storage, retained in form');
});

for (const boundary of ['tenantId', 'userId'] as const) {
  // Catches leaking browser-retained policy drafts into another signed-in identity.
  test(`bridge drafts do not appear after the ${boundary} changes`, async ({ page }) => {
    const state = await boot(page);
    await extractAndOpen(page);
    state.identity[boundary] = `different-${boundary}`;
    await page.reload();
    // The draft's home is Setup → Onboarding checklists: it opens for the new identity, finds no draft of theirs, and says so.
    await expect(page.locator('#setup-section-heading')).toHaveText('Onboarding checklists', { timeout: 30_000 });
    await expect(page.getByRole('status').filter({ hasText: 'No policy task draft is available' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Policy task drafts', exact: true })).toHaveCount(0);
    expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
  });
}

// Catches preserving write controls after the same user's write authorization is removed.
test('read-only recruitment permission cannot save a populated policy draft', async ({ page }) => {
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await page.getByTitle('Due offset days', { exact: true }).fill('2');
  state.identity.readOnly = true;
  await page.reload();
  // A read-only recruitment user (HR Officer) is not offered Setup → Onboarding checklists at all (it needs recruitment.write and
  // Admin or HR Manager, as the API does): no editor, no populated draft and no save control are reachable, and nothing is written.
  await expect(page.locator('[data-module-section="onboardingChecklists"]')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Setup & Administration', exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('region', { name: 'Policy task drafts', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Fill existing task form', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Save Template Task', exact: true })).toHaveCount(0);
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
});

test('existing policy-prefilled controls remain usable at mobile width', async ({ page }, info) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const state = await boot(page);
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  const title = page.getByPlaceholder('Task title', { exact: true });
  await expect(title).toHaveValue('Arrange contract signing');
  await title.fill('Mobile-reviewed signing');
  await page.getByTitle('Due offset days', { exact: true }).fill('3');
  await expect(page.getByRole('button', { name: 'Save Template Task', exact: true })).toBeEnabled();
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
  expect(state.errors).toEqual([]);
  await title.scrollIntoViewIfNeeded();
  await page.screenshot({ path: info.outputPath('mobile-existing-form-prefill.png'), fullPage: true });
});

// Catches a company switch stripping the policy guard while leaving its filled values savable as manual input.
test('Group company switch clears old policy values without converting them into a savable manual task', async ({ page }) => {
  const state = await boot(page, { group: true });
  await extractAndOpen(page);
  await page.getByRole('button', { name: 'Fill existing task form', exact: true }).click();
  await page.getByTitle('Due offset days', { exact: true }).fill('2');
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('Arrange contract signing');
  await expect(page.getByRole('button', { name: 'Save Template Task', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Switch company', exact: true }).click();
  await page.getByRole('menuitem', { name: /Other Group Company LLC/ }).click();
  // Setup guards unsaved edits: the filled form is unsaved, so the switch asks before it discards them.
  await page.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Policy task drafts', exact: true })).toHaveCount(0);
  await expect(page.getByPlaceholder('Task title', { exact: true })).toHaveValue('');
  await expect(page.getByPlaceholder('Owner', { exact: true })).toHaveValue('');
  await expect(page.getByPlaceholder('Description', { exact: true }).last()).toHaveValue('');
  const save = page.getByRole('button', { name: 'Save Template Task', exact: true });
  if (await save.isEnabled()) await save.click();
  expect(state.writes.filter(write => write.path.startsWith('/api/recruitment/'))).toEqual([]);
  await expect(page.getByRole('status').filter({ hasText: 'No policy task draft is available' })).toBeVisible();
  expect(state.errors).toEqual([]);
});
