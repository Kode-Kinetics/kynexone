import { expect, test, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import { saudiIqama, saudiNationalId } from './bootstrap/saudiIds';

/**
 * New hires review: a hire from an accepted offer for a Saudi company cannot activate without the government ID, and
 * the review said "add it to the draft" while offering no field. These drive the screen against a mocked API: the
 * field the review offers (Saudi National ID, otherwise Iqama), what is checked before a request is sent, the exact
 * request, the re-check that follows, and the maker-checker wording. The server's rules are the backend tests.
 */

const SHOTS = process.env.DRAFT_IDENTITY_SHOTS ?? '';
const NATIONAL_ID = saudiNationalId(21);
const IQAMA = saudiIqama(22);

const DRAFT = {
  id: 'd-1', status: 'PendingHrApproval', currentStep: 'HrApproval', name: 'Hind Alharbi', arabicName: 'هند الحربي', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ',
  joiningDate: '2026-12-01', source: 'Recruitment', applicationId: 'a-1', jobTitle: 'Officer', createdByUserId: 'rec-1', createdByName: 'Sara Ali', isMine: false,
  canApprove: true, approveBlockedReason: null, profileCompletenessScore: 80, createdAtUtc: '2026-11-10T08:00:00Z', submittedAtUtc: '2026-11-10T08:05:00Z',
  decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeId: null, activatedEmployeeCode: null,
};

interface Opts { nationality?: 'Saudi' | 'Pakistani'; canWrite?: boolean; serverRefuses?: boolean; arabic?: boolean }

async function open(page: Page, opts: Opts = {}) {
  const saudi = (opts.nationality ?? 'Saudi') === 'Saudi';
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  let saved = '';
  await page.addInitScript((arabic: boolean) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
  }, !!opts.arabic);

  const problem = saudi
    ? { key: 'IdNumber', label: 'Government ID number', reason: 'missing', fix: 'Add government id number to the draft.' }
    : { key: 'IqamaNumber', label: 'Iqama number', reason: 'missing', fix: 'Add iqama number to the draft.' };
  const review = () => ({
    summary: { ...DRAFT, name: saudi ? 'Hind Alharbi' : 'Imran Khan' },
    draft: {
      id: 'd-1', status: 'PendingHrApproval', englishName: saudi ? 'Hind Alharbi' : 'Imran Khan', arabicName: '', personalEmail: '', workEmail: '', phone: '',
      nationality: opts.nationality ?? 'Saudi', countryCode: 'SA', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ', workLocation: '',
      joiningDate: '2026-12-01', contractType: 'Unlimited', probationEndDate: null, salary: null,
      idNumber: saudi ? saved : '', iqamaNumber: saudi ? '' : saved,
    },
    documentCount: 0,
    activationCheck: saved
      ? { canActivate: true, resolvedCompanyName: 'Riyadh Co', problems: [], advisories: [] }
      : { canActivate: false, resolvedCompanyName: 'Riyadh Co', problems: [problem], advisories: [] },
  });

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (request.method() === 'PUT' && pathname === '/api/employees/drafts/d-1') {
      const body = request.postDataJSON() as Record<string, string>;
      writes.push({ path: pathname, body });
      if (opts.serverRefuses) return json({ error: 'invalid_identity', message: 'English server text', errors: { idNumber: ['id_checksum_invalid'] } }, 422);
      saved = body.idNumber ?? body.iqamaNumber ?? '';
      return json(review().draft);
    }
    if (request.method() !== 'GET') return json({});
    if (pathname === '/api/auth/me') return json({
      id: 'hr-1', tenantId: 't1', email: 'hr@evostel.com', fullName: 'Hana HR', roles: ['HR Manager'], accountType: 'Group', isGroupScope: true, companies: [],
      permissions: ['employees.read', 'employees.approve', ...(opts.canWrite === false ? [] : ['employees.write'])],
    });
    if (pathname === '/api/employees/drafts') return json({
      items: [DRAFT], total: 1, page: 1, pageSize: 25, counts: { awaitingApproval: 1, draft: 0, activated: 0, rejected: 0, cancelled: 0 },
    });
    if (pathname === '/api/employees/drafts/d-1') return json(review());
    if (pathname.endsWith('/offer-compensation')) return json({}, 404);
    if (pathname === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    if (pathname === '/api/features/disabled-keys' || pathname === '/api/features/modules' || pathname === '/api/notifications' || pathname.includes('help-text')) return json([]);
    return json({ items: [], total: 0, page: 1, pageSize: 100 });
  });

  await page.goto('/people/new-hires');
  await page.getByRole('button', { name: 'Review and decide' }).first().click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('draft-identity-fix')).toBeVisible();
  return { dialog, writes };
}

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: `${SHOTS}/${name}.png` });
}

