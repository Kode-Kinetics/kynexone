import { expect, test } from '@playwright/test';
import { bootEss } from './ess/essMocks';

/** P2: Arabic at 360 px. No horizontal scroll, the page is RTL, and numbers (IBAN, ID, phone) stay left-to-right. */
test.use({ viewport: { width: 360, height: 780 } });

const noHorizontalScroll = (page: import('@playwright/test').Page) => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);

test('Arabic list and form at 360 px: right-to-left, no sideways scroll, numbers left-to-right', async ({ page }, info) => {
  const log = await bootEss(page, { locale: 'ar', sections: ['person', 'id', 'bank'], states: { person: 'ChangesRequested' }, note: 'يرجى استخدام رقم جوالك.' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('heading', { name: 'أكمل ملفك الشخصي', exact: true })).toBeVisible();
  await expect(page.getByTestId('section-person')).toContainText('بيانات التواصل');
  await expect(page.getByTestId('section-person')).toContainText('مطلوب تعديل');
  expect(await noHorizontalScroll(page)).toBe(true);
  await page.screenshot({ path: process.env.P2_SHOTS ? `${process.env.P2_SHOTS}/p2-ess-list-ar-360.png` : info.outputPath('list.png'), animations: 'disabled' });

  await page.getByTestId('section-person').click();
  await expect(page.getByText('يرجى استخدام رقم جوالك.')).toBeVisible();
  for (const label of ['رقم الجوال (مطلوب)', 'البريد الإلكتروني الشخصي (مطلوب)']) await expect(page.getByLabel(label)).toHaveAttribute('dir', 'ltr');
  expect(await noHorizontalScroll(page)).toBe(true);
  await page.screenshot({ path: process.env.P2_SHOTS ? `${process.env.P2_SHOTS}/p2-ess-form-ar-360.png` : info.outputPath('form.png'), animations: 'disabled' });

  await page.getByRole('button', { name: 'العودة إلى القائمة' }).click();
  await page.getByTestId('section-bank').click();
  await expect(page.locator('bdi[dir="ltr"]', { hasText: 'SA** **** **** **** **** 7519' })).toBeVisible();
  await expect(page.getByLabel('رقم الآيبان (مطلوب)')).toHaveAttribute('dir', 'ltr');
  await page.getByLabel('رقم الآيبان (مطلوب)').fill('SA4420000001234567891234');
  await page.getByRole('button', { name: 'إرسال إلى الموارد البشرية' }).click();
  const dialog = page.getByRole('dialog', { name: 'أكّد أنك أنت' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('كلمة المرور')).toHaveAttribute('dir', 'ltr');
  expect(await noHorizontalScroll(page)).toBe(true);
  await page.screenshot({ path: process.env.P2_SHOTS ? `${process.env.P2_SHOTS}/p2-ess-stepup-ar-360.png` : info.outputPath('stepup.png'), animations: 'disabled' });
  expect(log.errors).toEqual([]);
});

test('Arabic HR review at 360 px', async ({ page }, info) => {
  const log = await bootEss(page, { locale: 'ar', persona: 'hr' });
  await expect(page.getByRole('heading', { name: 'مراجعة استكمال الملفات' })).toBeVisible();
  await page.getByTestId('inbox-item').nth(1).getByRole('button', { name: 'مراجعة' }).click();
  await expect(page.getByTestId('inbox-item').nth(1)).toContainText('تذهب إلى المالية');
  expect(await noHorizontalScroll(page)).toBe(true);
  await page.screenshot({ path: process.env.P2_SHOTS ? `${process.env.P2_SHOTS}/p2-hr-review-ar-360.png` : info.outputPath('hr.png'), animations: 'disabled' });
  expect(log.errors).toEqual([]);
});

test('Arabic: the expiry shows its Hijri date in Arabic, left-to-right date input', async ({ page }) => {
  const log = await bootEss(page, { locale: 'ar', sections: ['id'], idClass: 'expat' });
  await page.getByTestId('section-id').click();
  await expect(page.getByTestId('hijri-iqamaExpiry')).toContainText('الهجري:');
  await expect(page.getByLabel('تاريخ انتهاء الإقامة')).toHaveAttribute('dir', 'ltr');
  expect(await noHorizontalScroll(page)).toBe(true);
  if (process.env.P2_SHOTS) await page.screenshot({ path: `${process.env.P2_SHOTS}/p2-ess-identity-hijri-ar-360.png`, animations: 'disabled' });
  expect(log.errors).toEqual([]);
});
