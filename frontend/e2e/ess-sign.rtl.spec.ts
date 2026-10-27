import { expect, test } from '@playwright/test';
import { bootEsign, EMPLOYEE_NAME_AR } from './ess/esignMocks';

/** P4: Arabic at 360 px. No sideways scroll, right-to-left, names and codes stay left-to-right. */
test.use({ viewport: { width: 360, height: 780 } });
const shots = process.env.P4_SHOTS;
const noHorizontalScroll = (page: import('@playwright/test').Page) => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);

test('Arabic list, sign page and password dialog at 360 px', async ({ page }) => {
  const log = await bootEsign(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('heading', { name: 'مستندات للتوقيع', exact: true })).toBeVisible();
  await expect(page.getByTestId('not-qualified').first()).toContainText('وليس توقيعًا مؤهلًا');
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-list-ar-360.png`, animations: 'disabled' });

  await page.getByTestId('sign-row').first().getByRole('link', { name: 'اقرأ ووقّع' }).click();
  await expect(page.getByTestId('pdf-preview')).toBeVisible();
  await expect(page.getByTestId('consent-text').locator('[lang="ar"]')).toContainText('أقرّ بأنني قرأت');
  // CD65: the Arabic wording comes first in an Arabic session, aligned to the right
  await expect(page.getByTestId('consent-text').locator('p').first()).toHaveAttribute('lang', 'ar');
  await expect(page.getByTestId('consent-text').locator('p').first()).toHaveCSS('text-align', 'right');
  await expect(page.getByTestId('sign-submit')).toHaveText('أُقرّ وأوقّع');
  await page.getByLabel('قرأت هذا المستند').check();
  const name = page.getByLabel(/اكتب اسمك الكامل/);
  await name.fill(EMPLOYEE_NAME_AR);
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-sign-ar-360.png`, animations: 'disabled' });
  await page.getByRole('button', { name: 'توقيع', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'أكّد أنك أنت' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel(/كلمة المرور/)).toHaveAttribute('dir', 'ltr');
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-stepup-ar-360.png`, animations: 'disabled' });
  await dialog.getByLabel(/كلمة المرور/).fill('x');
  await dialog.getByRole('button', { name: 'تأكيد وتوقيع' }).click();
  await expect(page.getByTestId('sign-success')).toBeVisible();
  await expect(page.getByTestId('verification-code')).toHaveAttribute('dir', 'ltr');
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-ess-success-ar-360.png`, animations: 'disabled' });
  expect(log.signs[0].typedName).toBe(EMPLOYEE_NAME_AR);
  expect(log.signs[0].language).toBe('ar');
  expect(log.errors).toEqual([]);
});

test('Arabic HR signatures panel and sign-in-person dialog at 360 px', async ({ page }) => {
  const log = await bootEsign(page, { locale: 'ar', persona: 'hr' });
  const panel = page.getByTestId('signatures-panel');
  await expect(panel.getByRole('heading', { name: 'التوقيعات' })).toBeVisible();
  // 72 §4: the employee workspace around the panel is Arabic too
  await expect(page.getByRole('heading', { name: 'الامتثال والمستندات', exact: true })).toBeVisible();
  await expect(page.getByText('سجلات الهوية الخاصة بالدولة')).toBeVisible();
  await expect(page.getByRole('link', { name: 'رفع مستند', exact: true })).toBeVisible();
  await panel.scrollIntoViewIfNeeded();
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-hr-signatures-ar-360.png`, animations: 'disabled' });
  await panel.getByTestId('signature-row').first().getByRole('button', { name: 'التوقيع حضوريًا' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByTestId('pdf-preview')).toBeVisible();
  expect(await noHorizontalScroll(page)).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/p4-hr-device-ar-360.png`, animations: 'disabled' });
  expect(log.errors).toEqual([]);
});
