import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import { bootHire, expectNoHorizontalScroll } from './hire/hireMocks';

/**
 * Browser Pilot: the hire wizard (hire_wizard_v2) against a mocked API. Runs in the main `chromium` project
 * (CI: `npx playwright test --project=chromium`) because that project collects every e2e/*.spec.ts that is not ignored.
 * Local: next dev -p 5174, then playwright test -c e2e/playwright.employee-hire.config.ts.
 */
const STEPS = ['Hire basics', 'Person and contact', 'Job, pay and documents', 'Review and create'];

async function basics(page: Page, over: { name?: string; start?: string; designation?: string } = {}) {
  await page.getByLabel('English full name').fill(over.name ?? 'Alex Morgan');
  await page.getByLabel('Start date').fill(over.start ?? '2026-11-01');
  await page.getByLabel('Designation').selectOption({ label: over.designation ?? 'Specialist' });
}
const next = (page: Page) => page.getByRole('button', { name: 'Next', exact: true }).click();
const saveDraft = (page: Page) => page.getByRole('button', { name: 'Save draft', exact: true }).click();
const heading = (page: Page, name: string) => page.getByRole('heading', { level: 2, name, exact: true });

test.describe('flag', () => {
  test('G31: hire_wizard_v2 OFF renders the legacy wizard, ON renders the new one', async ({ page }) => {
    await bootHire(page, { flagOff: true });
    await expect(page.getByRole('dialog', { name: 'Add Employee', exact: true })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Progress' })).toHaveCount(0);
  });
  test('G31: ON shows the new wizard and not the legacy dialog', async ({ page }) => {
    await bootHire(page);
    await expect(page.getByRole('heading', { level: 1, name: 'Add employee' })).toBeVisible();
    await expect(page.getByRole('dialog', { name: 'Add Employee', exact: true })).toHaveCount(0);
  });
});

test.describe('quick hire (desktop)', () => {
  test('AC1: four steps, one array, no hidden step is reachable', async ({ page }) => {
    await bootHire(page);
    const items = page.getByRole('list', { name: 'Progress' }).getByRole('listitem');
    await expect(items).toHaveCount(4);
    for (let i = 0; i < 4; i++) await expect(items.nth(i)).toContainText(STEPS[i]);
    await expect(items.nth(0)).toHaveAttribute('aria-current', 'step');
    await expect(page.getByRole('list', { name: 'Progress' }).getByRole('button')).toHaveCount(0);
  });

  test('AC5/CD21: start date is empty; it is a neutral "Needed before activation" hint and never blocks Next', async ({ page }) => {
    await bootHire(page);
    await expect(page.getByLabel('Start date')).toHaveValue('');
    await expect(page.locator('#hire-startDate-hint')).toHaveText('Needed before activation');
    await page.getByLabel('English full name').fill('Alex Morgan');
    await next(page);
    await expect(heading(page, STEPS[1])).toBeVisible();
    await expect(page.getByTestId('error-summary')).toHaveCount(0);
    await expect(page.locator('#hire-startDate-error')).toHaveCount(0);
  });

  test('CD21: with only name and company, Review lists start date and designation under "Before activation" and Create still works', async ({ page }) => {
    const api = await bootHire(page);
    await page.getByLabel('English full name').fill('Alex Morgan');
    await next(page); await next(page); await next(page);
    const before = page.getByText('Before activation');
    await expect(before).toBeVisible();
    await expect(page.getByText('Start date').last()).toBeVisible();
    await page.getByRole('button', { name: 'Create employee draft' }).click();
    await expect(page.getByRole('heading', { name: 'Alex Morgan has been added as a draft.' })).toBeVisible();
    expect(api.creates[0].body.joiningDate).toBeUndefined();
  });

  test('AC3: one company is preselected and read-only', async ({ page }) => {
    await bootHire(page);
    const c = page.getByLabel('Company');
    await expect(c).toBeDisabled();
    await expect(c).toHaveValue('company-hire');
  });

  test('AC4: several companies and none chosen -> Save draft focuses Company, step does not change', async ({ page }) => {
    await bootHire(page, { multipleCompanies: true });
    await page.getByLabel('English full name').fill('Alex Morgan');
    await saveDraft(page);
    await expect(page.locator('#hire-companyId')).toBeFocused();
    await expect(page.locator('#hire-companyId-error')).toHaveText('Choose the employing company.');
    await expect(heading(page, STEPS[0])).toBeVisible();
  });

  test('AC2/41: draft with the four fields sends one POST with one Idempotency-Key, even on a double click', async ({ page }) => {
    let release!: () => void;
    const hold = new Promise<void>(r => { release = r; });
    const api = await bootHire(page, { holdCreate: hold });
    await basics(page);
    const btn = page.getByRole('button', { name: 'Save draft', exact: true });
    await btn.dblclick();
    await page.waitForTimeout(150);
    release();
    await expect(page.getByTestId('saved-indicator')).toContainText('Saved');
    expect(api.creates).toHaveLength(1);
    expect(api.creates[0].key).toMatch(/^[0-9a-f-]{36}$/);
    const b = api.creates[0].body;
    expect(b).toMatchObject({ englishName: 'Alex Morgan', companyId: 'company-hire', joiningDate: '2026-11-01', designationId: 'desig-1' });
    for (const k of ['arabicName', 'dateOfBirth', 'nationalityCode', 'mobileNumber', 'salaryBreakdown', 'payrollProfile', 'complianceRecords']) expect(b[k]).toBeUndefined();
  });

  test('G03: a 151-character name shows the client error and fires no POST', async ({ page }) => {
    const api = await bootHire(page);
    await basics(page, { name: 'A'.repeat(151) });
    await saveDraft(page);
    await expect(page.locator('#hire-englishName-error')).toHaveText('Use 150 characters or fewer.');
    expect(api.creates).toHaveLength(0);
  });

  test('G04/A11: employee code is read-only until "Set manually"', async ({ page }) => {
    await bootHire(page);
    const code = page.getByLabel('Employee code');
    await expect(code).toHaveAttribute('readonly', '');
    await page.getByRole('button', { name: 'Set manually' }).click();
    await expect(code).not.toHaveAttribute('readonly', '');
  });

  test('G02/AC9: grade that conflicts with the designation shows the conflict', async ({ page }) => {
    await bootHire(page);
    await basics(page);
    await page.getByLabel('Grade').selectOption({ label: 'G6 - Senior' });
    await page.getByLabel('Grade').blur();
    await expect(page.locator('#hire-gradeId-error')).toHaveText('This designation belongs to grade G5.');
  });

  test('G01/AC7: a start date beyond today+2y is an inline error (EN and AR); a date 40 days back is not an error', async ({ page }) => {
    await bootHire(page);
    await page.getByLabel('Start date').fill('2099-01-01');
    await page.getByLabel('Start date').blur();
    await expect(page.locator('#hire-startDate-error')).toContainText(/^Start date must be between \d{4}-\d{2}-\d{2} and \d{4}-\d{2}-\d{2}\.$/);
    const past = new Date(Date.now() - 40 * 86400000).toISOString().slice(0, 10);
    await page.getByLabel('Start date').fill(past);
    await page.getByLabel('Start date').blur();
    await expect(page.locator('#hire-startDate-error')).toHaveCount(0);
  });

  test('G01: the same range error reads in Arabic', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await page.getByLabel('تاريخ المباشرة').fill('2099-01-01');
    await page.getByLabel('تاريخ المباشرة').blur();
    await expect(page.locator('#hire-startDate-error')).toContainText('يجب أن يكون تاريخ المباشرة بين');
  });

  test('AC21: a Hijri line sits beside the start date', async ({ page }) => {
    await bootHire(page);
    await page.getByLabel('Start date').fill('2026-10-14');
    await expect(page.getByTestId('hijri-startDate')).toContainText('1448');
  });
});

