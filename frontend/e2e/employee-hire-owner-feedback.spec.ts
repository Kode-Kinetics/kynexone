import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import { bootHire, expectNoHorizontalScroll } from './hire/hireMocks';

/**
 * Browser Pilot for the owner feedback on the live wizard (OD7 to OD10), mocked API with the real response shapes.
 * Local: next dev -p <port>, then playwright test -c e2e/playwright.employee-hire.config.ts (EMPLOYEE_HIRE_BASE_URL).
 */
const L = {
  en: { name: 'English full name', start: 'Start date', desig: 'Designation', grade: 'Grade', next: 'Next', save: 'Save draft', email: 'Work email', nat: /Nationality/, suggest: 'Suggest (AR)', arName: 'Arabic full name' },
  ar: { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', grade: 'الدرجة', next: 'التالي', save: 'حفظ كمسودة', email: 'البريد الإلكتروني الوظيفي', nat: /الجنسية/, suggest: 'اقتراح بالعربية', arName: 'الاسم الكامل بالعربية' },
} as const;
const next = (page: Page, l: typeof L.en | typeof L.ar) => page.getByRole('button', { name: l.next, exact: true }).click();

test.describe('OD7 work email', () => {
  test('one free-text field: no fixed domain suffix, any domain is accepted, a bare name is refused with a sentence', async ({ page }) => {
    const api = await bootHire(page);
    await page.getByLabel(L.en.name).fill('Alex Morgan');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    const field = page.getByLabel(L.en.email);
    await expect(page.getByText('@example.test', { exact: true })).toHaveCount(0);
    await field.fill('alex.morgan');
    await field.blur();
    await expect(page.locator('#hire-workEmail-error')).toHaveText('Enter the complete work email address issued by IT, for example name@company.com.');
    await field.fill('alex.morgan@gmail.com');
    await field.blur();
    await expect(page.locator('#hire-workEmail-error')).toHaveCount(0);
    await expect(page.locator('#hire-workEmail-hint')).toContainText('signs in to Self-Service');
    await page.getByRole('button', { name: L.en.save, exact: true }).click();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[0].body.workEmail).toBe('alex.morgan@gmail.com');
  });
});

test.describe('OD8 grade', () => {
  test('any active grade can be chosen with a designation; Create asks for a reason and sends it', async ({ page }) => {
    const api = await bootHire(page, { warnFirstCreate: true, warnings: [{ code: 'grade_differs_from_designation', field: 'gradeId' }] });
    await page.getByLabel(L.en.name).fill('Alex Morgan');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    await page.getByLabel(L.en.desig).selectOption({ label: 'Specialist' });
    await expect(page.getByLabel(L.en.grade)).toHaveValue('grade-g5');
    await page.getByLabel(L.en.grade).selectOption({ label: 'G6 - Senior' });
    await page.getByLabel(L.en.grade).blur();
    await expect(page.locator('#hire-gradeId-error')).toHaveCount(0);
    await page.getByRole('button', { name: L.en.save, exact: true }).click();
    const dlg = page.getByRole('dialog', { name: 'Confirm before saving' });
    await expect(dlg).toContainText('This grade is not the grade of the chosen designation.');
    await dlg.getByLabel('Reason').fill('Seconded from the regional office');
    await dlg.getByRole('button', { name: 'Confirm and save' }).click();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[1].body.gradeId).toBe('grade-g6');
    expect(api.creates[1].body.acknowledgedWarnings).toEqual([{ code: 'grade_differs_from_designation', reason: 'Seconded from the regional office' }]);
  });
});

test.describe('OD9 Arabic name suggestion', () => {
  const names = { 'Mohammed Abdullah Al-Qahtani': 'محمد عبد الله القحطاني', 'Fatimah Ahmed Al-Harbi': 'فاطمة أحمد الحربي' };
  test('Suggest (AR) shows the suggestion, Use this spelling fills the field; an unknown name asks for manual entry', async ({ page }) => {
    const api = await bootHire(page, { transliterations: names });
    await page.getByLabel(L.en.name).fill('Mohammed Abdullah Al-Qahtani');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    await page.getByLabel(L.en.desig).selectOption({ label: 'Specialist' });
    await next(page, L.en);
    await page.getByRole('button', { name: L.en.suggest }).click();
    await expect(page.getByText('محمد عبد الله القحطاني')).toBeVisible();
    await page.getByRole('button', { name: 'Use this spelling' }).click();
    await expect(page.getByLabel(L.en.arName)).toHaveValue('محمد عبد الله القحطاني');
    expect(api.translits[0]).toMatchObject({ text: 'Mohammed Abdullah Al-Qahtani', kind: 'person-name' });
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await page.getByLabel(L.en.name).fill('Zxqv Plorth');
    await next(page, L.en);
    await page.getByRole('button', { name: L.en.suggest }).click();
    await expect(page.getByText('No reliable suggestion for this name.')).toBeVisible();
  });
});

