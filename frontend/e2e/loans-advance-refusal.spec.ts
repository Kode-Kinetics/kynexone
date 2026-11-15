import { expect, test } from '@playwright/test';

/**
 * A salary-advance refusal is now a coded body { error, message, messageAr }. The New Advance Request form used to put the raw
 * response body into the error line, which for an object would break the page; it now shows the sentence, in Arabic for an
 * Arabic reader. API mocked, as in loans-separate-payments.spec.ts.
 */
for (const locale of ['en', 'ar'] as const) {
  test(`advance request refusal is shown as a sentence (${locale})`, async ({ page }, info) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript((lang) => {
      localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone.theme', 'light');
      localStorage.setItem('kynexone-locale-choice-v2', lang);
    }, locale);
    const refusal = {
      error: 'advance_policy_required',
      message: 'Salary advances need an active advance policy.',
      messageAr: 'تتطلب سلف الرواتب سياسة سلف فعّالة.',
    };
    let posted = false;
    await page.route('**/api/**', async route => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
      if (path === '/api/auth/me') return json({ id: 'hr-1', tenantId: 'tenant-1', tenantSlug: 'fixture', email: 'hr@example.test', fullName: 'HR Manager', roles: ['HR Manager'], permissions: ['loans.read', 'loans.write', 'employees.read'], companies: [] });
      if (['/api/features/disabled-keys', '/api/features/modules', '/api/notifications'].includes(path)) return json([]);
      if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
      if (path.endsWith('/audit')) return json({});
      if (path === '/api/employees') return json({ items: [{ id: 7, publicId: 'emp-7', fullName: 'Amira Mansour', employeeCode: 'E-007', department: 'Ops', status: 'Active' }], total: 1, page: 1, pageSize: 8 });
      if (path === '/api/finance/advances' && request.method() === 'POST') { posted = true; return json(refusal, 400); }
      if (path === '/api/finance/advances') return json({ total: 0, items: [] });
      return json({ items: [], total: 0 });
    });

    await page.goto('/loans');
    // The module's tab labels read in the viewer's language.
    await page.getByRole('button', { name: locale === 'ar' ? 'السلف على الراتب' : 'Salary Advances', exact: true }).click();
    await page.getByRole('button', { name: /New Advance Request|طلب سلفة جديد/ }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByPlaceholder(/Search by name or code|ابحث/).fill('Amira');
    await dialog.getByRole('button', { name: /Amira Mansour/ }).click();
    await dialog.getByTitle(/Requested Amount|المبلغ المطلوب/).fill('1000');
    await dialog.getByRole('button', { name: /^(Submit|إرسال)$/ }).click();

    await expect.poll(() => posted).toBe(true);
    await expect(dialog.getByText(locale === 'ar' ? refusal.messageAr : refusal.message, { exact: true })).toBeVisible();
    await page.screenshot({ path: info.outputPath(`advance-refusal-${locale}.png`), fullPage: true });
    expect(errors).toEqual([]);
  });
}