test.describe('person, pickers and validation (desktop)', () => {
  async function toStep2(page: Page) { await basics(page); await next(page); await expect(heading(page, STEPS[1])).toBeVisible(); }

  test('AC13-16/G05: nationality picker orders Saudi first, makes no request while typing, rejects free text', async ({ page }) => {
    const api = await bootHire(page);
    await toStep2(page);
    const box = page.getByRole('combobox', { name: /Nationality/ });
    await box.fill('sau');
    await expect(page.getByRole('listbox').getByRole('option').first()).toContainText('Saudi Arabia');
    await box.fill('Suadi');
    await box.blur();
    await expect(page.locator('#hire-nationalityCode-error')).toHaveText('Choose a nationality from the list.');
    expect(api.catalogCalls).toHaveLength(0);
    await box.fill('paki');
    await page.getByRole('listbox').getByRole('option', { name: /Pakistan/ }).click();
    await expect.poll(() => api.catalogCalls.length).toBe(1);
    expect(api.catalogCalls[0]).toContain('nationality=PK');
  });

  test('AC17/18: the ID type follows nationality (Saudi, GCC, expat, non-resident)', async ({ page }) => {
    await bootHire(page);
    await toStep2(page);
    const pick = async (text: string, re: RegExp) => { const b = page.getByRole('combobox', { name: /Nationality/ }); await b.fill(text); await page.getByRole('listbox').getByRole('option', { name: re }).first().click(); };
    await pick('sau', /Saudi Arabia/);
    await next(page);
    await expect(page.getByLabel('ID type')).toHaveValue('nationalId');
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await pick('kuw', /Kuwait/);
    await next(page);
    await expect(page.getByLabel('ID type')).toHaveValue('gccId');
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await pick('paki', /Pakistan/);
    await next(page);
    await expect(page.getByLabel('ID type')).toHaveValue('iqama');
    await page.getByLabel('Resident in Saudi Arabia').uncheck();
    await expect(page.getByLabel('ID type')).toHaveValue('passport');
  });

  test('AC19: a bad ID number shows the specific error with aria-invalid and a resolvable aria-describedby; fixing it clears message and summary (AC29-31, G10)', async ({ page }) => {
    await bootHire(page);
    await toStep2(page);
    const b = page.getByRole('combobox', { name: /Nationality/ });
    await b.fill('sau'); await page.getByRole('listbox').getByRole('option', { name: /Saudi Arabia/ }).click();
    await next(page);
    const id = page.getByLabel('ID number');
    await id.fill('1000000009');
    await next(page);
    const summary = page.getByTestId('error-summary');
    await expect(summary).toBeFocused();
    await expect(summary.getByRole('link', { name: 'This ID number is not valid. Check the digits.' })).toBeVisible();
    await expect(id).toHaveAttribute('aria-invalid', 'true');
    const described = await id.getAttribute('aria-describedby');
    for (const ref of described!.split(' ')) await expect(page.locator(`[id="${ref}"]`)).toHaveCount(1);
    await summary.getByRole('link').first().click();
    await expect(id).toBeFocused();
    await id.fill('1000000008');
    await id.blur();
    await expect(page.locator('#hire-idNumber-error')).toHaveCount(0);
    await expect(id).not.toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByTestId('error-summary')).toHaveCount(0);
  });

  test('AC20/21: date of birth in the future is a hard error; Hijri shows beside it', async ({ page }) => {
    await bootHire(page);
    await toStep2(page);
    await page.getByLabel('Date of birth').fill('1990-06-01');
    await expect(page.getByTestId('hijri-dob')).toContainText(/\d{4}/);
    await page.getByLabel('Date of birth').fill('2999-01-01');
    await page.getByLabel('Date of birth').blur();
    await expect(page.locator('#hire-dob-error')).toHaveText('Date of birth cannot be in the future.');
  });

  test('G06/AC22: a Saudi company preselects +966 and the example starts with 05; the number is stored as E.164 (AC23)', async ({ page }) => {
    const api = await bootHire(page);
    await toStep2(page);
    await expect(page.getByLabel('Mobile country code')).toHaveValue('SA');
    await expect(page.getByLabel('Mobile number')).toHaveAttribute('placeholder', /^05/);
    await page.getByLabel('Mobile number').fill('0512345678');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    expect(api.creates[0].body.mobileNumber).toBe('+966512345678');
  });

  test('AC24/25: bad IBAN and negative allowance are field errors', async ({ page }) => {
    await bootHire(page);
    await toStep2(page); await next(page);
    await page.getByLabel('IBAN').fill('SA03800000006080101675');
    await page.getByLabel('Housing allowance').fill('-5');
    await next(page);
    await expect(page.locator('#hire-iban-error')).toHaveText('Saudi IBANs have 24 characters and start with SA.');
    await expect(page.locator('#hire-housing-error')).toHaveText('Amount cannot be negative.');
  });

  test('AC26/G08: a server warning opens an acknowledge dialog; a short reason is refused; the retry reuses the key', async ({ page }) => {
    const api = await bootHire(page, { warnFirstCreate: true });
    await basics(page, { start: '2026-01-05' });
    await saveDraft(page);
    const dlg = page.getByRole('dialog', { name: 'Confirm before saving' });
    await expect(dlg).toContainText('The start date is more than 30 days in the past.');
    await dlg.getByLabel('Reason').fill('short');
    await dlg.getByRole('button', { name: 'Confirm and save' }).click();
    await expect(dlg.getByText('Enter a reason of at least 10 characters.')).toBeVisible();
    await dlg.getByLabel('Reason').fill('Hired before paperwork was entered');
    await dlg.getByRole('button', { name: 'Confirm and save' }).click();
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    expect(api.creates).toHaveLength(2);
    expect(api.creates[1].key).toBe(api.creates[0].key);
    expect(api.creates[1].body.acknowledgedWarnings).toEqual([{ code: 'joining_backdated', reason: 'Hired before paperwork was entered' }]);
  });
});

