import { test, expect, type Page } from '@playwright/test';

// Browser contracts use only synthetic intercepted API responses. Root runs these against
// the approved 5198 frontend; this file never launches a runtime or contacts a mail relay.
const id = '11111111-1111-1111-1111-111111111111';
const design = { schemaVersion: 2, pageSize: 'A4', landscape: false, margin: 36, direction: 'ltr', fontFamily: 'Lato', headingFontFamily: 'Lato', customFont: null, customFontName: '', fontSize: 11, headingSize: 25, lineHeight: 1.45, textColor: '#253047', accentColor: '#2454D6', backgroundColor: '#FFFFFF', pageBorder: false, header: '{{company_name}}', footer: '', pageNumbers: true, logo: null, logoWidth: 110, logoAlign: 'left', signature: null, stamp: null, signatory: 'HR', signatoryTitle: '', candidateAcceptance: '', customFields: {}, blocks: [{ id: 'block', kind: 'text', text: 'Actual candidate', enabled: true, align: 'left', fontSize: null, color: null, background: null, bold: false, italic: false, underline: false, spacing: 12, image: null, imageWidth: 180, rows: [] }] };
async function boot(page: Page, options: { issued?: boolean; conflict?: boolean; locale?: string; outcome?: string } = {}) {
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  let emailCalls = 0;
  const offer = { id, applicationId: 'app', companyId: 'company', seriesId: 'series', revisionNumber: 2, version: 4, candidateName: 'Synthetic Offer Candidate', recipientEmail: 'synthetic@example.invalid', offeredJobTitle: 'Engineer', offeredDepartment: 'Engineering', startDate: '2027-01-01', responseDeadline: '2027-02-01T12:00:00Z', probationMonths: 3, basicSalary: 1000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, grossSalary: 1000, status: options.issued ? 'Approved' : 'Draft', generatedAtUtc: '2026-10-19T00:00:00Z', frozenAtUtc: options.issued ? '2026-10-19T00:00:00Z' : null };
  const snapshot = { schemaVersion: 1, currency: 'SAR', precision: 2, components: [{ code: 'BASIC', labelEn: 'Basic salary', labelAr: 'الراتب الأساسي', kind: 'Cash', amount: 1000, frequency: 'Monthly', included: true, exclusionReason: null, description: null, payComponentId: null, order: 0, required: true, structureField: 'BasicSalary' }], monthlyCash: 1000, annualRecurringCash: 12000, oneTimeCash: 0, basicSalary: 1000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, policyVersion: 0, policySource: 'CompanyDefault' };
  const historyRow = { ...offer, position: offer.offeredJobTitle, communicationOutcome: options.outcome ?? 'NotIssued', originalProvenance: options.issued ? 'FrozenOriginal' : 'NotFrozen' };
  const workspace = { offer, artifact: options.issued ? { id: 'artifact', sha256: 'hash', sizeBytes: 100, provenance: 'FrozenOriginal', offerVersion: 4 } : null, originalProvenance: historyRow.originalProvenance, approvals: [], capabilities: Object.fromEntries(['editDraft', 'freeze', 'issue', 'revise', 'withdraw', 'recordResponse', 'downloadOriginal'].map(key => [key, { allowed: options.issued ? ['issue', 'downloadOriginal'].includes(key) : ['editDraft', 'freeze', 'revise'].includes(key), reason: 'State does not allow this action.' }])), revisions: [historyRow], changedFields: [], communicationOutcome: historyRow.communicationOutcome, compensationSnapshot: snapshot, designSnapshot: { design, values: { candidate_name: offer.candidateName }, templateId: null, templateVersion: null } };
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, options.locale ?? 'en');
  await page.route('**/api/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (request.method() !== 'GET') writes.push({ path, body: request.postDataJSON() ?? {} });
    if (path === '/api/auth/me') return reply({ id: 'offer-reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Offer Reviewer', roles: ['Admin'], permissions: ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'dashboard.read'], accountType: 'SingleCompany', isGroupScope: false, companies: [{ id: 'company', name: 'Synthetic Company', code: 'QA', countryCode: 'SAU', isActive: true }] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.endsWith('/workspace')) return reply(workspace);
    if (path.endsWith('/component-options')) return reply({ policy: { companyId: 'company', currency: 'SAR', precision: 2, requiredComponentCodes: ['BASIC'], expectedVersion: 0 }, options: [], canonicalBasic: snapshot.components[0] });
    if (path.endsWith('/offer-templates/defaults')) return reply({ design, tokens: ['candidate_name'] });
    if (path.endsWith('/offer-templates')) return reply([]);
    if (path.endsWith('/draft')) { if (options.conflict) return reply({ error: 'offer_version_conflict', message: 'The offer changed. Reload before saving.' }, 409); return reply(offer); }
    if (path.endsWith('/history')) return reply({ items: [historyRow], total: 1, page: 1, pageSize: 20 });
    if (path.endsWith('/events')) return reply({ items: [], total: 0, page: 1, pageSize: 20 });
    if (path.endsWith('/mail-readiness')) return reply({ configured: false, mode: 'Capture', source: 'CaptureOnly', fromAddress: null, fromName: null, canManageSettings: true, blockedReason: null, configurationFingerprint: 'reviewed-sender', recipientRestricted: false });
    if (path.endsWith('/email')) { emailCalls++; if (emailCalls === 1) return route.abort('failed'); return reply({ id: 'dispatch', offerId: id, version: 1, outcome: 'Unknown', reservationVersion: 6, reservationState: 'Unknown', subject: 'Offer', message: 'Review the original', recipientEmail: offer.recipientEmail, recipientName: offer.candidateName, artifactId: 'artifact', initiatorUserId: 'offer-reviewer', initiatorName: 'Offer Reviewer', fromAddress: null, fromName: null, senderSource: 'CaptureOnly', senderFingerprint: 'reviewed-sender', createdAtUtc: '2026-10-19T00:00:00Z', finalizedAtUtc: null, allowedActions: ['RecordConfirmedExternalSending', 'AuthorizeRiskAcknowledgedResend'], attempts: [], transportOutcome: null }); }
    if (path.endsWith('/original') || path.endsWith('/preview')) return route.fulfill({ contentType: 'application/pdf', body: Buffer.from('%PDF-1.4\n%%EOF') });
    if (path.includes('/features/') || path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    return reply({ items: [], total: 0 });
  });
  await page.goto(`/recruitment/offers/${id}`);
  await expect(page.getByRole('heading', { name: 'Synthetic Offer Candidate' })).toBeVisible();
  return writes;
}

