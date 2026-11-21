import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * Release A — the employee's own package (My package, /ess/package), fixture lane (e2e/playwright.fixture.config.ts).
 * Air ticket, medical class, education allowance and per diem are recorded and shown but nothing pays, claims or tracks
 * them yet: each such line says so in one sentence, in English and Arabic; a line that is paid in salary does not.
 * The payload follows EssPackageController's view (one line per benefit, grouped pay / contract / facility).
 *
 * Screenshots: set MY_PACKAGE_EVIDENCE_DIR.
 */

const EVIDENCE = process.env.MY_PACKAGE_EVIDENCE_DIR;
const NOW = new Date('2026-10-06T07:00:00Z');
const COMPANY = { id: 'company-1', name: 'Masar Facility Services', code: 'MFS', countryCode: 'SA', isActive: true };
const EMPLOYEE = {
  id: 'u-emp', tenantId: 't1', tenantSlug: 'masar', email: 'mohammed@masar.test', fullName: 'Mohammed Abdelrahman', employeeId: 1,
  roles: ['Employee'], accountType: 'Tenant', isGroupScope: false, companies: [COMPANY], permissions: ['ess.read', 'ess.write'],
};

type Lang = 'en' | 'ar';
const say = (lang: Lang, key: string) => (LOCALE_DICTS[lang] as Record<string, string>)[key] ?? key;
const NOTE = 'Recorded entitlement. Payout and claims for this benefit are not connected yet.';

const why = (basis: 'salary' | 'contract' | 'grade' | 'policy') => ({ basis, gradeName: 'G3', gradeNameAr: 'الدرجة 3', companyRule: false, since: '2026-02-01', until: null });
const line = (componentCode: string, labelEn: string, labelAr: string, group: 'pay' | 'contract' | 'facility', extra: Record<string, unknown>) => ({
  componentCode, labelEn, labelAr, group, eligible: true, offered: true, fixed: group === 'contract', valueType: 'Amount', amount: null, rate: null,
  monthlyCash: null, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null, dependantsCovered: 0, limitPeriod: null,
  reasonCode: null, reasonCriterion: null, eligibleFrom: null, resolvedAmount: null, why: why(group === 'pay' ? 'salary' : group === 'contract' ? 'contract' : 'grade'),
  ...extra,
});
const PACKAGE = {
  asOf: '2026-10-06', currency: 'SAR', gradeName: 'G3', gradeNameAr: 'الدرجة 3', companyName: COMPANY.name, companyNameAr: 'مسار لخدمات المرافق',
  termStartsOn: '2026-02-01', fixedUntil: '2027-01-31', renewalReviewOpensOn: null, dependantsOnFile: 0,
  lines: [
    line('HOUSING', 'Housing allowance', 'بدل السكن', 'pay', { amount: 2000, monthlyCash: 2000, resolvedAmount: 2000 }),
    line('TRANSPORT', 'Transport allowance', 'بدل النقل', 'pay', { amount: 800, monthlyCash: 800, resolvedAmount: 800 }),
    line('AIR_TICKET', 'Air ticket', 'تذكرة طيران', 'contract', { valueType: 'Quantity', quantity: 1, limitPeriod: 'ContractYear' }),
    line('MEDICAL', 'Medical insurance', 'التأمين الطبي', 'contract', { valueType: 'CoverageTier', coverageTier: 'CchiBasic' }),
    line('EDUCATION', 'Education allowance', 'بدل التعليم', 'contract', { amount: 10000, limitPeriod: 'Annual', resolvedAmount: 10000 }),
    line('PER_DIEM', 'Per diem', 'بدل يومي', 'facility', { amount: 300, resolvedAmount: 300 }),
  ],
};

async function boot(page: Page, lang: Lang) {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.clock.setFixedTime(NOW);
  await page.addInitScript((l) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', l);
  }, lang);
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(EMPLOYEE);
    if (p === '/api/ess/package') return json(PACKAGE);
    // "My benefits — used and left" sits under the package and reads its own endpoint; nothing counted to show here.
    if (p === '/api/ess/package/usage') return json({ benefits: [], requests: [] });
    // release_a is opt-in: an absent key in disabled-keys means the platform switched it on for this tenant.
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false, currencyCode: 'SAR' });
    return json({ items: [], total: 0 });
  });
  return errors;
}

for (const lang of ['en', 'ar'] as const) {
  test(`My package: benefits nothing pays yet say so in one line, paid ones do not (${lang})`, async ({ page }, info) => {
    const errors = await boot(page, lang);
    await page.goto('/ess/package');
    await expect(page.locator('html')).toHaveAttribute('dir', lang === 'ar' ? 'rtl' : 'ltr');
    const lineOf = (code: string) => {
      const l = PACKAGE.lines.find((x) => x.componentCode === code)!;
      return page.getByRole('listitem').filter({ has: page.getByText(lang === 'ar' ? l.labelAr : l.labelEn, { exact: true }) });
    };
    await expect(lineOf('HOUSING')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('recorded-only-note')).toHaveCount(4);
    for (const code of ['AIR_TICKET', 'MEDICAL', 'EDUCATION', 'PER_DIEM'])
      await expect(lineOf(code).getByTestId('recorded-only-note'), code).toHaveText(say(lang, NOTE));
    for (const code of ['HOUSING', 'TRANSPORT'])
      await expect(lineOf(code).getByTestId('recorded-only-note'), code).toHaveCount(0);
    // The note never pushes the page sideways on a phone.
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    if (EVIDENCE) {
      fs.mkdirSync(EVIDENCE, { recursive: true });
      await page.screenshot({ path: `${EVIDENCE}/my-package-${lang}-${info.project.name}.png`, fullPage: true });
    }
    expect(errors).toEqual([]);
  });
}