test.describe('duplicates, autosave, guard, review (desktop)', () => {
  test('G13/AC43/44: leaving the name field with no DOB or ID checks for a duplicate and shows the panel; "different person" needs a reason', async ({ page }) => {
    const api = await bootHire(page, { duplicate: { status: 'Active' } });
    await page.getByLabel('English full name').fill('Alex Morgan');
    await page.getByLabel('English full name').blur();
    const panel = page.getByRole('region', { name: 'Possible match' });
    await expect(panel).toContainText('Alex Morgan');
    expect(api.dupChecks[0]).toMatchObject({ englishName: 'Alex Morgan', companyId: 'company-hire' });
    expect(api.dupChecks[0].dateOfBirth).toBeUndefined();
    await expect(panel.getByRole('button', { name: 'Rehire this person' })).toHaveCount(0);
    await panel.getByRole('button', { name: 'This is a different person' }).click();
    await panel.getByRole('button', { name: 'Confirm different person' }).click();
    await expect(panel.getByText('Enter a reason of at least 10 characters.')).toBeVisible();
    await panel.getByLabel('Reason').fill('Different date of birth on the ID');
    await panel.getByRole('button', { name: 'Confirm different person' }).click();
    await expect(panel).toContainText('Marked as a different person');
    await page.getByLabel('Start date').fill('2026-11-01');
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    expect(api.creates[0].body).toMatchObject({ acknowledgeDuplicate: true, duplicateOverrideReason: 'Different date of birth on the ID' });
  });

  test('G14/AC45: a terminated match offers Rehire, which posts to the rehire endpoint', async ({ page }) => {
    const api = await bootHire(page, { duplicate: { status: 'Terminated', matchType: 'strong' } });
    await basics(page);
    await page.getByLabel('English full name').blur();
    await page.getByRole('region', { name: 'Possible match' }).getByRole('button', { name: 'Rehire this person' }).click();
    await page.getByRole('region', { name: 'Possible match' }).getByLabel('Reason').fill('Returning after a career break');
    await page.getByRole('button', { name: 'Confirm rehire' }).click();
    await expect.poll(() => api.rehires.length).toBe(1);
    expect(api.rehires[0]).toMatchObject({ startDate: '2026-11-01', designationId: 'desig-1', reason: 'Returning after a career break' });
    expect(api.creates).toHaveLength(0);
  });

  test('AC37/G12: after the draft exists an edit autosaves once, naming the version, and shows "Saved HH:mm" in Riyadh time', async ({ page }) => {
    const api = await bootHire(page);
    await basics(page);
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toHaveText('Saved 11:00');
    await page.getByLabel('English full name').fill('Alex J Morgan');
    await page.getByLabel('English full name').pressSequentially('x');
    await expect.poll(() => api.puts.length, { timeout: 5000 }).toBe(1);
    expect(api.puts[0].baseUpdatedAtUtc).toBe('2026-10-27T08:00:00Z');
    expect(api.puts[0].request.englishName).toBe('Alex J Morganx');
    await expect(page.getByTestId('saved-indicator')).toHaveText('Saved 11:00');
  });

  test('AC40: a 409 on autosave stops it and shows the reload banner', async ({ page }) => {
    const api = await bootHire(page, { conflictOnPut: true });
    await basics(page);
    await saveDraft(page);
    // The draft exists once the saved indicator shows; an edit typed before that is the first version's, not a change.
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    await page.getByLabel('English full name').fill('Alex Morgan Two');
    await expect(page.getByRole('alert').filter({ hasText: 'This draft was changed somewhere else. Reload to continue.' })).toBeVisible({ timeout: 5000 });
    const n = api.puts.length;
    await page.getByLabel('English full name').fill('Alex Morgan Three');
    await page.waitForTimeout(1800);
    expect(api.puts.length).toBe(n);
  });

  test('AC38/39/G12: unsaved work is guarded (unload and in-app links); saved or untouched work never prompts', async ({ page }) => {
    await bootHire(page);
    const prevented = () => page.evaluate(() => { const e = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented; });
    expect(await prevented()).toBe(false);
    await page.getByLabel('English full name').fill('Alex Morgan');
    expect(await prevented()).toBe(true);
    await page.evaluate(() => { const a = document.createElement('a'); a.href = '/people'; a.id = 'probe-link'; a.textContent = 'x'; a.style.cssText = 'position:fixed;top:0;left:0;z-index:99999'; document.body.appendChild(a); });
    await page.locator('#probe-link').click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Cancel' }).click();
    await basics(page);
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    expect(await prevented()).toBe(false);
  });

  test('G11/AC32: a step change moves focus to the step heading; a polite live region announces progress', async ({ page }) => {
    await bootHire(page);
    await basics(page);
    await next(page);
    await expect(heading(page, STEPS[1])).toBeFocused();
    await expect(page.locator('[aria-live="polite"]').filter({ hasText: 'Step 2 of 4' })).toHaveCount(1);
  });

  test('G18/G19/AC51/52: Review lists every section with Edit and "Not provided"; success shows readiness and no sign-in slip', async ({ page }) => {
    await bootHire(page);
    await basics(page);
    await next(page); await next(page); await next(page);
    await expect(heading(page, STEPS[3])).toBeVisible();
    for (const s of ['Hire basics', 'Person and contact', 'Job and pay', 'Documents']) {
      const sec = page.getByRole('region', { name: s });
      await expect(sec.getByRole('button', { name: /^Edit/ })).toBeVisible();
    }
    await expect(page.getByRole('region', { name: 'Person and contact' }).getByText('Not provided').first()).toBeVisible();
    await page.getByRole('button', { name: 'Create employee draft' }).click();
    await expect(page.getByRole('heading', { name: 'Alex Morgan has been added as a draft.' })).toBeVisible();
    await expect(page.getByTestId('readiness')).toBeVisible();
    await expect(page.getByText(/sign-in slip|welcome code/i)).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Add another' })).toBeVisible();
  });

  test('discard dialog replaces the native confirm; discarding a saved draft deletes it', async ({ page }) => {
    const api = await bootHire(page);
    page.on('dialog', d => { throw new Error(`native dialog: ${d.message()}`); });
    await basics(page);
    await saveDraft(page);
    await expect(page.getByTestId('saved-indicator')).toBeVisible();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    const dlg = page.getByRole('alertdialog', { name: 'Leave without saving?' });
    await expect(dlg).toBeVisible();
    await dlg.getByRole('button', { name: 'Discard this draft' }).click();
    await expect.poll(() => api.discards.length).toBe(1);
  });
});

