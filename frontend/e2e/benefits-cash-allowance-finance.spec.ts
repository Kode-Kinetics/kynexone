import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * Wave D, Benefits by grade, in a real browser against route mocks: a cash allowance somebody without the Finance permission added waits
 * for Finance ("Waiting for Finance", words and icon, no grade value can be set); Finance confirms it in a panel inside the grid area
 * (the GL account preselected, end of service as a category and a note), or is told the maker cannot confirm; a confirmed allowance is
 * corrected from a month on with a reason (a committed month names the earliest one that can change; stale draft runs are listed).
 * Written without a run: the sandbox cannot launch Chromium. CI's browser lane runs it.
 *
 * Run: npx playwright test -c e2e/playwright.matrix.config.ts (starts `next dev` on MATRIX_FIXTURE_PORT, default 5197).
 */

const fixtures = path.join(__dirname, 'fixtures', 'benefits-by-grade');
const load = (name: string) => JSON.parse(fs.readFileSync(path.join(fixtures, `${name}.json`), 'utf8'));
const groupMatrix = load('matrix-group');
const companies: { id: string; name: string }[] = load('companies');

type Locale = 'en' | 'ar';
const say = (locale: Locale, key: string, values: Record<string, string | number> = {}) =>
  Object.entries(values).reduce((text, [k, v]) => text.replaceAll(`{${k}}`, String(v)), (LOCALE_DICTS[locale] as Record<string, string>)[key] ?? key);

const info = (over: object = {}) => ({
  financePending: false, financeConfirmedAtUtc: '2026-11-01T08:00:00Z', glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbExclusionCategory: null,
  eosbExclusionReason: null, effectiveFrom: null, ...over,
});
const allowance = (code: string, nameEn: string, nameAr: string, cashAllowance: object, extra: object = {}) => ({
  code, nameEn, nameAr, class: 'Allowance', floor: 'None', group: 'Wage', allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [],
  allowedDependantScopes: ['None'], allowedLimitPeriods: [], defaultLimitPeriod: null, isFloor: false, canBeSkipped: true, isLoanFacility: false,
  floorReason: null, isCustom: true, cashAllowance, ...extra,
});
const waiting = allowance('ALW_PHONE', 'Phone', 'هاتف', info({ financePending: true, financeConfirmedAtUtc: null }));
const confirmed = allowance('ALW_CAR', 'Car', 'سيارة', info({ glDriverKey: 'EARN:CAR' }));
const withAllowances = (components: object[]) => ({
  ...groupMatrix,
  components: [...groupMatrix.components, ...components],
  offerings: [...groupMatrix.offerings, ...components.map((c: any) => ({ componentCode: c.code, mode: 'Group', offered: true, skippedFrom: null, changesOn: null, canBeSkipped: true }))],
});
const glDrivers = [
  { key: 'EARN:PHONE', label: 'Phone allowances', labelAr: null, usable: true, reasonCode: null, reason: null },
  { key: 'EARN:CAR', label: 'Car allowances', labelAr: null, usable: true, reasonCode: null, reason: null },
];

