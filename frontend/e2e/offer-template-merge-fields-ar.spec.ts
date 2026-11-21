import { expect, test } from '@playwright/test';

/**
 * Offer letter designer in Arabic (fixture lane): the merge-field picker named the server's clause fields in English
 * ("probation period", "work location", "contract term clause"). Synthetic intercepted API responses only.
 */
const design = { schemaVersion: 2, pageSize: 'A4', landscape: false, margin: 36, direction: 'ltr', fontFamily: 'Lato', headingFontFamily: 'Lato', customFont: null, customFontName: '', fontSize: 11, headingSize: 25, lineHeight: 1.45, textColor: '#253047', accentColor: '#2454D6', backgroundColor: '#FFFFFF', pageBorder: false, header: '{{company_name}}', footer: '', pageNumbers: true, logo: null, logoWidth: 110, logoAlign: 'left', signature: null, stamp: null, signatory: 'HR', signatoryTitle: '', candidateAcceptance: '', customFields: {}, blocks: [{ id: 'block', kind: 'text', text: 'Actual candidate', enabled: true, align: 'left', fontSize: null, color: null, background: null, bold: false, italic: false, underline: false, spacing: 12, image: null, imageWidth: 180, rows: [] }] };
const TOKENS = ['candidate_name', 'probation_period', 'work_location', 'contract_term_clause', 'notice_period_employer', 'notice_period_employee',
  'gosi_clause', 'health_residency_clause', 'compensation_lines', 'probation_clause'];

test('the offer designer names every merge field in Arabic', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', 'ar'); });
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json, status: 200 });
    if (path === '/api/auth/me') return reply({ id: 'u', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Offer Reviewer', roles: ['HR Manager'], permissions: ['recruitment.read', 'recruitment.write'], companies: [], isGroupScope: false });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'ar' });
    if (path === '/api/features/disabled-keys' || path === '/api/notifications' || path.includes('/features/')) return reply([]);
    if (path.endsWith('/offer-templates/defaults')) return reply({ design, tokens: TOKENS });
    if (path.endsWith('/offer-templates')) return reply([]);
    if (path.endsWith('/preview')) return route.fulfill({ contentType: 'application/pdf', body: Buffer.from('%PDF-1.4\n%%EOF') });
    return reply({ items: [], total: 0 });
  });
  await page.goto('/recruitment/offer-templates');
  const picker = page.getByLabel('إدراج حقل عرض عند المؤشر', { exact: true });
  await expect(picker).toBeVisible({ timeout: 45_000 });
  const options = (await picker.locator('option').allTextContents()).map((o) => o.trim());
  for (const arabic of ['مدة التجربة', 'موقع العمل', 'بند مدة العقد', 'مدة إشعار صاحب العمل', 'مدة إشعار الموظف', 'بند GOSI', 'بند التأمين الصحي والإقامة', 'بنود الراتب والبدلات', 'بند فترة التجربة']) {
    expect(options, arabic).toContain(arabic);
  }
  expect(options.filter((o) => /[A-Za-z]{4,}/.test(o.replace(/GOSI/g, '')))).toEqual([]);
  if (process.env.AR_SHOTS) await page.screenshot({ path: `${process.env.AR_SHOTS}/offer-templates-ar.png`, fullPage: true });
  expect(errors).toEqual([]);
});