test('a Saudi hire is asked for the National ID, checks it before sending, then re-checks and clears', async ({ page }) => {
  const { dialog, writes } = await open(page);
  const fix = dialog.getByTestId('draft-identity-fix');
  await expect(fix.getByText('Government ID needed to activate')).toBeVisible();
  await expect(fix.getByLabel('National ID')).toBeVisible();
  await expect(fix.getByLabel('Iqama')).toHaveCount(0);
  await expect(fix.getByText('Saving this makes you an editor of this hire, so a different HR approver has to activate it.')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Approve and activate' })).toBeDisabled();
  await shot(page, 'identity-en-empty');

  await fix.getByLabel('National ID').fill('123');
  await fix.getByRole('button', { name: 'Save and re-check' }).click();
  await expect(fix.getByRole('alert')).toHaveText('National ID must be 10 digits starting with 1.');
  await fix.getByLabel('National ID').fill(IQAMA);
  await fix.getByRole('button', { name: 'Save and re-check' }).click();
  await expect(fix.getByRole('alert')).toHaveText('National ID must be 10 digits starting with 1.');
  expect(writes, 'nothing invalid is sent').toEqual([]);
  await shot(page, 'identity-en-error');

  await fix.getByLabel('National ID').fill(NATIONAL_ID);
  await fix.getByRole('button', { name: 'Save and re-check' }).click();
  await expect(dialog.getByText('Ready to activate. Nothing on this draft would stop activation.')).toBeVisible();
  await expect(dialog.getByTestId('draft-identity-fix')).toHaveCount(0);
  await expect(dialog.getByTestId('draft-identity-saved')).toHaveText('Government ID saved. You edited this hire, so a different HR approver has to activate it.');
  expect(writes).toEqual([{ path: '/api/employees/drafts/d-1', body: { idNumber: NATIONAL_ID } }]);
  await shot(page, 'identity-en-cleared');
});

test('a non-Saudi hire is asked for the Iqama number, and only that is sent', async ({ page }) => {
  const { dialog, writes } = await open(page, { nationality: 'Pakistani' });
  const fix = dialog.getByTestId('draft-identity-fix');
  await expect(fix.getByLabel('Iqama')).toBeVisible();
  await expect(fix.getByLabel('National ID')).toHaveCount(0);
  await fix.getByLabel('Iqama').fill(IQAMA);
  await fix.getByRole('button', { name: 'Save and re-check' }).click();
  await expect(dialog.getByText('Ready to activate. Nothing on this draft would stop activation.')).toBeVisible();
  expect(writes).toEqual([{ path: '/api/employees/drafts/d-1', body: { iqamaNumber: IQAMA } }]);
});

test('the server\'s own refusal is shown in the same words', async ({ page }) => {
  const { dialog, writes } = await open(page, { serverRefuses: true });
  const fix = dialog.getByTestId('draft-identity-fix');
  await fix.getByLabel('National ID').fill(NATIONAL_ID);
  await fix.getByRole('button', { name: 'Save and re-check' }).click();
  await expect(fix.getByRole('alert')).toHaveText('This ID number is not valid. Check the digits.');
  expect(writes).toHaveLength(1);
});

test('someone who cannot edit hires is told so instead of being offered a field', async ({ page }) => {
  const { dialog } = await open(page, { canWrite: false });
  const fix = dialog.getByTestId('draft-identity-fix');
  await expect(fix.getByText('Only someone who can edit new hires can add this number.')).toBeVisible();
  await expect(fix.getByRole('textbox')).toHaveCount(0);
});

test('Arabic: the same field, labelled and explained in Arabic, laid out right to left', async ({ page }) => {
  const { dialog } = await open(page, { arabic: true });
  const fix = dialog.getByTestId('draft-identity-fix');
  await expect(fix.getByText('الهوية الحكومية مطلوبة للتفعيل')).toBeVisible();
  await expect(fix.getByLabel('الهوية الوطنية')).toBeVisible();
  await expect(fix.getByRole('button', { name: 'حفظ وإعادة الفحص' })).toBeVisible();
  await expect(fix.getByText('يجب أن يفعّله معتمد موارد بشرية آخر', { exact: false })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await shot(page, 'identity-ar-empty');
  await fix.getByLabel('الهوية الوطنية').fill('123');
  await fix.getByRole('button', { name: 'حفظ وإعادة الفحص' }).click();
  await expect(fix.getByRole('alert')).toHaveText('يجب أن يتكون رقم الهوية الوطنية من 10 أرقام ويبدأ بالرقم 1.');
  await shot(page, 'identity-ar-error');
});
