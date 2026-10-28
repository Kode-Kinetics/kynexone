import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import { bootHire, expectNoHorizontalScroll, realDraft } from './hire/hireMocks';

/**
 * Browser Pilot for the consultant-audit and security fixes (CD26-CD35, CD42) on the hire wizard (hire_wizard_v2), mocked API.
 * Local: next dev -p 5174, then playwright test -c e2e/playwright.employee-hire.config.ts. CI collects it in the chromium project.
 */
const L = {
  en: { name: 'English full name', start: 'Start date', desig: 'Designation', next: 'Next', save: 'Save draft', back: 'Back', nat: /Nationality/, create: 'Create employee draft', docs: 'Documents' },
  ar: { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', next: 'التالي', save: 'حفظ كمسودة', back: 'السابق', nat: /الجنسية/, create: 'إنشاء مسودة الموظف', docs: 'المستندات' },
} as const;

async function basics(page: Page, l: typeof L.en | typeof L.ar = L.en, over: { name?: string; start?: string } = {}) {
  await page.getByLabel(l.name).fill(over.name ?? 'Alex Morgan');
  await page.getByLabel(l.start).fill(over.start ?? '2026-11-01');
  await page.getByLabel(l.desig).selectOption({ label: 'Specialist' });
}
const next = (page: Page, l: typeof L.en | typeof L.ar = L.en) => page.getByRole('button', { name: l.next, exact: true }).click();
const saveDraft = (page: Page, l: typeof L.en | typeof L.ar = L.en) => page.getByRole('button', { name: l.save, exact: true }).click();
async function pickNationality(page: Page, text: string, re: RegExp, l: typeof L.en | typeof L.ar = L.en) {
  await page.getByRole('combobox', { name: l.nat }).fill(text);
  await page.getByRole('listbox').getByRole('option', { name: re }).first().click();
}
async function toStep(page: Page, n: 2 | 3 | 4, l: typeof L.en | typeof L.ar = L.en) { await basics(page, l); for (let i = 1; i < n; i++) await next(page, l); }

test.describe('CD26 work email, contract end date, probation, wps/eosb (payload)', () => {
  test('OD7: the work email is one full address, sent exactly as typed (any domain)', async ({ page }) => {
    const api = await bootHire(page);
    await basics(page);
    await page.getByLabel('Work email').fill('alex.morgan@issued.example');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[0].body.workEmail).toBe('alex.morgan@issued.example');
  });

  test('CD17: the contract end date is disabled until the contract is fixed-term; it is checked against the start date and cleared when the type changes', async ({ page }) => {
    const api = await bootHire(page);
    await toStep(page, 3);
    const end = page.getByLabel('Contract end date');
    await expect(end).toBeDisabled();
    await page.getByLabel('Contract type').selectOption({ label: 'Fixed term' });
    await expect(end).toBeEnabled();
    await end.fill('2026-10-01');
    await next(page);
    await expect(page.locator('#hire-contractEndDate-error')).toHaveText('The contract end date must be after the start date.');
    await end.fill('2027-11-01');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[0].body).toMatchObject({ contractType: 'Fixed term', contractEndDate: '2027-11-01' });
    await page.getByLabel('Contract type').selectOption({ label: 'Permanent' });
    await expect(end).toBeDisabled();
    await expect(end).toHaveValue('');
  });

  test('typing an IBAN and MOL ID does not set the WPS and EOSB flags', async ({ page }) => {
    const api = await bootHire(page);
    await toStep(page, 3);
    await page.getByLabel('IBAN').fill('SA03 8000 0000 6080 1016 7519');
    await page.getByLabel('MOL ID').fill('1-123456');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    const pp = api.creates[0].body.payrollProfile;
    expect(pp).toMatchObject({ iban: 'SA0380000000608010167519', molId: '1-123456' });
    expect(pp).not.toHaveProperty('wpsEligible');
    expect(pp).not.toHaveProperty('eosbEligible');
  });

  test('CD31: a probation end date field; the 180-day warning arrives through the acknowledge dialog and the reason is sent back', async ({ page }) => {
    const api = await bootHire(page, { warnFirstCreate: true, warnings: [{ code: 'probation_over_limit', field: 'probationEndDate' }] });
    await toStep(page, 3);
    await page.getByLabel('Probation end date').fill('2027-06-30');
    await saveDraft(page);
    const dlg = page.getByRole('dialog', { name: 'Confirm before saving' });
    await expect(dlg).toContainText('The probation period is longer than 180 days.');
    await dlg.getByLabel('Reason').fill('Senior hire with a long probation');
    await dlg.getByRole('button', { name: 'Confirm and save' }).click();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates).toHaveLength(2);
    expect(api.creates[1].body).toMatchObject({ probationEndDate: '2027-06-30', acknowledgedWarnings: [{ code: 'probation_over_limit', reason: 'Senior hire with a long probation' }] });
  });
});

