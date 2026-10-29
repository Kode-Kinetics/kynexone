import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import { bootHire, company, expectNoHorizontalScroll, grade, grade2, hireTemplateFixture } from './hire/hireMocks';

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

  test('the first ArrowDown selects the first country and keyboard navigation keeps the active option in view', async ({ page }) => {
    await bootHire(page);
    await page.getByLabel(L.en.name).fill('Alex Morgan');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    await page.getByLabel(L.en.desig).selectOption({ label: 'Specialist' });
    await next(page, L.en);
    const box = page.getByRole('combobox', { name: L.en.nat });
    await box.focus();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(box).toHaveValue('Saudi Arabia');

    await box.fill('');
    for (let i = 0; i < 18; i++) await page.keyboard.press('ArrowDown');
    const activeId = await box.getAttribute('aria-activedescendant');
    expect(activeId).toBeTruthy();
    const list = await page.getByRole('listbox').boundingBox();
    const active = await page.locator(`#${activeId}`).boundingBox();
    expect(list).not.toBeNull();
    expect(active).not.toBeNull();
    expect(active!.y).toBeGreaterThanOrEqual(list!.y);
    expect(active!.y + active!.height).toBeLessThanOrEqual(list!.y + list!.height);
  });

  test.describe('mobile popover', () => {
    test.use({ viewport: { width: 360, height: 780 } });
    test('the nationality options do not open behind the sticky action bar', async ({ page }) => {
      await bootHire(page, { locale: 'ar' });
      await page.getByLabel(L.ar.name).fill('Alex Morgan');
      await page.getByLabel(L.ar.start).fill('2026-11-01');
      await page.getByLabel(L.ar.desig).selectOption({ label: 'Specialist' });
      await next(page, L.ar);
      await page.getByRole('combobox', { name: L.ar.nat }).click();
      const list = await page.getByRole('listbox').boundingBox();
      const bar = await page.getByTestId('action-bar').boundingBox();
      expect(list).not.toBeNull();
      expect(bar).not.toBeNull();
      expect(list!.y + list!.height).toBeLessThanOrEqual(bar!.y);
    });
  });
});

const companyB = { ...company, id: 'company-b', legalNameEn: 'Company B', legalNameAr: 'الشركة ب' };
const sharedGrade = { ...grade, id: 'grade-shared', code: 'GS', name: 'Shared', nameAr: 'مشتركة', companyId: null };
const orgByCompany = {
  companies: [company, companyB],
  branches: [
    { id: 'branch-a', companyId: company.id, code: 'A', nameEn: 'Branch A', nameAr: 'فرع أ', isActive: true },
    { id: 'branch-b', companyId: companyB.id, code: 'B', nameEn: 'Branch B', nameAr: 'فرع ب', isActive: true },
  ],
  departments: [
    { id: 'dept-a', companyId: company.id, branchId: 'branch-a', code: 'A', nameEn: 'Department A', isActive: true, managerEmployeeId: 17 },
    { id: 'dept-b', companyId: companyB.id, branchId: 'branch-b', code: 'B', nameEn: 'Department B', isActive: true, managerEmployeeId: 18 },
    { id: 'dept-shared', companyId: null, code: 'S', nameEn: 'Shared Department', isActive: true },
  ],
  designations: [
    { id: 'desig-a', companyId: company.id, code: 'A', titleEn: 'Designation A', gradeId: grade.id, isActive: true },
    { id: 'desig-b', companyId: companyB.id, code: 'B', titleEn: 'Designation B', gradeId: grade2.id, isActive: true },
    { id: 'desig-shared', companyId: null, code: 'S', titleEn: 'Shared Designation', gradeId: sharedGrade.id, isActive: true },
  ],
  grades: [
    { ...grade, companyId: company.id, nameAr: 'محترف' },
    { ...grade2, companyId: companyB.id, nameAr: 'خبير' },
    sharedGrade,
  ],
  managers: [
    { id: 17, employeeCode: 'A-17', fullName: 'Manager A', status: 'Active', companyId: company.id },
    { id: 18, employeeCode: 'B-18', fullName: 'Manager B', status: 'Active', companyId: companyB.id },
  ],
};
const templateA = { ...hireTemplateFixture, id: 'template-a', companyId: company.id, name: 'Template A', defaults: { ...hireTemplateFixture.defaults, departmentId: 'dept-a', managerRule: 'department-head' as const } };
const templateB = { ...hireTemplateFixture, id: 'template-b', companyId: companyB.id, name: 'Template B', defaults: { ...hireTemplateFixture.defaults, departmentId: 'dept-b', managerRule: 'department-head' as const } };

