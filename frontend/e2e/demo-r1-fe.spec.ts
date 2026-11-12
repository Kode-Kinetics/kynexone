import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { bootHire } from './hire/hireMocks';

/**
 * Demo round 1 front-end fixes, against route mocks, in English and Arabic:
 *  F5  the hire success readiness list reads "Salary package waiting for Finance approval" in Arabic;
 *  F6  a monthly-accrual leave balance (entitled 0) never reads "16.25 of 0 days available";
 *  F7  an offer's response deadline reads as a date, not as raw "2026-12-15T00:00:00Z";
 *  F10 the external-sending Channel is a choice of the three the API accepts.
 * (F1, Benefits by grade, is in benefits-by-grade-matrix.spec.ts.)
 *
 * Run: R1FE_PORT=5186 npx playwright test -c e2e/playwright.demo-r1-fe.config.ts   (R1FE_SHOTS=<dir> keeps screenshots)
 */

async function shot(page: Page, name: string) {
  const dir = process.env.R1FE_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`), fullPage: true });
}

const SALARY_PACKAGE_LABEL = 'Salary package waiting for Finance approval — it must be approved by a different person before this employee can be activated';
const L = {
  en: { name: 'English full name', start: 'Start date', desig: 'Designation', next: 'Next', create: 'Create employee draft' },
  ar: { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', next: 'التالي', create: 'إنشاء مسودة الموظف' },
} as const;

for (const locale of ['en', 'ar'] as const) {
  test.describe(`demo round 1 (${locale})`, () => {
    test(`F5: the hire success readiness list is in ${locale}, the salary-package blocker included`, async ({ page }) => {
      const l = L[locale];
      const review = {
        blockers: [{ key: 'SalaryPackageApproval', label: SALARY_PACKAGE_LABEL }, { key: 'ReadinessPolicy', label: 'No readiness policy applies: the employee\'s country is not set' }],
        warnings: [], notProvided: [],
      };
      await bootHire(page, { locale, review });
      await page.getByLabel(l.name).fill('Alex Morgan');
      await page.getByLabel(l.start).fill('2026-11-01');
      await page.getByLabel(l.desig).selectOption({ label: 'Specialist' });
      for (let i = 0; i < 3; i++) await page.getByRole('button', { name: l.next, exact: true }).click();
      await page.getByRole('button', { name: l.create }).click();
      const ready = page.getByTestId('readiness');
      await expect(ready).toBeVisible();
      if (locale === 'ar') {
        await expect(ready).toContainText('حزمة الراتب بانتظار موافقة المالية');
        await expect(ready).not.toContainText(/Salary package|No readiness policy/);
      } else {
        await expect(ready).toContainText(SALARY_PACKAGE_LABEL);
      }
      await shot(page, `f5-hire-readiness-${locale}`);
    });

    test('F6: a monthly-accrual balance has no "of 0" denominator', async ({ page }) => {
      await page.addInitScript(l => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', l); }, locale);
      await page.route('**/api/**', async route => {
        const req = route.request(); const p = new URL(req.url()).pathname;
        const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
        if (p === '/api/auth/me') return json({ id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: ['Employee'], permissions: ['ess.read', 'ess.write'], companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
        if (p === '/api/features/disabled-keys') return json(['release_a']);
        if (p === '/api/features/modules') return json([]);
        if (p === '/api/leave/types') return json([{ id: 'lt-annual', code: 'ANNUAL', nameEn: 'Annual Leave', nameAr: 'إجازة سنوية', category: 'Annual', isActive: true, isPaid: true }]);
        if (p === '/api/ess/leave/balance') return json([
          { leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave', entitled: 0, used: 3, pending: 0, available: 16.25 },
          { leaveTypeId: 'lt-sick', leaveTypeName: 'Sick Leave', entitled: 30, used: 2, pending: 0, available: 28 },
        ]);
        if (p === '/api/leave/requests' && req.method() === 'GET') return json({ items: [], total: 0, page: 1 });
        if (p === '/api/notifications') return json([]);
        return json({ items: [], total: 0 });
      });
      await page.goto('/ess/leave');
      const card = page.getByTestId('my-leave-balances');
      await expect(card).toBeVisible({ timeout: 60_000 });
      await expect(card).not.toContainText(/of 0 |من 0 /);
      await expect(card).toContainText(locale === 'ar' ? '16.25 يوماً متاحة' : '16.25 days available');
      await expect(card).toContainText(locale === 'ar' ? '28 من 30 يوماً متاحة' : '28 of 30 days available');
      await shot(page, `f6-ess-leave-balance-${locale}`);
    });

    test('F7 + F10: the deadline reads as a date, and the external channel is one of three', async ({ page }) => {
      const id = '11111111-1111-1111-1111-111111111111';
      const writes: { path: string; body: Record<string, any> }[] = [];
      const offer = { id, applicationId: 'app', companyId: 'company', seriesId: 'series', revisionNumber: 2, version: 4, candidateName: 'Synthetic Offer Candidate', recipientEmail: 'synthetic@example.invalid', offeredJobTitle: 'Engineer', offeredDepartment: 'Engineering', startDate: '2027-01-01', responseDeadline: '2026-12-15T00:00:00Z', probationMonths: 3, basicSalary: 1000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, grossSalary: 1000, status: 'Approved', generatedAtUtc: '2026-10-19T00:00:00Z', frozenAtUtc: '2026-10-19T00:00:00Z' };
      const snapshot = { schemaVersion: 1, currency: 'SAR', precision: 2, components: [{ code: 'BASIC', labelEn: 'Basic salary', labelAr: 'الراتب الأساسي', kind: 'Cash', amount: 1000, frequency: 'Monthly', included: true, exclusionReason: null, description: null, payComponentId: null, order: 0, required: true, structureField: 'BasicSalary' }], monthlyCash: 1000, annualRecurringCash: 12000, oneTimeCash: 0, basicSalary: 1000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, policyVersion: 0, policySource: 'CompanyDefault' };
      const design = { schemaVersion: 2, pageSize: 'A4', landscape: false, margin: 36, direction: 'ltr', fontFamily: 'Lato', headingFontFamily: 'Lato', customFont: null, customFontName: '', fontSize: 11, headingSize: 25, lineHeight: 1.45, textColor: '#253047', accentColor: '#2454D6', backgroundColor: '#FFFFFF', pageBorder: false, header: '{{company_name}}', footer: '', pageNumbers: true, logo: null, logoWidth: 110, logoAlign: 'left', signature: null, stamp: null, signatory: 'HR', signatoryTitle: '', candidateAcceptance: '', customFields: {}, blocks: [] };
      const historyRow = { ...offer, position: offer.offeredJobTitle, communicationOutcome: 'NotIssued', originalProvenance: 'FrozenOriginal' };
      const workspace = { offer, artifact: { id: 'artifact', sha256: 'hash', sizeBytes: 100, provenance: 'FrozenOriginal', offerVersion: 4 }, originalProvenance: 'FrozenOriginal', approvals: [], capabilities: Object.fromEntries(['editDraft', 'freeze', 'issue', 'revise', 'withdraw', 'recordResponse', 'downloadOriginal'].map(key => [key, { allowed: ['issue', 'downloadOriginal'].includes(key), reason: 'State does not allow this action.' }])), revisions: [historyRow], changedFields: [], communicationOutcome: 'NotIssued', compensationSnapshot: snapshot, designSnapshot: { design, values: { candidate_name: offer.candidateName }, templateId: null, templateVersion: null } };
      await page.addInitScript(l => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', l); }, locale);
      await page.route('**/api/**', async route => {
        const request = route.request(), p = new URL(request.url()).pathname;
        const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
        if (request.method() !== 'GET') writes.push({ path: p, body: request.postDataJSON() ?? {} });
        if (p === '/api/auth/me') return reply({ id: 'offer-reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Offer Reviewer', roles: ['Admin'], permissions: ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'dashboard.read'], companies: [] });
        if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
        if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: locale, defaultTimezone: 'Asia/Riyadh', stated: true });
        if (p === '/api/features/disabled-keys') return reply(['release_a']);
        if (p.endsWith('/workspace')) return reply(workspace);
        if (p.endsWith('/component-options')) return reply({ policy: { companyId: 'company', currency: 'SAR', precision: 2, requiredComponentCodes: ['BASIC'], expectedVersion: 0 }, options: [], canonicalBasic: snapshot.components[0] });
        if (p.endsWith('/offer-templates/defaults')) return reply({ design, tokens: ['candidate_name'] });
        if (p.endsWith('/offer-templates')) return reply([]);
        if (p.endsWith('/history')) return reply({ items: [historyRow], total: 1, page: 1, pageSize: 20 });
        if (p.endsWith('/events')) return reply({ items: [], total: 0, page: 1, pageSize: 20 });
        if (p.endsWith('/mail-readiness')) return reply({ configured: false, mode: 'Capture', source: 'CaptureOnly', fromAddress: null, fromName: null, canManageSettings: true, blockedReason: null, configurationFingerprint: 'reviewed-sender', recipientRestricted: false });
        if (p.endsWith('/external-sends')) return reply({}, 200);
        if (p.includes('/features/') || p === '/api/notifications' || p.startsWith('/api/ai/policy/')) return reply([]);
        return reply({ items: [], total: 0 });
      });
      await page.goto(`/recruitment/offers/${id}`);
      await expect(page.getByRole('heading', { name: 'Synthetic Offer Candidate' })).toBeVisible();
      await page.getByRole('button', { name: locale === 'ar' ? 'مراجعة الإرسال' : 'Send review', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      // F7: the deadline is a date in the app's own format, never the ISO text.
      await expect(dialog).not.toContainText('2026-12-15T00:00:00Z');
      await expect(dialog).toContainText(locale === 'ar' ? /١٥|15/ : /15 Dec/);
      await shot(page, `f7-send-dialog-deadline-${locale}`);
      // F10: three choices, and the one picked is what is sent.
      await dialog.getByRole('button', { name: locale === 'ar' ? 'تسجيل إرسال خارجي' : 'Record external sending', exact: true }).click();
      const channel = dialog.getByLabel(locale === 'ar' ? 'القناة / المصدر' : 'Channel / source');
      await expect(channel.locator('option')).toHaveCount(4); // the prompt and the three channels
      await expect(channel.locator('option[value]:not([value=""])')).toHaveText(locale === 'ar' ? ['البريد الإلكتروني', 'حضورياً', 'أخرى'] : ['Email', 'In person', 'Other']);
      await channel.selectOption('InPerson');
      await shot(page, `f10-external-channel-${locale}`);
      await dialog.getByLabel(locale === 'ar' ? 'وقت الحدث' : 'Occurred at').fill('2026-10-19T12:00');
      await dialog.getByLabel(locale === 'ar' ? 'مرجع الدليل' : 'Evidence reference').fill('Handed over at the office');
      await dialog.getByLabel(locale === 'ar' ? 'السبب' : 'Reason', { exact: true }).fill('Handed over in person');
      await dialog.getByRole('button', { name: locale === 'ar' ? 'تسجيل إرسال خارجي' : 'Record external sending', exact: true }).last().click();
      await expect.poll(() => writes.filter(w => w.path.endsWith('/external-sends')).length).toBe(1);
      expect(writes.find(w => w.path.endsWith('/external-sends'))!.body.channel).toBe('InPerson');
    });
  });
}