test.describe('CD26 error sentences say the true thing', () => {
  test('date of birth: future and over 100 are different hard errors; 14 years old is not a client error', async ({ page }) => {
    await bootHire(page);
    await toStep(page, 2);
    const dob = page.getByLabel('Date of birth');
    await dob.fill('2999-01-01'); await dob.blur();
    await expect(page.locator('#hire-dob-error')).toHaveText('Date of birth cannot be in the future.');
    await dob.fill('1920-01-01'); await dob.blur();
    await expect(page.locator('#hire-dob-error')).toHaveText('Date of birth cannot be more than 100 years ago.');
    await dob.fill('2015-01-01'); await dob.blur();
    await expect(page.locator('#hire-dob-error')).toHaveCount(0);
  });

  test('IBAN: a wrong checksum and a wrong shape are different sentences', async ({ page }) => {
    await bootHire(page);
    await toStep(page, 3);
    const iban = page.getByLabel('IBAN');
    await iban.fill('SA0380000000608010167518'); await iban.blur();
    await expect(page.locator('#hire-iban-error')).toHaveText('This IBAN is not valid. Check the digits.');
    await iban.fill('SA03800000006080101675'); await iban.blur();
    await expect(page.locator('#hire-iban-error')).toHaveText('Saudi IBANs have 24 characters and start with SA.');
  });

  test('IBAN follows the company country: a UAE company accepts a UAE IBAN and the hint does not say Saudi', async ({ page }) => {
    await bootHire(page, { companyCountry: 'AE' });
    await toStep(page, 3);
    const iban = page.getByLabel('IBAN');
    await iban.fill('AE07 0331 2345 6789 0123 456'); await iban.blur();
    await expect(page.locator('#hire-iban-error')).toHaveCount(0);
    await expect(page.locator('#hire-iban-hint')).not.toContainText('Saudi');
    await expect(page.getByTestId('iban-bank')).toHaveCount(0);
  });

  test('the bank name is shown from the SAMA code', async ({ page }) => {
    await bootHire(page);
    await toStep(page, 3);
    await page.getByLabel('IBAN').fill('SA03 8000 0000 6080 1016 7519');
    await expect(page.getByTestId('iban-bank')).toContainText('Al Rajhi Bank');
  });

  test('a server value_too_long names the field and its own limit', async ({ page }) => {
    await bootHire(page, { createErrors: { workLocation: ['value_too_long'], 'payrollProfile.molId': ['value_too_long'] } });
    await toStep(page, 3);
    await page.getByLabel('Work location').fill('Somewhere');
    await saveDraft(page);
    const summary = page.getByTestId('error-summary');
    await expect(summary).toContainText('Use 120 characters or fewer in Work location.');
    await expect(summary).toContainText('Use 40 characters or fewer in MOL ID.');
    await expect(summary).not.toContainText('150');
  });
});