test.describe('OD10 nationality', () => {
  test('the picker lists every country from the real endpoint and Arabic search finds them', async ({ page }) => {
    await bootHire(page);
    await page.getByLabel(L.en.name).fill('Alex Morgan');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    await page.getByLabel(L.en.desig).selectOption({ label: 'Specialist' });
    await next(page, L.en);
    const box = page.getByRole('combobox', { name: L.en.nat });
    await box.click();
    expect(await page.getByRole('listbox').getByRole('option').count()).toBeGreaterThanOrEqual(245);
    await expect(page.getByRole('listbox').getByRole('option').first()).toContainText('Saudi Arabia');
    await box.fill('sri lan');
    await page.getByRole('listbox').getByRole('option', { name: /Sri Lanka/ }).click();
    await expect(box).toHaveValue('Sri Lanka');
    await box.fill('الفلبين');
    await expect(page.getByRole('listbox').getByRole('option').first()).toContainText('PH');
  });
});

test.describe('screenshots (EN 1440, AR 360)', () => {
  const dir = process.env.HIRE_SCREENS_DIR;
  test.skip(!dir, 'set HIRE_SCREENS_DIR to capture');
  for (const [w, h, locale] of [[1440, 1000, 'en'], [360, 780, 'ar']] as const) {
    const l = L[locale];
    const shot = (page: Page, n: string) => { fs.mkdirSync(dir!, { recursive: true }); return page.screenshot({ path: `${dir}/hf-${n}-${locale}-${w}.png`, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' }); };
    test.describe(`${locale} ${w}`, () => {
      test.use({ viewport: { width: w, height: h } });
      test('basics: work email and grade', async ({ page }) => {
        await bootHire(page, { locale });
        await page.getByLabel(l.name).fill('Mohammed Abdullah Al-Qahtani');
        await page.getByLabel(l.start).fill('2026-11-01');
        await page.getByLabel(l.desig).selectOption({ index: 1 });
        await page.getByLabel(l.grade).selectOption({ index: 2 });
        await page.getByLabel(l.email).fill('mohammed.qahtani@issued.example');
        await page.getByLabel(l.email).blur();
        await expectNoHorizontalScroll(page);
        await shot(page, 'basics-email-grade');
      });
      test('grade reason dialog', async ({ page }) => {
        await bootHire(page, { locale, warnFirstCreate: true, warnings: [{ code: 'grade_differs_from_designation', field: 'gradeId' }] });
        await page.getByLabel(l.name).fill('Alex Morgan');
        await page.getByLabel(l.desig).selectOption({ index: 1 });
        await page.getByLabel(l.grade).selectOption({ index: 2 });
        await page.getByRole('button', { name: l.save, exact: true }).click();
        await expect(page.getByRole('dialog')).toBeVisible();
        await shot(page, 'grade-dialog');
      });
      test('arabic name suggestion', async ({ page }) => {
        await bootHire(page, { locale, transliterations: { 'Mohammed Abdullah Al-Qahtani': 'محمد عبد الله القحطاني' } });
        await page.getByLabel(l.name).fill('Mohammed Abdullah Al-Qahtani');
        await page.getByLabel(l.start).fill('2026-11-01');
        await page.getByLabel(l.desig).selectOption({ index: 1 });
        await next(page, l);
        await page.getByRole('button', { name: l.suggest }).click();
        await expect(page.getByText('محمد عبد الله القحطاني')).toBeVisible();
        await expectNoHorizontalScroll(page);
        await shot(page, 'arabic-suggestion');
      });
      test('nationality list', async ({ page }) => {
        await bootHire(page, { locale });
        await page.getByLabel(l.name).fill('Alex Morgan');
        await page.getByLabel(l.start).fill('2026-11-01');
        await page.getByLabel(l.desig).selectOption({ index: 1 });
        await next(page, l);
        await page.getByRole('combobox', { name: l.nat }).click();
        await expect(page.getByRole('listbox')).toBeVisible();
        await shot(page, 'nationality-list');
      });
    });
  }
});
