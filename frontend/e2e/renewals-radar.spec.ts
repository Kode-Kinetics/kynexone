import { expect, test, type Page } from '@playwright/test';
import { faisalChain, radar } from './fixtures/renewals/fixtures';

// Contract renewals (Release A R4) on mocked routes: the radar and the chain drawer, in English and in Arabic (RTL).
// Run: npx playwright test -c e2e/playwright.renewals.config.ts

const LOCALE_CHOICE_KEY = 'kynexone-locale-choice-v2';

async function boot(page: Page, locale: 'en' | 'ar') {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(([key, value]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem(key, value);
  }, [LOCALE_CHOICE_KEY, locale]);
  await page.route('**/api/**', (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/auth/me') {
      return route.fulfill({ json: {
        id: 'hr-1', employeeId: 1, tenantId: 'tenant-1', tenantSlug: 'masar', fullName: 'HR Manager', roles: ['HR Manager'],
        permissions: ['contracts.renewal.read', 'contracts.renewal.manage'],
        companies: [{ id: 'co-1', name: 'Masar Facility Services Co.', code: 'MFS', countryCode: 'SA', isActive: true }],
      } });
    }
    if (path === '/api/features/disabled-keys') return route.fulfill({ json: [] });
    if (path === '/api/features/modules') return route.fulfill({ json: [] });
    if (path === '/api/contracts/renewals/radar') return route.fulfill({ json: radar });
    if (path === '/api/contracts/con-faisal/chain') return route.fulfill({ json: faisalChain });
    if (path === '/api/tenant-admin/localization') return route.fulfill({ json: { currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh', countryCode: 'SA' } });
    if (path === '/api/notifications') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], total: 0 } });
  });
  await page.goto('/contract-renewals');
  return errors;
}

test('the radar shows buckets that add up, the storyline badge and Next line, drill-downs and fast-lane selection (English)', async ({ page }) => {
  const errors = await boot(page, 'en');
  await expect(page.getByRole('heading', { name: 'Contract renewals' })).toBeVisible();
  await expect(page.getByText('Every contract ending in the next 120 days, what is due next, and what happens if it is missed.')).toBeVisible();

  // Buckets, overdue first, and the storyline lines.
  await expect(page.getByRole('button', { name: /Overdue: contract already ended/ })).toContainText('1');
  await expect(page.getByText('Art 55: 2 of 3 renewals, 3.9 of 4 years — only convert to indefinite or non-renew')).toBeVisible();
  await expect(page.getByText('Next: send offer by 16 Oct — if missed: renews on current terms (Art. 74(2))')).toBeVisible();
  await expect(page.getByText('Expired — holdover pending').first()).toBeVisible();

  // Only the fast-lane-eligible row can be selected; changing the filter starts a new selection.
  await page.getByRole('checkbox', { name: 'Select Ramon Dela Cruz for renewal on current terms' }).check();
  await expect(page.getByRole('region', { name: 'Selected reviews' })).toContainText('1 selected to renew on current terms');
  await expect(page.getByRole('checkbox', { name: 'Select Faisal Al-Qahtani for renewal on current terms' })).toBeDisabled();
  await page.getByRole('button', { name: /In 31–60 days/ }).click();
  await expect(page.getByRole('region', { name: 'Selected reviews' })).toHaveCount(0);
  await expect(page.getByText('Showing 1 of 3 reviews.')).toBeVisible();

  // Every reconciliation number drills down to its records.
  await page.getByRole('button', { name: 'Show the contracts without a review (1)' }).click();
  await expect(page.getByRole('region', { name: 'Due without a review' })).toContainText('Saudi or non-Saudi not confirmed');
  await page.getByRole('button', { name: 'Show the contracts whose review opens later (1)' }).click();
  await expect(page.getByRole('region', { name: 'Reviews that open later' })).toContainText('Noura Al-Otaibi');

  // The chain drawer: Art. 55 meter, start-date anchor and signing date.
  await page.getByRole('button', { name: 'Show the contracts with an open review (2)' }).click();
  await page.getByRole('row', { name: /Faisal Al-Qahtani/ }).getByRole('button', { name: 'Contract history' }).click();
  const drawer = page.getByRole('dialog', { name: 'Contract history' });
  await expect(drawer).toContainText('2 of 3 renewals');
  await expect(drawer).toContainText('3.9 of 4 years');
  await expect(drawer).toContainText('At the limit: at renewal this contract can only become indefinite or not be renewed.');
  await expect(drawer.getByRole('columnheader', { name: 'Starts (renewal anchor)' })).toBeVisible();
  await expect(drawer).toContainText('Convert to indefinite · Do not renew');
  expect(errors).toEqual([]);
});

test('the radar and the chain drawer read right-to-left in Arabic, with Arabic names and sentences', async ({ page }) => {
  const errors = await boot(page, 'ar');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('heading', { name: 'تجديد العقود' })).toBeVisible();
  await expect(page.getByText('المادة 55: 2 من 3 تجديدات، 3.9 من 4 سنوات — التحويل إلى غير محدد المدة أو عدم التجديد فقط')).toBeVisible();
  await expect(page.getByText(/التالي: إرسال العرض قبل .* — إن فات: يتجدد بالشروط الحالية \(المادة 74 فقرة 2\)/)).toBeVisible();
  await expect(page.getByText('فيصل القحطاني')).toBeVisible();
  await expect(page.getByText(/شركة مسار للخدمات/).first()).toBeVisible();

  await page.getByRole('button', { name: 'عرض العقود التي ليس لها مراجعة (1)' }).click();
  await expect(page.getByRole('region', { name: 'مستحقة دون مراجعة' })).toContainText('خالد الصباح');
  await expect(page.getByRole('region', { name: 'مستحقة دون مراجعة' })).toContainText('لم يتم تأكيد سعودي أو غير سعودي');

  await page.getByRole('button', { name: 'عرض العقود التي لها مراجعة مفتوحة (2)' }).click();
  await page.getByRole('row', { name: /فيصل القحطاني/ }).getByRole('button', { name: 'سجل العقود' }).click();
  const drawer = page.getByRole('dialog', { name: 'سجل العقود' });
  await expect(drawer).toContainText('2 من 3 تجديدات');
  await expect(drawer).toContainText('التحويل إلى غير محدد المدة · عدم التجديد');
  expect(errors).toEqual([]);
});