test('workspace respects capabilities and protects dirty edits before preview or stale recovery', async ({ page }) => {
  await boot(page, { conflict: true });
  await expect(page.getByRole('button', { name: 'Send review', exact: true })).toHaveCount(0);
  await page.getByLabel('Job title', { exact: true }).fill('Reviewed Engineer');
  await page.getByRole('tab', { name: 'Letter', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel('Job title', { exact: true })).toHaveValue('Reviewed Engineer');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('The offer changed');
  await expect(page.getByLabel('Job title', { exact: true })).toHaveValue('Reviewed Engineer');
  await page.getByRole('button', { name: 'Reload latest', exact: true }).click();
  await page.getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByLabel('Job title', { exact: true })).toHaveValue('Engineer');
});

test('network replay keeps reviewed payload, capture is labelled and unknown has reconciliation only', async ({ page }) => {
  const writes = await boot(page, { issued: true });
  await page.getByRole('button', { name: 'Send review', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Capture mode is for testing');
  await page.getByLabel('Message', { exact: true }).fill('Review the original');
  await page.getByRole('dialog').getByRole('button', { name: 'Send email', exact: true }).last().click();
  await page.getByRole('button', { name: 'Replay reviewed request', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Unknown — reconciliation required');
  expect(writes.filter(w => w.path.endsWith('/email'))[0].body).toEqual(writes.filter(w => w.path.endsWith('/email'))[1].body);
  await expect(page.getByRole('button', { name: /^Retry$/ })).toHaveCount(0);
  await expect(page.getByLabel('Reconciliation action')).toBeVisible();
});

test('390px Arabic interface preserves authored document direction and six designer tabs', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 }); await boot(page, { locale: 'ar' });
  await page.getByRole('tab', { name: 'الخطاب', exact: true }).click();
  await expect(page.getByRole('tablist', { name: 'عناصر تحكم تصميم الخطاب' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'تخطيط الصفحة', exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'تخطيط الصفحة', exact: true }).click();
  await expect(page.getByLabel('اتجاه القراءة', { exact: true })).toHaveValue('ltr');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