async function boot(page: Page, locale: Locale, permissions: string[], matrix: () => unknown) {
  const errors: string[] = [];
  const calls: { path: string; body: any }[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(([lang]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, [locale]);
  await page.route('**/api/**', route => {
    const request = route.request(); const url = new URL(request.url()); const p = url.pathname;
    if (p === '/api/auth/me') return route.fulfill({ json: {
      id: 'user-1', tenantId: 'tenant-1', tenantSlug: 'masar', fullName: 'Finance Fatima', roles: ['Finance'], isGroupScope: true, permissions,
      companies: companies.map((c, i) => ({ ...c, code: `M${i + 1}`, countryCode: 'SA', isActive: true })),
    } });
    if (p === '/api/entitlements/matrix' && request.method() === 'GET') return route.fulfill({ json: matrix() });
    if (p === '/api/entitlements/components/cash-gl-drivers') return route.fulfill({ json: glDrivers });
    if (p === '/api/tenant-admin/localization') return route.fulfill({ json: { countryCode: 'SA', currencyCode: 'SAR', defaultLanguage: locale } });
    if (p.includes('/features/') || p === '/api/notifications') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/benefits/by-grade');
  await expect(page.getByRole('heading', { name: say(locale, 'Benefits by grade'), level: 1 })).toBeVisible();
  return { errors, calls };
}

// The toolbar button, which opens the Add a benefit dialog; its own submit has the same words, so it is reached through the dialog.
const addBenefitButton = (page: Page, locale: Locale) => page.getByRole('button', { name: say(locale, 'Add benefit'), exact: true }).first();
// Finance's visible step on a waiting row: "Review and confirm", named for its benefit.
const confirmButton = (page: Page, locale: Locale, benefit: string) =>
  page.getByRole('button', { name: new RegExp(`${say(locale, 'Review and confirm')}\\s*${benefit.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`) });
// Any grade's cell for a benefit: the label is "{benefit} for {grade}: {value}" in the locale's word order, so the grade and value are matched, not left empty.
const cellOfAnyGrade = (locale: Locale, benefit: string) => {
  const marks = say(locale, '{benefit} for {grade}: {value}', { benefit: 'BENEFIT_MARK', grade: 'GRADE_MARK', value: 'VALUE_MARK' }).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`^${marks.replace('BENEFIT_MARK', benefit.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).replace('GRADE_MARK', '.+').replace('VALUE_MARK', '.*')}$`);
};
const name = (locale: Locale, c: { nameEn: string; nameAr: string }) => (locale === 'ar' ? c.nameAr : c.nameEn);

for (const locale of ['en', 'ar'] as const) {
  test.describe(`cash allowances and Finance (${locale})`, () => {
    test('a pending allowance says it waits for Finance, takes no value, and the guide says so', async ({ page }) => {
      const { errors } = await boot(page, locale, ['entitlements.read', 'entitlements.manage'], () => withAllowances([waiting]));
      const row = page.getByRole('row').filter({ hasText: name(locale, waiting) });
      // Words and an icon, not colour alone.
      await expect(row.getByTestId('row-chip')).toHaveText(say(locale, 'Waiting for Finance'));
      await expect(row.getByTestId('row-chip').locator('svg')).toHaveCount(1);
      // Its grade cells cannot be opened; nobody without the Finance permission is offered Confirm, and is told who can.
      await expect(row.getByRole('button', { name: cellOfAnyGrade(locale, name(locale, waiting)) }).first()).toBeDisabled();
      await expect(row.getByRole('button', { name: say(locale, 'Confirm') })).toHaveCount(0);
      await expect(row.getByText(say(locale, 'Finance must confirm it before it can be used.'))).toBeVisible();
      await expect(page.getByTestId('guide-waiting-for-finance')).toContainText(name(locale, waiting));
      expect(errors).toEqual([]);
    });

    // G-011: nothing told Finance an allowance was waiting; it showed only as a badge in the grid. Now a list, with the Confirm action, for those who can confirm.
    test('Finance sees a Waiting for Finance list with a Review and confirm action that opens the confirm panel; nobody else sees the list', async ({ page }) => {
      const { errors } = await boot(page, locale, ['entitlements.read', 'entitlements.manage', 'payroll.approve'], () => withAllowances([waiting]));
      const list = page.getByTestId('waiting-for-finance-list');
      await expect(list).toBeVisible();
      await expect(list.getByRole('heading')).toBeVisible();
      await expect(list.getByTestId('waiting-for-finance-item')).toHaveCount(1);
      await expect(list.getByTestId('waiting-for-finance-item')).toContainText(name(locale, waiting));
      await expect(list).toHaveAttribute('id', 'waiting-for-finance');
      await list.getByRole('button', { name: say(locale, 'Review {benefit}, then confirm it', { benefit: name(locale, waiting) }) }).click();
      await expect(page.getByTestId('finance-confirm-panel')).toBeVisible();
      expect(errors).toEqual([]);
    });

    test('without the Finance permission there is no Waiting for Finance list', async ({ page }) => {
      const { errors } = await boot(page, locale, ['entitlements.read', 'entitlements.manage'], () => withAllowances([waiting]));
      await expect(page.getByTestId('waiting-for-finance')).toHaveCount(1);   // the row badge stays
      await expect(page.getByTestId('waiting-for-finance-list')).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('Finance confirms it in a panel in the grid area, with the GL account preselected and the end of service category', async ({ page }) => {
      let done = false;
      const { errors, calls } = await boot(page, locale, ['entitlements.read', 'entitlements.manage', 'payroll.approve'],
        () => withAllowances([done ? { ...waiting, cashAllowance: info({ glDriverKey: 'EARN:CAR' }) } : waiting]));
      await page.route('**/api/entitlements/components/ALW_PHONE/confirm', route => {
        calls.push({ path: new URL(route.request().url()).pathname, body: route.request().postDataJSON() });
        done = true;
        return route.fulfill({ json: { ...waiting, cashAllowance: info({ glDriverKey: 'EARN:CAR' }), warnings: [{ code: 'fixed_cash', message: 'A fixed monthly cash amount is normally part of the wage.' }] } });
      });
      await confirmButton(page, locale, name(locale, waiting)).click();
      const panel = page.getByTestId('finance-confirm-panel');
      await expect(panel).toBeVisible();
      await expect(panel.getByRole('heading', { name: say(locale, 'Confirm {benefit}', { benefit: name(locale, waiting) }) })).toBeFocused();
      const gl = panel.getByLabel(say(locale, 'GL account for this allowance (required)'));
      await expect(gl).toHaveValue('EARN:PHONE'); // what the maker entered
      // Leaving it out needs its category; the error sits at the field.
      await panel.getByRole('radio', { name: say(locale, 'No, leave it out') }).check();
      await panel.getByRole('button', { name: say(locale, 'Confirm'), exact: true }).click();
      await expect(panel.getByText(say(locale, 'Choose why this allowance is left out of end of service pay.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      await panel.getByLabel(say(locale, 'Why is it left out? (required)')).selectOption('Discretionary');
      await gl.selectOption('EARN:CAR');
      await panel.getByRole('button', { name: say(locale, 'Confirm'), exact: true }).click();
      await expect(page.getByText(say(locale, '{benefit} was confirmed. Set its value for each grade, then publish.', { benefit: name(locale, waiting) }))).toBeVisible();
      expect(calls[0]).toMatchObject({ path: '/api/entitlements/components/ALW_PHONE/confirm', body: {
        glDriverKey: 'EARN:CAR', eosbIncluded: false, eosbExclusionCategory: 'Discretionary', eosbExclusionReason: null } });
      // Server cautions are shown calmly, and the row is no longer waiting.
      await expect(page.getByTestId('allowance-cautions')).toContainText('A fixed monthly cash amount is normally part of the wage.');
      await expect(page.getByTestId('row-chip').filter({ hasText: say(locale, 'Waiting for Finance') })).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('the person who added it is told someone else must confirm, and Confirm is withdrawn', async ({ page }) => {
      const { errors } = await boot(page, locale, ['entitlements.read', 'entitlements.manage', 'payroll.approve'], () => withAllowances([waiting]));
      await page.route('**/api/entitlements/components/ALW_PHONE/confirm', route => route.fulfill({ status: 403, json: { error: 'maker_cannot_confirm', message: 'You added it.' } }));
      await confirmButton(page, locale, name(locale, waiting)).click();
      await page.getByTestId('finance-confirm-panel').getByRole('button', { name: say(locale, 'Confirm'), exact: true }).click();
      await expect(page.getByTestId('finance-confirm-panel').getByRole('alert')).toContainText(say(locale, 'You added this allowance, so someone else in Finance must confirm it.'));
      await expect(page.getByTestId('finance-confirm-panel').getByRole('button', { name: say(locale, 'Confirm'), exact: true })).toBeDisabled();
      await expect(page.getByTestId('maker-cannot-confirm')).toBeVisible();
      await expect(confirmButton(page, locale, name(locale, waiting))).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('Finance corrects a confirmed allowance from a month on: a committed month names the earliest, and stale draft runs are listed', async ({ page }) => {
      const { errors, calls } = await boot(page, locale, ['entitlements.read', 'entitlements.manage', 'payroll.approve'], () => withAllowances([confirmed]));
      let attempts = 0;
      await page.route('**/api/entitlements/components/ALW_CAR/correct', route => {
        calls.push({ path: new URL(route.request().url()).pathname, body: route.request().postDataJSON() });
        if (++attempts === 1) return route.fulfill({ status: 409, json: { error: 'period_committed', message: 'A payroll month is locked.', earliestEffectiveFrom: '2027-01-01' } });
        return route.fulfill({ json: { component: { ...confirmed, cashAllowance: info({ glDriverKey: 'EARN:PHONE', effectiveFrom: '2027-01-01' }) },
          staleRuns: [{ id: 'run-1', year: 2027, month: 2, status: 'Draft' }, { id: 'run-0', year: 2027, month: 1, status: 'Draft' }] } });
      });
      await page.getByRole('button', { name: say(locale, 'Actions for {benefit}', { benefit: name(locale, confirmed) }), exact: true }).click();
      await page.getByRole('menuitem', { name: say(locale, 'Correct GL / end of service'), exact: true }).click();
      const panel = page.getByTestId('finance-correct-panel');
      await expect(panel).toBeVisible();
      // Nothing changed, no reason: the problems are said at their fields and nothing is sent.
      await panel.getByRole('button', { name: say(locale, 'Save the correction') }).click();
      await expect(panel.getByText(say(locale, 'Say why you are changing it.'))).toBeVisible();
      await expect(panel.getByText(say(locale, 'Change the GL account or the end of service setting, or cancel.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      await panel.getByLabel(say(locale, 'GL account for this allowance (required)')).selectOption('EARN:PHONE');
      await panel.getByLabel(say(locale, 'Reason for the change (required)')).fill('Posted to the wrong account');
      await panel.getByRole('button', { name: say(locale, 'Save the correction') }).click();
      // The committed month: the earliest that can still change is offered and named.
      await expect(panel.locator('#finance-correct-month-error')).toBeVisible();
      await expect(panel.getByLabel(say(locale, 'Change starts from'))).toHaveValue('2027-01-01');
      await panel.getByRole('button', { name: say(locale, 'Save the correction') }).click();
      expect(calls[1].body).toEqual({ effectiveFrom: '2027-01-01', reason: 'Posted to the wrong account', glDriverKey: 'EARN:PHONE' });
      // Draft runs calculated with the old setting are named, oldest first.
      const stale = page.getByTestId('stale-runs');
      await expect(stale).toBeVisible();
      await expect(stale).toContainText(say(locale, 'These draft runs were calculated with the old setting; process them again: {months}.', { months: '' }).split(':')[0]);
      expect(errors).toEqual([]);
    });

    test('adding a cash allowance without the Finance permission says Finance must confirm it, and shows the cautions', async ({ page }) => {
      let exists = false;
      const { errors, calls } = await boot(page, locale, ['entitlements.read', 'entitlements.manage'], () => (exists ? withAllowances([waiting]) : groupMatrix));
      await page.route('**/api/entitlements/components', route => {
        if (route.request().method() !== 'POST') return route.fallback();
        calls.push({ path: '/api/entitlements/components', body: route.request().postDataJSON() });
        exists = true;
        return route.fulfill({ json: { ...waiting, warnings: [{ code: 'reimbursive_name', message: 'The name sounds like a reimbursement; check whether it counts toward end of service pay.' }] } });
      });
      await addBenefitButton(page, locale).click();
      await page.getByLabel(say(locale, 'Benefit name (English)')).fill('Phone');
      await page.getByRole('radio', { name: say(locale, 'Yes, a monthly cash allowance paid in the salary') }).check();
      await page.getByLabel(say(locale, 'GL account for this allowance (required)')).selectOption('EARN:PHONE');
      await page.getByRole('dialog').getByRole('button', { name: say(locale, 'Add benefit'), exact: true }).click();
      await expect(page.getByText(say(locale, 'Added. Finance must confirm the GL account and end-of-service setting before it can be used.'))).toBeVisible();
      await expect(page.getByTestId('allowance-cautions')).toContainText('The name sounds like a reimbursement');
      await expect(page.getByTestId('row-chip')).toBeVisible();
      expect(errors).toEqual([]);
    });
  });
}