test.describe('CD26 duplicate panel and rehire copy', () => {
  test('a name-only match says so, with a readable status and the company name', async ({ page }) => {
    await bootHire(page, { duplicate: { status: 'Active', matchType: 'possible', signals: ['Same name'] } });
    await page.getByLabel('English full name').fill('Alex Morgan');
    await page.getByLabel('English full name').blur();
    const panel = page.getByRole('region', { name: 'Possible match' });
    await expect(panel).toContainText('Same name — check before continuing.');
    await expect(panel).toContainText('Active, Hire Test Company');
    await expect(panel).toContainText('Joined in 2021');
    await expect(panel).not.toContainText('2,021');
    await expect(panel).not.toContainText('TEST-044');
    await expect(panel).not.toContainText('Riyadh');
  });

  test('Arabic: the status and the company are in Arabic', async ({ page }) => {
    await bootHire(page, { locale: 'ar', duplicate: { status: 'Terminated', matchType: 'strong' } });
    await page.getByLabel(L.ar.name).fill('Alex Morgan');
    await page.getByLabel(L.ar.name).blur();
    const panel = page.getByRole('region', { name: 'تطابق محتمل' });
    await expect(panel).toContainText('منتهي الخدمة');
    await expect(panel).toContainText('انضم في 2021');
    await expect(panel).toContainText('شركة اختبار، انضم في 2021');   // Arabic comma joins status and year
    await expect(panel).not.toContainText('·');
    await expect(panel).not.toContainText('٠');
    await expect(panel).toContainText('شركة اختبار');
    await expect(panel).not.toContainText('Terminated');
  });

  test('after Rehire the success step says rehired, not "added as a draft"', async ({ page }) => {
    await bootHire(page, { duplicate: { status: 'Terminated', matchType: 'strong' } });
    await basics(page);
    await page.getByLabel('English full name').blur();
    const panel = page.getByRole('region', { name: 'Possible match' });
    await panel.getByRole('button', { name: 'Rehire this person' }).click();
    await panel.getByLabel('Reason').fill('Returning after a career break');
    await page.getByRole('button', { name: 'Confirm rehire' }).click();
    await expect(page.getByRole('heading', { name: 'Alex Morgan has been rehired.' })).toBeVisible();
    await expect(page.getByText('added as a draft')).toHaveCount(0);
  });
});

test.describe('lead review: refresh resumes the same draft', () => {
  test('after the first save the URL carries ?draft=<id>; a reload hydrates that draft and posts no second create', async ({ page }) => {
    const detail = realDraft({ id: 901, designationId: 'desig-1', gradeId: 'grade-g5' });
    const api = await bootHire(page, { draftDetail: detail });
    await basics(page);
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    await expect(page).toHaveURL(/\/people\/new\?draft=901$/);
    expect(api.creates).toHaveLength(1);
    await page.reload();
    await expect(page.getByLabel('English full name')).toHaveValue('Alex Morgan');
    expect(api.gets).toContain('/api/employees/901');
    expect(api.creates).toHaveLength(1);
  });
});

test.describe('lead review: no Self-Service promise on Review', () => {
  test('neutral "before activation" copy, EN and AR', async ({ page }) => {
    await bootHire(page);
    await basics(page); await next(page); await next(page); await next(page);
    await expect(page.getByText('You can leave this for now and add it later from the employee record.')).toBeVisible();
    await expect(page.getByText(/Self-Service/i)).toHaveCount(0);
  });
  test('AR', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await basics(page, L.ar); await next(page, L.ar); await next(page, L.ar); await next(page, L.ar);
    await expect(page.getByText('يمكنك تركه الآن وإضافته لاحقًا من ملف الموظف.')).toBeVisible();
    await expect(page.getByText('الخدمة الذاتية')).toHaveCount(0);
  });
});

