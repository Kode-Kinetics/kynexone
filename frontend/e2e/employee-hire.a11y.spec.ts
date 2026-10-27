import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootHire } from './hire/hireMocks';

/** G11: axe (WCAG 2.2 AA tags) on every step and state of the hire wizard, desktop EN and 360 px AR. Serious and critical fail. */
async function clean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(150);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 3).map(n => n.target.join(' ')) }));
  expect(bad, label).toEqual([]);
}

const L = {
  en: { name: 'English full name', start: 'Start date', desig: 'Designation', next: 'Next', save: 'Save draft', cancel: 'Cancel' },
  ar: { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', next: 'التالي', save: 'حفظ كمسودة', cancel: 'إلغاء' },
} as const;

for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe ${locale} ${viewport.width}`, () => {
    test.use({ viewport });
    test('steps, error state, duplicate panel, dialogs, success', async ({ page }) => {
      const l = L[locale];
      await bootHire(page, { locale, duplicate: { status: 'Terminated', matchType: 'strong' }, warnFirstCreate: true });
      await clean(page, 'step 1 empty');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await expect(page.getByTestId('error-summary')).toBeFocused();
      await clean(page, 'step 1 errors');
      await page.getByLabel(l.name).fill('Alex Morgan');
      await page.getByLabel(l.name).blur();
      await expect(page.getByTestId('error-summary')).toHaveCount(0);
      await page.getByLabel(l.start).fill('2026-01-05');
      await page.getByLabel(l.desig).selectOption({ label: 'Specialist' });
      await expect(page.locator('section[aria-label]').first()).toBeVisible();
      await clean(page, 'duplicate panel');
      await page.getByRole('button', { name: l.save, exact: true }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await clean(page, 'warning dialog');
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'step 2');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'step 3');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'review');
      await page.getByRole('button', { name: l.cancel, exact: true }).click();
      await expect(page.getByRole('alertdialog')).toBeVisible();
      await clean(page, 'discard dialog');
    });
  });
}

/** Audit-fix states: name-only duplicate, resumed draft, the expat passport row, Review with Hijri lines and warnings, success with warnings, and a failed resume. */
const draft = {
  id: 44, status: 'Draft', updatedAtUtc: '2026-10-27T08:00:00Z', companyId: 'company-hire', englishName: 'Alex Morgan', arabicName: 'أليكس مورغان', preferredName: '', gender: '', maritalStatus: '',
  nationality: 'Pakistan', nationalityCode: 'PK', dateOfBirth: '1990-06-01', personalEmail: '', workEmail: 'alex.morgan@example.test', phone: '', joiningDate: '2026-01-05',
  designationId: 'desig-1', gradeId: 'grade-g5', employmentType: 'Full-time', contractType: 'Fixed term', contractEndDate: '2027-11-01', iqamaNumber: '2000000006', iqamaExpiryDate: '2027-01-01',
  passportNumber: 'AB1234567', passportExpiryDate: '2030-05-05', payrollProfile: { iban: 'SA0380000000608010167519', wpsEligible: true, eosbEligible: true },
  complianceRecords: [], documents: [], history: [], transfers: [],
};
for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe audit-fix states ${locale} ${viewport.width}`, () => {
    test.use({ viewport });
    const l = L[locale];
    test('name-only duplicate panel', async ({ page }) => {
      await bootHire(page, { locale, duplicate: { status: 'Active', matchType: 'possible', signals: ['Same name'] } });
      await page.getByLabel(l.name).fill('Alex Morgan');
      await page.getByLabel(l.name).blur();
      await expect(page.locator('section[aria-label]').first()).toBeVisible();
      await clean(page, 'name-only duplicate');
    });
    test('resumed draft, passport row, review and success', async ({ page }) => {
      await bootHire(page, { locale, draftDetail: draft, review: { blockers: [{ key: 'pay:ibanMissing', label: '' }], warnings: [{ key: 'data:joining_backdated', label: '' }], notProvided: [] } }, '/people/new?draft=44');
      await expect(page.getByLabel(l.name)).toHaveValue('Alex Morgan');
      await clean(page, 'resumed draft step 1');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'step 3 with passport row and contract end date');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await expect(page.getByTestId('review-hijri-idExpiry')).toBeVisible();
      await clean(page, 'review with hijri lines and warnings');
      await page.getByRole('button', { name: locale === 'en' ? 'Create employee draft' : 'إنشاء مسودة الموظف' }).click();
      await expect(page.getByTestId('readiness')).toContainText(/\w/);
      await clean(page, 'success with warnings');
    });
    test('draft not available', async ({ page }) => {
      await bootHire(page, { locale, draftDetail: 404 }, '/people/new?draft=44');
      await expect(page.getByRole('link', { name: locale === 'en' ? 'Start a new employee' : 'إضافة موظف جديد' })).toBeVisible();
      await clean(page, 'draft not found');
    });
  });
}