test.describe('closing the reported gaps', () => {
  test('AC27: a displayed amount uses the app formatter (Latin digits, grouping) in Arabic too', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await page.getByLabel('الاسم الكامل بالإنجليزية').fill('Alex Morgan');
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    await page.getByLabel('الراتب الأساسي').fill('12000.5');
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    const row = page.getByRole('region', { name: 'الوظيفة والراتب' });
    await expect(row).toContainText(/12,000\.50/);
    expect(await row.innerText()).not.toMatch(/[٠-٩]/);
  });

  test('AC38: browser Back and a sidebar link both hit the leave guard while work is unsaved', async ({ page }) => {
    await bootHire(page, {}, '/people');
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => (window as unknown as { next: { router: { push: (u: string) => void } } }).next.router.push('/people/new'));
    await expect(page).toHaveURL(/\/people\/new/);
    await page.getByLabel('English full name').fill('Alex Morgan');
    await page.evaluate(() => history.back());
    const dlg = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(dlg).toBeVisible();
    await expect(page).toHaveURL(/\/people\/new/);
    await dlg.getByRole('button', { name: 'Cancel' }).click();
    await expect(dlg).toHaveCount(0);
    await page.getByRole('button', { name: 'Org Chart' }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    await expect(page).toHaveURL(/\/people\/new/);
  });

  test('AC47: a match in a company the user cannot see is masked (no name, no code, no record link)', async ({ page }) => {
    await bootHire(page, { duplicate: { status: 'Active', canView: false } });
    await page.getByLabel('English full name').fill('Alex Morgan');
    await page.getByLabel('English full name').blur();
    const panel = page.getByRole('region', { name: 'Possible match' });
    await expect(panel).toContainText('A person with these details exists in another company you cannot see.');
    await expect(panel.getByRole('link', { name: 'Open record' })).toHaveCount(0);
    await expect(panel.getByRole('button', { name: 'Rehire this person' })).toHaveCount(0);
    await expect(panel).not.toContainText('TEST-');
  });
});