test.describe('CD26 draft resume (/people/new?draft=<id>)', () => {
  const detail = realDraft({
    id: 44, nationality: 'Pakistan', nationalityCode: 'PK', dateOfBirth: '1990-06-01', workEmail: 'alex.morgan@example.test',
    designationId: 'desig-1', gradeId: 'grade-g5', employmentType: 'Full-time', contractType: 'Permanent', iqamaNumber: '2000000006', iqamaExpiryDate: '2027-01-01T00:00:00Z',
  });

  test('the draft is loaded through the employee GET, the form is filled, and autosave continues on the same id', async ({ page }) => {
    const api = await bootHire(page, { draftDetail: detail }, '/people/new?draft=44');
    await expect(page.getByLabel('English full name')).toHaveValue('Alex Morgan');
    await expect(page.getByLabel('Start date')).toHaveValue('2026-11-01');
    await expect(page.getByLabel('Work email')).toHaveValue('alex.morgan@example.test');
    expect(api.gets).toContain('/api/employees/44');
    await page.getByLabel('English full name').fill('Alex J Morgan');
    await expect.poll(() => api.puts.length, { timeout: 6000 }).toBe(1);
    expect(api.puts[0].baseUpdatedAtUtc).toBe('2026-10-27T08:00:00Z');
    expect(api.puts[0].request.englishName).toBe('Alex J Morgan');
    expect(api.creates).toHaveLength(0);
    await next(page); await next(page);
    await expect(page.getByLabel('ID number')).toHaveValue('2000000006');
  });

  test('the real API shape: nationality is restored, the start date is a plain date, and the next autosave keeps both (no conflict)', async ({ page }) => {
    const api = await bootHire(page, { draftDetail: realDraft({ id: 44, workEmail: 'alex.morgan@example.test' }) }, '/people/new?draft=44');
    await expect(page.getByLabel('English full name')).toHaveValue('Alex Morgan');
    await expect(page.getByLabel('Start date')).toHaveValue('2026-11-01');
    await page.getByLabel('English full name').fill('Alex J Morgan');
    await expect.poll(() => api.puts.length, { timeout: 6000 }).toBe(1);
    expect(api.puts[0].request).toMatchObject({ nationalityCode: 'EG', joiningDate: '2026-11-01' });
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    await next(page);
    await expect(page.getByRole('combobox', { name: L.en.nat })).toHaveValue(/Egypt/);
  });

  test('a 409 that is a warning, not a conflict, is answered in a dialog and autosave carries on', async ({ page }) => {
    const api = await bootHire(page, { draftDetail: realDraft({ id: 44, workEmail: 'alex.morgan@example.test' }), warnOnFirstPut: true }, '/people/new?draft=44');
    await page.getByLabel('English full name').fill('Alex J Morgan');
    await expect.poll(() => api.puts.length, { timeout: 6000 }).toBe(1);
    await expect(page.getByRole('dialog', { name: 'Confirm before saving' })).toBeVisible();
    await expect(page.getByText('This draft was changed somewhere else')).toHaveCount(0);
    await page.getByRole('dialog', { name: 'Confirm before saving' }).getByRole('button', { name: 'Cancel' }).click();
    await page.getByLabel('English full name').fill('Alex K Morgan');
    await expect.poll(() => api.puts.length, { timeout: 6000 }).toBe(2);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
  });

  for (const status of [404, 403] as const) {
    test(`${status}: a friendly message, no form data, no autosave`, async ({ page }) => {
      const api = await bootHire(page, { draftDetail: status }, '/people/new?draft=44');
      await expect(page.getByRole('alert').filter({ hasText: 'This draft could not be found' })).toContainText('This draft could not be found, or you do not have access to it.');
      await expect(page.getByRole('link', { name: 'Start a new employee' })).toBeVisible();
      await expect(page.getByLabel('English full name')).toHaveCount(0);
      expect(api.puts).toHaveLength(0);
    });
  }

  test('a record that is no longer a draft is not opened for editing', async ({ page }) => {
    const api = await bootHire(page, { draftDetail: { ...detail, status: 'Active' } }, '/people/new?draft=44');
    await expect(page.getByRole('alert').filter({ hasText: /\w/ })).toContainText('This employee is no longer a draft.');
    await expect(page.getByRole('link', { name: 'Open the record' })).toBeVisible();
    await expect(page.getByLabel('English full name')).toHaveCount(0);
    expect(api.puts).toHaveLength(0);
  });
});