test.describe('company boundary rebase', () => {
  test('blank company shows only shared choices', async ({ page }) => {
    await bootHire(page, { org: orgByCompany });
    await page.locator('#hire-companyId').selectOption('');
    await expect(page.getByLabel(L.en.desig).locator('option')).toHaveText(['Not chosen', 'Shared Designation']);
    await expect(page.getByLabel(L.en.grade).locator('option')).toHaveText(['Not chosen', 'GS - Shared']);
    await expect(page.getByLabel('Branch').locator('option')).toHaveText(['Not chosen']);
    await expect(page.getByLabel('Department').locator('option')).toHaveText(['Not chosen', 'Shared Department']);
  });

  test('switching company drops invalid org refs and old template defaults but preserves an accessible manager', async ({ page }) => {
    const api = await bootHire(page, { org: orgByCompany, hireTemplates: [templateA, templateB] });
    await page.getByLabel(L.en.name).fill('Alex Morgan');
    await page.locator('#hire-companyId').selectOption(company.id);
    await page.getByLabel(L.en.desig).selectOption('desig-a');
    await page.getByLabel('Branch').selectOption('branch-a');
    await page.getByLabel('Hire template').selectOption(templateA.id);
    await expect(page.getByText('Applied: Template A')).toBeVisible();
    await page.getByLabel('Line manager').selectOption('18');

    await page.locator('#hire-companyId').selectOption(companyB.id);
    await expect(page.getByLabel(L.en.desig)).toHaveValue('');
    await expect(page.getByLabel(L.en.grade)).toHaveValue('');
    await expect(page.getByLabel('Branch')).toHaveValue('');
    await expect(page.getByLabel('Department')).toHaveValue('');
    await expect(page.getByLabel('Line manager')).toHaveValue('18');
    await expect(page.getByText('Applied: Template A')).toHaveCount(0);

    await page.getByRole('button', { name: L.en.save, exact: true }).click();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[0].body).toMatchObject({ companyId: companyB.id, reportingManagerEmployeeId: 18 });
    expect(api.creates[0].body).not.toHaveProperty('designationId');
    expect(api.creates[0].body).not.toHaveProperty('gradeId');
    expect(api.creates[0].body).not.toHaveProperty('branchId');
    expect(api.creates[0].body).not.toHaveProperty('departmentId');
    expect(api.creates[0].body).not.toHaveProperty('hireTemplateId');
    expect(api.creates[0].body).not.toHaveProperty('workLocation');
  });

  test('shared designation, grade and department survive a company switch', async ({ page }) => {
    await bootHire(page, { org: orgByCompany });
    await page.getByLabel(L.en.desig).selectOption('desig-shared');
    await page.getByLabel('Department').selectOption('dept-shared');
    await page.locator('#hire-companyId').selectOption(companyB.id);
    await expect(page.getByLabel(L.en.desig)).toHaveValue('desig-shared');
    await expect(page.getByLabel(L.en.grade)).toHaveValue('grade-shared');
    await expect(page.getByLabel('Department')).toHaveValue('dept-shared');
  });

  test('company switch drops old grade acknowledgements even when a shared grade survives', async ({ page }) => {
    const api = await bootHire(page, {
      org: orgByCompany,
      hireTemplates: [templateA, templateB],
      warnFirstCreate: true,
      warnings: [{ code: 'grade_band_out_of_range', field: 'gradeId' }],
    });
    await page.getByLabel(L.en.name).fill('Shared Grade Person');
    await page.getByLabel(L.en.start).fill('2026-11-01');
    await page.locator('#hire-companyId').selectOption(company.id);
    await page.getByLabel(L.en.desig).selectOption('desig-shared');
    await page.getByLabel('Hire template').selectOption(templateA.id);
    await page.getByRole('button', { name: L.en.save, exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Confirm before saving' });
    await dialog.getByLabel('Reason').fill('Approved against the first company pay package');
    await dialog.getByRole('button', { name: 'Confirm and save' }).click();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');

    await page.locator('#hire-companyId').selectOption(companyB.id);
    await expect(page.getByLabel(L.en.grade)).toHaveValue('grade-shared');
    await page.getByRole('button', { name: L.en.save, exact: true }).click();
    await expect.poll(() => api.puts.length).toBeGreaterThan(0);
    const request = api.puts.at(-1)?.request as { acknowledgedWarnings?: Array<{ code: string }> } | undefined;
    expect(request?.acknowledgedWarnings ?? []).not.toContainEqual(expect.objectContaining({ code: 'grade_band_out_of_range' }));
  });

  test('old-company template choices disappear while the new company list is loading', async ({ page }) => {
    let releaseB!: () => void;
    const holdB = new Promise<void>(resolve => { releaseB = resolve; });
    await bootHire(page, { org: orgByCompany, hireTemplates: [templateA, templateB], holdHireTemplatesForCompany: { [companyB.id]: holdB } });
    await page.locator('#hire-companyId').selectOption(company.id);
    await expect(page.getByLabel('Hire template')).toContainText('Template A');
    await page.locator('#hire-companyId').selectOption(companyB.id);
    await expect(page.getByLabel('Hire template')).toHaveCount(0);
    releaseB();
    await expect(page.getByLabel('Hire template')).toContainText('Template B');
  });

  test('Arabic grade choices and Review use the Arabic grade name', async ({ page }) => {
    await bootHire(page, { locale: 'ar', org: { ...orgByCompany, companies: [company] } });
    await page.getByLabel(L.ar.desig).selectOption('desig-a');
    await expect(page.getByLabel(L.ar.grade).locator('option:checked')).toHaveText('G5 - محترف');
    await page.getByLabel(L.ar.name).fill('Alex Morgan');
    await next(page, L.ar);
    await next(page, L.ar);
    await next(page, L.ar);
    await expect(page.getByText('G5 - محترف', { exact: true })).toBeVisible();
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