test.describe('Arabic RTL at 360px', () => {
  test.use({ viewport: { width: 360, height: 780 } });

  test('AC33/34/35: Arabic throughout, IBAN/ID/phone/email LTR, no horizontal scroll, action bar reachable', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1, name: 'إضافة موظف' })).toBeVisible();
    await expect(page.getByLabel('الاسم الكامل بالإنجليزية')).toBeVisible();
    await page.getByLabel('الاسم الكامل بالإنجليزية').fill('Alex Morgan');
    await page.getByLabel('تاريخ المباشرة').fill('2026-11-01');
    await page.getByLabel('المسمى الوظيفي').selectOption({ label: 'Specialist' });
    await expectNoHorizontalScroll(page);
    const bar = page.getByTestId('action-bar');
    const box = (await bar.boundingBox())!;
    expect(box.y + box.height).toBeLessThanOrEqual(780 + 1);
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    await expect(page.getByLabel('رقم الجوال')).toHaveAttribute('dir', 'ltr');
    await expectNoHorizontalScroll(page);
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    for (const l of ['الآيبان', 'رقم الهوية']) await expect(page.getByLabel(l)).toHaveAttribute('dir', 'ltr');
    await expectNoHorizontalScroll(page);
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    await expectNoHorizontalScroll(page);
  });

  test('AC33: an Arabic error reads in Arabic with the summary', async ({ page }) => {
    await bootHire(page, { locale: 'ar' });
    await page.getByRole('button', { name: 'التالي', exact: true }).click();
    await expect(page.getByTestId('error-summary')).toContainText('أدخل الاسم الكامل للموظف بالإنجليزية.');
  });
});