test.describe('CD27 Border Number and the expat passport row', () => {
  test('Border Number is an ID type', async ({ page }) => {
    await bootHire(page);
    await toStep(page, 3);
    await expect(page.getByLabel('ID type').locator('option', { hasText: 'Border Number' })).toHaveCount(1);
  });

  test('an expat sees the passport number and expiry as a second row, and both go out as compliance records; a Saudi does not', async ({ page }) => {
    const api = await bootHire(page);
    await basics(page); await next(page);
    await pickNationality(page, 'paki', /Pakistan/);
    await next(page);
    await expect(page.getByLabel('ID type')).toHaveValue('iqama');
    await page.getByLabel('ID number').fill('2000000006');
    await page.getByLabel('ID expiry date').fill('2027-01-01');
    await page.getByLabel('Passport number').fill('ab1234567');
    await page.getByLabel('Passport expiry date').fill('2030-05-05');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates[0].body.complianceRecords).toEqual([
      expect.objectContaining({ fieldKey: 'iqama_number', fieldValue: '2000000006', expiryDate: '2027-01-01' }),
      expect.objectContaining({ fieldKey: 'passport_number', fieldValue: 'AB1234567', expiryDate: '2030-05-05' }),
    ]);
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await pickNationality(page, 'sau', /Saudi Arabia/);
    await next(page);
    await expect(page.getByLabel('Passport number')).toHaveCount(0);
  });
});

test.describe('CD32 override hint', () => {
  test('without employees.override_validation a bad checksum says an HR Manager can approve it', async ({ page }) => {
    await bootHire(page, { permissions: ['employees.read', 'employees.write'] });
    await basics(page); await next(page);
    await pickNationality(page, 'sau', /Saudi Arabia/);
    await next(page);
    await page.getByLabel('ID number').fill('1000000009');
    await page.getByLabel('ID number').blur();
    await expect(page.getByText('An HR Manager can approve this exception.')).toBeVisible();
  });
  test('with the permission the hint is not shown (the reason field is)', async ({ page }) => {
    await bootHire(page);
    await basics(page); await next(page);
    await pickNationality(page, 'sau', /Saudi Arabia/);
    await next(page);
    await page.getByLabel('ID number').fill('1000000009');
    await page.getByLabel('ID number').blur();
    await expect(page.getByText('An HR Manager can approve this exception.')).toHaveCount(0);
    await expect(page.getByLabel('Reason to use this number anyway')).toBeVisible();
  });
  test('a warning that needs permission shows the hint in the dialog', async ({ page }) => {
    await bootHire(page, { permissions: ['employees.read', 'employees.write'], warnFirstCreate: true, warnings: [{ code: 'under_minimum_age', field: 'dateOfBirth', requiresPermission: true }] });
    await basics(page);
    await saveDraft(page);
    const dlg = page.getByRole('dialog', { name: 'Confirm before saving' });
    await expect(dlg.getByText('An HR Manager can approve this exception.')).toBeVisible();
  });
});

test.describe('CD34 Review and Success', () => {
  async function fillForReview(page: Page, l: typeof L.en | typeof L.ar) {
    await basics(page, l, { start: '2026-01-05' });
    await page.getByLabel(l === L.en ? 'Work email' : 'البريد الإلكتروني الوظيفي').fill('alex.morgan@example.test');
    await next(page, l);
    await pickNationality(page, l === L.en ? 'paki' : 'باك', /Pakistan|باكستان/, l);
    await next(page, l);
    await page.getByLabel(l === L.en ? 'Contract type' : 'نوع العقد').selectOption({ index: 2 });
    await page.getByLabel(l === L.en ? 'ID number' : 'رقم الهوية').fill('2000000006');
    await page.getByLabel(l === L.en ? 'ID expiry date' : 'تاريخ انتهاء الهوية').fill('2027-01-01');
    await page.getByLabel(l === L.en ? 'Passport number' : 'رقم جواز السفر').fill('AB1234567');
    await page.getByLabel(l === L.en ? 'Passport expiry date' : 'تاريخ انتهاء جواز السفر').fill('2030-05-05');
    await page.getByLabel(l === L.en ? 'IBAN' : 'الآيبان').fill('SA03 8000 0000 6080 1016 7519');
    await next(page, l);
  }

  test('EN: dates are localised with a Hijri line beside ID and passport expiries; enums, work email, bank name and the warnings list are shown', async ({ page }) => {
    await bootHire(page);
    await fillForReview(page, L.en);
    const body = page.locator('main, body').first();
    await expect(body).toContainText('5 January 2026');
    await expect(body).not.toContainText('2026-01-05');
    await expect(page.getByTestId('review-hijri-idExpiry')).toContainText(/\d{4}/);
    await expect(page.getByTestId('review-hijri-passportExpiry')).toContainText(/\d{4}/);
    await expect(page.getByRole('region', { name: 'Documents' })).toContainText('1 January 2027');
    await expect(page.getByRole('region', { name: 'Job and pay' })).toContainText('Al Rajhi Bank');
    await expect(page.getByRole('region', { name: 'Hire basics' })).toContainText('alex.morgan@example.test');
    const warns = page.getByRole('region', { name: 'Warnings you will be asked to confirm' });
    await expect(warns).toContainText('The start date is more than 30 days in the past.');
  });

  test('AR: dates and enum values are Arabic', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await fillForReview(page, L.ar);
    const docs = page.getByRole('region', { name: 'المستندات' });
    await expect(docs).not.toContainText('2027-01-01');
    await expect(docs).toContainText(/[؀-ۿ]/);
    await expect(page.getByTestId('review-hijri-idExpiry')).toContainText('هـ');
    const job = page.getByRole('region', { name: 'الوظيفة والراتب' });
    await expect(job).not.toContainText('Fixed term');
    await expect(job).toContainText('مصرف الراجحي');
  });

  test('the readiness list renders raw pay: and data: keys through t(), EN and AR', async ({ page }) => {
    const review = { blockers: [{ key: 'pay:compensationUnapproved', label: 'pay:compensationUnapproved' }, { key: 'pay:ibanMissing', label: 'pay:ibanMissing' }], warnings: [{ key: 'data:joining_backdated', label: 'data:joining_backdated' }], notProvided: [] };
    await bootHire(page, { review });
    await basics(page);
    await next(page); await next(page); await next(page);
    await page.getByRole('button', { name: 'Create employee draft' }).click();
    const ready = page.getByTestId('readiness');
    await expect(ready).toContainText('The start date is more than 30 days in the past.');
    await expect(ready).not.toContainText(/pay:|data:/);
  });
  test('AR success step has no raw keys', async ({ page }) => {
    const review = { blockers: [{ key: 'pay:ibanMissing', label: 'pay:ibanMissing' }], warnings: [{ key: 'data:joining_backdated', label: 'data:joining_backdated' }], notProvided: [] };
    await bootHire(page, { locale: 'ar', review });
    await basics(page, L.ar);
    await next(page, L.ar); await next(page, L.ar); await next(page, L.ar);
    await page.getByRole('button', { name: L.ar.create }).click();
    const ready = page.getByTestId('readiness');
    await expect(ready).toBeVisible();
    await expect(ready).not.toContainText(/pay:|data:/);
    await expect(ready).toContainText('تاريخ المباشرة');
  });
});

test.describe('CD34 Arabic copy in the form', () => {
  test('employee code and the domain hint', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await expect(page.getByLabel('الرقم الوظيفي')).toBeVisible();
    const hint = page.locator('#hire-workEmail-hint');
    await expect(hint).toContainText('الخدمة الذاتية');
    await expect(hint).toContainText('تسجيل الدخول');
    await expect(hint).not.toContainText('كاينكس');
  });
  test('the MOL ID label', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await toStep(page, 3, L.ar);
    await expect(page.getByLabel('رقم المنشأة في وزارة الموارد البشرية')).toBeVisible();
  });
});