test.describe('screenshots', () => {
  const dir = process.env.HIRE_SCREENS_DIR;
  test.skip(!dir, 'set HIRE_SCREENS_DIR to capture');
  for (const [w, h] of [[1440, 1000], [360, 780]] as const) for (const locale of ['en', 'ar'] as const) {
    test(`capture ${locale} ${w}`, async ({ page }) => {
      await page.setViewportSize({ width: w, height: h });
      await bootHire(page, { locale });
      fs.mkdirSync(dir!, { recursive: true });
      const shot = (n: string) => page.screenshot({ path: `${dir}/hire-${n}-${locale}-${w}.png`, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
      const L = locale === 'en'
        ? { name: 'English full name', start: 'Start date', desig: 'Designation', next: 'Next' }
        : { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', next: 'التالي' };
      await expect(page.getByLabel(L.name)).toBeVisible();
      await shot('1-basics-empty');
      await page.getByRole('button', { name: L.next, exact: true }).click();
      await shot('2-basics-errors');
      await page.getByLabel(L.name).fill('Alex Morgan');
      await page.getByLabel(L.start).fill('2026-10-14');
      await page.getByLabel(L.desig).selectOption({ label: 'Specialist' });
      await page.getByRole('button', { name: L.next, exact: true }).click();
      await shot('3-person');
      await page.getByRole('button', { name: L.next, exact: true }).click();
      await shot('4-job-pay-docs');
      await page.getByRole('button', { name: L.next, exact: true }).click();
      await shot('5-review');
    });
  }
});