test.describe('CD35 screenshots (EN 1440, AR 360)', () => {
  const dir = process.env.HIRE_SCREENS_DIR;
  test.skip(!dir, 'set HIRE_SCREENS_DIR to capture');
  const detail = realDraft({
    id: 44, arabicName: 'أليكس مورغان', nationality: 'Pakistan', nationalityCode: 'PK', dateOfBirth: '1990-06-01', workEmail: 'alex.morgan@example.test',
    designationId: 'desig-1', gradeId: 'grade-g5', employmentType: 'Full-time', contractType: 'Permanent', iqamaNumber: '2000000006', iqamaExpiryDate: '2027-01-01T00:00:00Z',
  });
  for (const [w, h, locale] of [[1440, 1000, 'en'], [360, 780, 'ar']] as const) {
    const l = L[locale];
    const shot = (page: Page, n: string) => { fs.mkdirSync(dir!, { recursive: true }); return page.screenshot({ path: `${dir}/hire-h-${n}-${locale}-${w}.png`, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' }); };
    const duplicateScreen = async (page: Page, name: string, duplicate: Parameters<typeof bootHire>[1] extends infer O ? NonNullable<O extends { duplicate?: infer D } ? D : never> : never) => {
      await bootHire(page, { locale, duplicate });
      await page.getByLabel(l.name).fill('Alex Morgan');
      await page.getByLabel(l.name).blur();
      await expect(page.locator('section[aria-label]').first()).toBeVisible();
      await shot(page, name);
    };
    test.describe(`${locale} ${w}`, () => {
      test.use({ viewport: { width: w, height: h } });
      test('duplicate panel: name only', async ({ page }) => { await duplicateScreen(page, 'dup-name', { status: 'Active', matchType: 'possible', signals: ['Same name'] }); });
      test('duplicate panel: ID match', async ({ page }) => { await duplicateScreen(page, 'dup-id', { status: 'Terminated', matchType: 'strong', signals: ['Iqama number match'] }); });
      test('warning acknowledge dialog', async ({ page }) => {
        await bootHire(page, { locale, warnFirstCreate: true, warnings: [{ code: 'joining_backdated_over_30d', field: 'joiningDate' }, { code: 'probation_over_limit', field: 'probationEndDate' }] });
        await basics(page, l, { start: '2026-01-05' });
        await saveDraft(page, l);
        await expect(page.getByRole('dialog')).toBeVisible();
        await shot(page, 'warning-dialog');
      });
      test('success: new hire', async ({ page }) => {
        await bootHire(page, { locale, review: { blockers: [{ key: 'pay:compensationUnapproved', label: '' }], warnings: [{ key: 'data:joining_backdated', label: '' }], notProvided: [] } });
        await basics(page, l); await next(page, l); await next(page, l); await next(page, l);
        await page.getByRole('button', { name: l.create }).click();
        await expect(page.getByTestId('readiness')).toBeVisible();
        await page.waitForTimeout(300);
        await shot(page, 'success-new');
      });
      test('success: rehire', async ({ page }) => {
        await bootHire(page, { locale, duplicate: { status: 'Terminated', matchType: 'strong', signals: ['Iqama number match'] } });
        await basics(page, l);
        await page.getByLabel(l.name).blur();
        const panel = page.locator('section[aria-label]').first();
        await panel.getByRole('button', { name: locale === 'en' ? 'Rehire this person' : 'إعادة تعيين هذا الشخص' }).click();
        await panel.getByRole('textbox').fill('Returning after a career break');
        await page.getByRole('button', { name: locale === 'en' ? 'Confirm rehire' : 'تأكيد إعادة التعيين' }).click();
        await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
        await page.waitForTimeout(400);
        await shot(page, 'success-rehire');
      });
      test('open nationality combobox', async ({ page }) => {
        await bootHire(page, { locale });
        await basics(page, l); await next(page, l);
        await page.getByRole('combobox', { name: l.nat }).fill(locale === 'en' ? 'ar' : 'ال');
        await expect(page.getByRole('listbox')).toBeVisible();
        await shot(page, 'nationality-open');
      });
      test('resumed draft', async ({ page }) => {
        await bootHire(page, { locale, draftDetail: detail }, '/people/new?draft=44');
        await expect(page.getByLabel(l.name)).toHaveValue('Alex Morgan');
        await page.waitForTimeout(300);
        await shot(page, 'resumed-draft');
      });
      test('review with Hijri lines', async ({ page }) => {
        await bootHire(page, { locale, draftDetail: detail }, '/people/new?draft=44');
        await expect(page.getByLabel(l.name)).toHaveValue('Alex Morgan');
        await next(page, l); await next(page, l); await next(page, l);
        await expect(page.getByTestId('review-hijri-idExpiry')).toBeVisible();
        await expectNoHorizontalScroll(page);
        await page.getByRole('region', { name: l.docs }).scrollIntoViewIfNeeded();
        await page.evaluate(() => window.scrollBy(0, 160));
        await shot(page, 'review-hijri');
      });
    });
  }
});
