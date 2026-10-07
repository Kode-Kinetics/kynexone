import { expect, test, type Locator, type Page, type Route } from '@playwright/test';

/**
 * Employee sign-in access, HR side: the Self-service card on the profile, the list's column, chips
 * and bulk print, Add Employee's suggested work email and "has been added" panel, "Add work emails",
 * and the bilingual sign-in slip print view.
 *
 * Mocked API (contract section 4 + Amendments 1–3); the server's rules are the backend builder's tests.
 * What these prove is the screen: one status and one button per state, the exact requests sent (no
 * active employee in a bulk print, no unaccepted suggestion in a create), the slip's content, and
 * that a welcome code never lands in storage or the URL.
 */

type State = 'waiting_for_work_email' | 'not_started' | 'code_given' | 'active' | 'stopped' | 'blocked';

interface Person {
  id: number; code: string; name: string; arabicName: string; email: string; state: State;
  department: string; branch: string; blockedCode?: string; stoppedReason?: string; issuer?: string;
}

const PEOPLE: Person[] = [
  { id: 42, code: 'EMP-0042', name: 'Noah Williams', arabicName: 'نوح ويليامز', email: 'noah.williams@evostel.com', state: 'not_started', department: 'Sales', branch: 'Riyadh HQ' },
  { id: 43, code: 'EMP-0043', name: 'Layla Haddad', arabicName: 'ليلى حداد', email: 'layla.haddad@evostel.com', state: 'code_given', department: 'Finance', branch: 'Riyadh HQ', issuer: 'Sara Ali' },
  { id: 44, code: 'EMP-0044', name: 'Omar Saleh', arabicName: 'عمر صالح', email: 'omar.saleh@evostel.com', state: 'active', department: 'Operations', branch: 'Jeddah' },
  { id: 45, code: 'EMP-0045', name: 'Sara Ali', arabicName: '', email: '', state: 'waiting_for_work_email', department: 'HR', branch: 'Riyadh HQ' },
  { id: 46, code: 'EMP-0046', name: 'Hamad Qahtani', arabicName: 'حمد القحطاني', email: 'hamad.qahtani@evostel.com', state: 'stopped', department: 'Sales', branch: 'Jeddah', stoppedReason: 'left_company' },
  { id: 47, code: 'EMP-0047', name: 'Fatima Zahrani', arabicName: 'فاطمة الزهراني', email: 'fatima.zahrani@evostel.com', state: 'blocked', department: 'Legal', branch: 'Riyadh HQ', blockedCode: 'company_email_domain_missing' },
  { id: 48, code: 'EMP-0048', name: 'Khalid Noor', arabicName: 'خالد نور', email: 'khalid.noor@evostel.com', state: 'not_started', department: 'IT', branch: 'Riyadh HQ' },
];

const CODES: Record<number, string> = { 42: '48217730', 43: '19004433', 44: '55102938', 45: '70013355', 48: '31415926', 50: '60606161' };
const EXPIRES = '2026-10-14T20:59:59Z';
const COMPANY = {
  id: 'c1', legalNameEn: 'Evostel', legalNameAr: 'إيفوستل', tradeName: 'Evostel', countryCode: 'SA', jurisdiction: 'KSA',
  registrationNumber: '1', taxNumber: '', wpsEmployerId: '', gosiEmployerId: '', qiwaEstablishmentId: '', defaultCurrency: 'SAR',
  emailDomain: 'evostel.com', workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active',
};
/** HR-facing jargon the practitioner panel ruled out (the slip itself may say "username"). */
const JARGON = /\buser\b|\blinks?\b|\blinked\b|invitation|access mode/i;

interface Captured { method: string; path: string; body: unknown; query: string }

async function openPeople(page: Page, opts: { createReturns422?: boolean; emailDelivery?: boolean } = {}) {
  const people = PEOPLE.map((p) => ({ ...p }));
  const writes: Captured[] = [];
  const listQueries: string[] = [];
  const errors: string[] = [];
  let created: { id: number; name: string; email: string } | null = null;
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
  });

  const listItem = (p: Person) => ({
    id: p.id, publicId: `p-${p.id}`, employeeCode: p.code, fullName: p.name, arabicName: p.arabicName, department: p.department,
    designation: 'Officer', branch: p.branch, status: p.state === 'stopped' ? 'Resigned' : 'Active', profileCompletenessScore: 90,
    iqamaNumber: '', readinessState: 'Ready', activationBlockersCount: 0, workEmail: p.email || null, accessState: p.state,
  });
  const detail = (p: Person) => ({
    ...listItem(p), englishName: p.name, preferredName: '', profilePhotoUrl: '', personalEmail: '', workEmail: p.email, phone: '',
    gender: '', maritalStatus: '', nationality: 'Saudi', countryCode: 'SA', companyId: 'c1', workLocation: '', effectiveDate: '2026-01-01',
    reason: '', createdAtUtc: '2026-01-01T00:00:00Z', complianceRecords: [], documents: [], history: [], transfers: [],
  });
  const access = (p: Person) => ({
    employeeId: p.id, employeeName: p.name, employeeCode: p.code, workEmail: p.email || null, state: p.state,
    codeExpiresAtUtc: p.state === 'code_given' ? EXPIRES : null, codeIssuedByName: p.issuer ?? null,
    lastCodeExpiredAtUtc: null, lastSignInAtUtc: p.state === 'active' ? '2026-10-01T06:30:00Z' : null,
    stoppedReason: p.stoppedReason ?? null, blockedCode: p.blockedCode ?? null, blockedReason: p.blockedCode ? 'Server English' : null, canIssue: true,
    emailDelivery: !!opts.emailDelivery,
  });

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    if (request.method() !== 'GET') {
      const body = request.postDataJSON();
      writes.push({ method: request.method(), path: pathname, body, query: url.search });
      if (pathname === '/api/employee-access/codes') {
        const ids = (body as { employeeIds: number[] }).employeeIds;
        // With email delivery the code is emailed (no `code`) unless the screen asked for a printable one.
        const emailIt = !!opts.emailDelivery && (body as { delivery?: string }).delivery !== 'print';
        const issued: unknown[] = [];
        const skipped: unknown[] = [];
        let deliveryMessage: string | null = opts.emailDelivery ? null : 'English server text';
        for (const id of ids) {
          const p = people.find((x) => x.id === id) ?? (created && created.id === id
            ? { id, code: 'EMP-0050', name: created.name, arabicName: '', email: created.email, state: 'not_started' as State, department: 'Sales', branch: 'Riyadh HQ' }
            : null);
          if (!p) { skipped.push({ employeeId: id, reasonCode: 'not_found', reason: 'Not found.' }); continue; }
          // Khalid: without email the plan is full; with email, this HR person typed his work email, so his code is printed.
          if (id === 48 && !opts.emailDelivery) { skipped.push({ employeeId: id, reasonCode: 'seat_limit', reason: 'English server text' }); continue; }
          const printIt = !emailIt || id === 48;
          if (id === 48) deliveryMessage = 'English server text';
          if (!['not_started', 'code_given', 'active'].includes(p.state)) { skipped.push({ employeeId: id, reasonCode: p.state, reason: 'English server text' }); continue; }
          issued.push({
            employeeId: id, employeeName: p.name, arabicName: p.arabicName || null, employeeCode: p.code, username: p.email,
            department: p.department, site: p.branch, ...(printIt ? { code: CODES[id] } : {}), delivery: printIt ? 'print' : 'email', expiresAtUtc: EXPIRES, tenantSlug: 'evostel',
          });
          if (p.state === 'not_started' && people.includes(p as Person)) (p as Person).state = 'code_given';
        }
        const allEmailed = issued.length > 0 && issued.every((i) => (i as { delivery: string }).delivery === 'email');
        return json({ issued, skipped, emailed: allEmailed, deliveryMessage });
      }
      if (pathname === '/api/employee-access/work-emails') {
        const { dryRun } = body as { dryRun: boolean };
        const sara = people.find((p) => p.id === 45)!;
        if (!dryRun) { sara.email = 'sara.ali@evostel.com'; sara.state = 'not_started'; }
        return json({
          matched: [{ employeeId: 45, employeeCode: 'EMP-0045', employeeName: 'Sara Ali', oldEmail: null, newEmail: 'sara.ali@evostel.com' }],
          notFound: ['E1044'],
          wrongDomain: [{ employeeCode: 'EMP-0060', workEmail: 'someone@gmail.com', expectedDomain: 'evostel.com' }],
          conflicts: [{ employeeCode: 'EMP-0044', workEmail: 'o.saleh@evostel.com', reason: 'username_differs', username: 'omar.saleh@evostel.com' }],
          saved: dryRun ? 0 : 1,
        });
      }
      if (pathname === '/api/employees/derive-work-email') {
        const local = (body as { localPart?: string }).localPart;
        const lp = local ?? 'mona.kamal';
        return json({ domain: 'evostel.com', pattern: 'first.last', localPart: lp, workEmail: `${lp}@evostel.com`, unique: true, suggestion: lp, status: 'derived', suggestedWorkEmail: `${lp}@evostel.com` });
      }
      if (pathname === '/api/employees/duplicate-check') return json({ matches: [] });
      if (pathname === '/api/employees') {
        const b = body as { englishName: string; workEmail?: string };
        if (opts.createReturns422) return json({ error: 'work_email_plus_address', message: 'English server text' }, 422);
        created = { id: 50, name: b.englishName, email: b.workEmail ?? '' };
        return json({ ...detail({ id: 50, code: 'EMP-0050', name: b.englishName, arabicName: '', email: b.workEmail ?? '', state: 'not_started', department: 'Sales', branch: 'Riyadh HQ' }) }, 201);
      }
      return json({});
    }

    if (pathname === '/api/auth/me') return json({
      id: 'hr-1', tenantId: 't1', email: 'hr@evostel.com', fullName: 'Hana HR', roles: ['HR Manager'], accountType: 'Group', isGroupScope: true, companies: [],
      permissions: ['employees.read', 'employees.write', 'employees.access.issue', 'employees.access.reset'],
    });
    if (pathname === '/api/employees') {
      listQueries.push(url.search);
      const access = url.searchParams.get('access');
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const status = url.searchParams.get('status');
      if (status) return json({ items: [], total: 0, page: 1, pageSize: 100 });
      const items = people.filter((p) => (!access || access.split(',').includes(p.state)) && p.name.toLowerCase().includes(search)).map(listItem);
      return json({ items, total: items.length, page: Number(url.searchParams.get('page') ?? 1), pageSize: 25 });
    }
    const accessMatch = /^\/api\/employee-access\/(\d+)$/.exec(pathname);
    if (accessMatch) {
      const id = Number(accessMatch[1]);
      if (id === 50 && created) return json(access({ id: 50, code: 'EMP-0050', name: created.name, arabicName: '', email: created.email, state: created.email ? 'not_started' : 'waiting_for_work_email', department: 'Sales', branch: 'Riyadh HQ' }));
      const p = people.find((x) => x.id === id);
      return p ? json(access(p)) : json({}, 404);
    }
    const detailMatch = /^\/api\/employees\/(\d+)$/.exec(pathname);
    if (detailMatch) {
      const id = Number(detailMatch[1]);
      if (id === 50 && created) return json(detail({ id: 50, code: 'EMP-0050', name: created.name, arabicName: '', email: created.email, state: 'not_started', department: 'Sales', branch: 'Riyadh HQ' }));
      const p = people.find((x) => x.id === id);
      return p ? json(detail(p)) : json({}, 404);
    }
    if (/\/readiness$/.test(pathname) || pathname === '/api/employees/field-catalog') return json({ message: 'not here' }, 404);
    if (pathname === '/api/companies') return json({ items: [COMPANY], total: 1, page: 1, pageSize: 100 });
    if (pathname === '/api/tenant-admin/usage') return json({ activeEmployees: 7, maxEmployees: 0, activeUsers: 3, maxUsers: 20, storageUsedMb: 1 });
    if (pathname === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', currencyCode: 'SAR', countryCode: 'SA' });
    if (pathname === '/api/features/disabled-keys' || pathname === '/api/features/modules' || pathname === '/api/notifications' || pathname.includes('help-text')) return json([]);
    return json({ items: [], total: 0, page: 1, pageSize: 100 });
  });

  await page.goto('/people');
  await expect(page.getByRole('heading', { name: 'Employee Management' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open profile for Noah Williams' })).toBeVisible();
  return { writes, listQueries, errors };
}

async function openProfile(page: Page, name: string): Promise<Locator> {
  await page.getByRole('button', { name: `Open profile for ${name}` }).click();
  const card = page.getByTestId('employee-access-card');
  await expect(card).toBeVisible();
  await expect(card.getByText('Checking self-service…')).toHaveCount(0);
  return card;
}

async function expectNoCodeStored(page: Page, codes: string[]) {
  const stored = await page.evaluate(() => {
    const dump = (s: Storage) => Array.from({ length: s.length }, (_, i) => `${s.key(i)}=${s.getItem(s.key(i)!)}`).join('\n');
    return `${dump(localStorage)}\n${dump(sessionStorage)}\n${location.href}`;
  });
  for (const code of codes) {
    expect(stored).not.toContain(code);
    expect(stored).not.toContain(`${code.slice(0, 4)} ${code.slice(4)}`);
  }
}

test('the Self-service card shows one status and one button per state', async ({ page }, testInfo) => {
  const { errors } = await openPeople(page);
  const expectations: Array<[string, string, string | null, RegExp]> = [
    ['Noah Williams', 'No access yet', 'Give access', /No code has been given yet\./],
    ['Layla Haddad', 'Code given, not signed in yet', 'Give new code', /Code given by Sara Ali\. Valid until Wednesday 14 October 2026 \(3 Jumada I 1448 AH\)\./],
    ['Omar Saleh', 'Using KynexOne', 'Reset sign-in', /Last signed in on Thursday 1 October 2026/],
    ['Sara Ali', 'Waiting for work email', 'Add work email', /Add a work email so Sara Ali can sign in\./],
    ['Hamad Qahtani', 'Access stopped', null, /Access stopped because the employee has left the company\./],
    ['Fatima Zahrani', 'Needs admin help', null, /The company email ending .* is not set up\./],
  ];
  for (const [name, label, button, detailText] of expectations) {
    const card = await openProfile(page, name);
    await expect(card.getByText(label, { exact: true })).toBeVisible();
    await expect(card.getByTestId('access-detail')).toHaveText(detailText);
    const buttons = card.getByRole('button');
    if (button) {
      await expect(buttons).toHaveCount(1);
      await expect(buttons.first()).toHaveText(button);
    } else {
      await expect(buttons).toHaveCount(0);
    }
    expect(await card.innerText()).not.toMatch(JARGON);
    if (name === 'Layla Haddad' && testInfo.project.name === 'desktop') await card.screenshot({ path: testInfo.outputPath('access-card.png') });
  }
  expect(errors).toEqual([]);
});

test('Give access opens the bilingual slip with the code and QR, and stores nothing', async ({ page }, testInfo) => {
  const { writes, errors } = await openPeople(page);
  const card = await openProfile(page, 'Noah Williams');
  await card.getByRole('button', { name: 'Give access' }).click();

  const slips = page.getByTestId('sign-in-slips');
  await expect(slips).toBeVisible();
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [42] }]);
  const slip = slips.getByTestId('sign-in-slip');
  await expect(slip).toHaveCount(1);
  await expect(slip.getByTestId('slip-code')).toHaveText('4821 7730');
  await expect(slip.getByTestId('slip-username')).toHaveText('noah.williams@evostel.com');
  await expect(slip.getByTestId('slip-qr')).toBeVisible();
  expect(await slip.getByTestId('slip-qr').getAttribute('src')).toMatch(/^data:image\/png;base64,/);
  await expect(slip.getByText('Your KynexOne sign-in')).toBeVisible();
  await expect(slip.getByText('بيانات الدخول إلى KynexOne')).toBeVisible();
  await expect(slip.getByText('نوح ويليامز')).toBeVisible();
  await expect(slip.getByText('Valid until Wednesday 14 October 2026 (3 Jumada I 1448 AH)')).toBeVisible();
  await expect(slip.getByText('صالح حتى الأربعاء، 14 أكتوبر 2026م (3 جمادى الأولى 1448هـ)')).toBeVisible();
  await expect(slip.getByText("Use this code once. Don't share it. HR will never ask for your password.")).toBeVisible();
  await expect(slip.getByText('If you need help, contact your HR team.')).toBeVisible();
  await expect(slip.locator('[dir="rtl"][lang="ar"] ol li')).toHaveCount(3);
  await expect(slips.getByText('These slips contain codes that will not be shown again after you close this page. If printing fails, give a new code.')).toBeVisible();
  await expect(slips.getByRole('button', { name: 'Print' })).toBeEnabled();
  await expectNoCodeStored(page, ['48217730']);

  if (testInfo.project.name === 'desktop') {
    await page.screenshot({ path: testInfo.outputPath('slip-screen.png') });
    await page.emulateMedia({ media: 'print' });
    await expect(page.getByTestId('employee-access-card')).toBeHidden();
    await expect(slips.getByRole('button', { name: 'Print' })).toBeHidden();
    await expect(slip).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('slip-print.png'), fullPage: true });
    await page.emulateMedia({ media: 'screen' });
  }

  await slips.getByRole('button', { name: 'Close' }).click();
  await expect(slips).toHaveCount(0);
  await expect(page.getByText('4821 7730')).toHaveCount(0);
  await expectNoCodeStored(page, ['48217730']);
  // The card reloads after the slips close.
  await expect(page.getByTestId('employee-access-card').getByText('Code given, not signed in yet', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('Give new code and Reset sign-in confirm first', async ({ page }) => {
  const { writes } = await openPeople(page);
  let card = await openProfile(page, 'Layla Haddad');
  await card.getByRole('button', { name: 'Give new code' }).click();
  await expect(card.getByText('The old code will stop working. Continue?')).toBeVisible();
  expect(writes.filter((w) => w.path === '/api/employee-access/codes')).toHaveLength(0);
  await card.getByRole('button', { name: 'Give new code' }).click();
  await expect(page.getByTestId('sign-in-slip').getByTestId('slip-code')).toHaveText('1900 4433');
  await page.getByTestId('sign-in-slips').getByRole('button', { name: 'Close' }).click();

  card = await openProfile(page, 'Omar Saleh');
  await card.getByRole('button', { name: 'Reset sign-in' }).click();
  await expect(card.getByText("Reset Omar Saleh's sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.")).toBeVisible();
  await card.getByRole('button', { name: 'Reset and print' }).click();
  await expect(page.getByTestId('sign-in-slip').getByTestId('slip-code')).toHaveText('5510 2938');
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [43] }, { employeeIds: [44] }]);
});

test('a skip on a single issue is explained on the card, with no slip', async ({ page }) => {
  await openPeople(page);
  const card = await openProfile(page, 'Khalid Noor');
  await card.getByRole('button', { name: 'Give access' }).click();
  await expect(card.getByRole('alert')).toHaveText("Your company's KynexOne plan is full.");
  await expect(page.getByTestId('sign-in-slips')).toHaveCount(0);
});

test('bulk print skips anyone already using KynexOne and says why', async ({ page }, testInfo) => {
  const { writes, errors } = await openPeople(page);
  const pill = page.getByRole('row').filter({ hasText: 'Layla Haddad' }).getByTestId('access-pill');
  await expect(pill).toHaveText('Code given, not signed in yet');
  for (const name of ['Noah Williams', 'Layla Haddad', 'Omar Saleh', 'Sara Ali']) await page.getByRole('checkbox', { name: `Select ${name}` }).check();
  const bulk = page.getByRole('button', { name: 'Print sign-in slips (4)' });
  await expect(bulk).toBeVisible();
  if (testInfo.project.name === 'desktop') await page.screenshot({ path: testInfo.outputPath('list-selected.png'), fullPage: true });
  expect(await page.getByTestId('access-filter').innerText()).not.toMatch(JARGON);
  expect(await page.locator('table').innerText()).not.toMatch(JARGON);

  await bulk.click();
  const confirm = page.getByRole('dialog').filter({ has: page.getByTestId('bulk-print-confirm') });
  await expect(confirm.getByText('Some selected employees already have a code. Their old codes will stop working. Continue?')).toBeVisible();
  await confirm.getByRole('button', { name: 'Print sign-in slips (2)' }).click();

  const slips = page.getByTestId('sign-in-slips');
  await expect(slips.getByTestId('sign-in-slip')).toHaveCount(2);
  // Never an active employee in a bulk request, and nobody the screen already knows cannot get a code.
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [42, 43] }]);
  await expect(slips.getByTestId('slips-summary')).toHaveText('Slips ready: 2. Skipped: 2.');
  const skipped = slips.getByTestId('skipped-list');
  await expect(skipped.getByRole('listitem').filter({ hasText: 'Omar Saleh' })).toContainText("Use Reset sign-in on the person's profile.");
  await expect(skipped.getByRole('listitem').filter({ hasText: 'Sara Ali' })).toContainText('No work email yet.');
  // Sorted by site, then department, then name: both Riyadh HQ; Finance before Sales.
  await expect(slips.getByTestId('slip-code')).toHaveText(['1900 4433', '4821 7730']);
  if (testInfo.project.name === 'desktop') {
    await page.emulateMedia({ media: 'print' });
    await slips.locator('.kx-sheet').first().screenshot({ path: testInfo.outputPath('slip-sheet-two.png') });
    await page.emulateMedia({ media: 'screen' });
  }
  await expectNoCodeStored(page, ['48217730', '19004433']);
  expect(errors).toEqual([]);
});

test('the Self-service chips filter the list on the server', async ({ page }) => {
  const { listQueries } = await openPeople(page);
  await page.getByTestId('access-filter').getByRole('button', { name: 'No access yet' }).click();
  await expect(page.getByRole('button', { name: 'Open profile for Layla Haddad' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Open profile for Noah Williams' })).toBeVisible();
  expect(listQueries.some((q) => new URLSearchParams(q).get('access') === 'not_started')).toBe(true);
});

test('Add Employee: the suggested work email is not saved unless accepted', async ({ page }) => {
  const { writes } = await openPeople(page);
  await page.getByRole('button', { name: 'Add Employee' }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.locator('label', { hasText: 'English full name' }).locator('input').fill('Mona Kamal');
  const local = dialog.getByTestId('work-email-local-part');
  await expect(local).toHaveAttribute('placeholder', 'mona.kamal');
  await expect(local).toHaveValue('');
  await expect(dialog.getByTestId('work-email-help')).toHaveText('This is also how they sign in to KynexOne.');
  await dialog.getByRole('button', { name: 'Create Employee' }).click();

  const added = dialog.getByTestId('employee-added');
  await expect(added).toContainText('Mona Kamal has been added.');
  await expect(added).toContainText('Add a work email so Mona Kamal can sign in.');
  const create = writes.find((w) => w.path === '/api/employees');
  expect((create!.body as { workEmail?: string }).workEmail).toBeUndefined();
  await expect(dialog.getByRole('button', { name: 'Print sign-in slip' })).toHaveCount(0);
  expect(await dialog.innerText()).not.toMatch(JARGON);
  await dialog.getByRole('button', { name: 'Later' }).click();
  await expect(page.getByTestId('employee-access-card')).toBeVisible();
});

test('Add Employee: accept the suggestion, then Print sign-in slip', async ({ page }, testInfo) => {
  const { writes } = await openPeople(page);
  await page.getByRole('button', { name: 'Add Employee' }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.locator('label', { hasText: 'English full name' }).locator('input').fill('Mona Kamal');
  const local = dialog.getByTestId('work-email-local-part');
  await expect(local).toHaveAttribute('placeholder', 'mona.kamal');
  if (testInfo.project.name === 'phone') await dialog.getByRole('button', { name: 'Use', exact: true }).click();
  else { await local.focus(); await page.keyboard.press('Tab'); }
  await expect(local).toHaveValue('mona.kamal');
  await dialog.getByRole('button', { name: 'Create Employee' }).click();
  await expect(dialog.getByTestId('employee-added')).toContainText('Mona Kamal has been added.');
  expect((writes.find((w) => w.path === '/api/employees')!.body as { workEmail?: string }).workEmail).toBe('mona.kamal@evostel.com');
  await dialog.getByRole('button', { name: 'Print sign-in slip' }).click();
  await expect(page.getByTestId('sign-in-slip').getByTestId('slip-code')).toHaveText('6060 6161');
  await expectNoCodeStored(page, ['60606161']);
});

test("Add Employee: a '+' in the work email is refused in plain words", async ({ page }) => {
  await openPeople(page, { createReturns422: true });
  await page.getByRole('button', { name: 'Add Employee' }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.locator('label', { hasText: 'English full name' }).locator('input').fill('Mona Kamal');
  await dialog.getByTestId('work-email-local-part').fill('mona+hr');
  await dialog.getByRole('button', { name: 'Create Employee' }).click();
  await expect(dialog.getByText("Work email can't contain '+'.")).toBeVisible();
  await expect(dialog.getByText('English server text')).toHaveCount(0);
});

test('Add work emails: paste, preview counts, save, then give access and print', async ({ page }, testInfo) => {
  const { writes, errors } = await openPeople(page);
  await page.getByTestId('access-filter').getByRole('button', { name: 'Waiting for work email' }).click();
  await page.getByRole('button', { name: 'Add work emails' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByTestId('work-emails-paste').fill([
    'Employee no.\tWork email', 'EMP-0045\tsara.ali@evostel.com', 'E1044\tghost@evostel.com',
    'EMP-0060\tsomeone@gmail.com', 'EMP-0044\to.saleh@evostel.com',
  ].join('\n'));
  await expect(dialog.getByTestId('work-emails-row-count')).toHaveText('4 rows found.');
  await dialog.getByRole('button', { name: 'Check the list' }).click();
  await expect(dialog.getByTestId('work-emails-preview')).toHaveText('Ready: 1 · Not found: 1 · Wrong email ending: 1 · Already used: 1');
  await expect(dialog.getByText('No employee has the number E1044.')).toBeVisible();
  await expect(dialog.getByText('someone@gmail.com does not end in @evostel.com.')).toBeVisible();
  await expect(dialog.getByText("This person already signs in with omar.saleh@evostel.com; their sign-in won't change.")).toBeVisible();
  expect(await dialog.innerText()).not.toMatch(JARGON);
  if (testInfo.project.name === 'desktop') await dialog.screenshot({ path: testInfo.outputPath('add-work-emails.png') });
  expect((writes.at(-1)!.body as { dryRun: boolean }).dryRun).toBe(true);

  await dialog.getByRole('button', { name: 'Save (1)' }).click();
  await expect(dialog.getByTestId('work-emails-saved')).toContainText('1 work email saved.');
  await expect(dialog.getByText('Give access to these employees now (1)?')).toBeVisible();
  expect((writes.at(-1)!.body as { dryRun: boolean }).dryRun).toBe(false);
  await dialog.getByRole('button', { name: 'Print sign-in slips (1)' }).click();
  await expect(page.getByTestId('sign-in-slip').getByTestId('slip-code')).toHaveText('7001 3355');
  // No Arabic name on file: the Arabic half shows the English name.
  await expect(page.getByTestId('sign-in-slip').locator('[lang="ar"] .kx-name')).toHaveText('Sara Ali');
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [45] }]);
  await expectNoCodeStored(page, ['70013355']);
  expect(errors).toEqual([]);
});

test('when the company can email codes, Email sign-in code leads and Print sign-in slip is second', async ({ page }) => {
  const { writes } = await openPeople(page, { emailDelivery: true });
  const card = await openProfile(page, 'Noah Williams');
  const buttons = card.getByRole('button');
  await expect(buttons).toHaveText(['Email sign-in code', 'Print sign-in slip']);
  await card.getByRole('button', { name: 'Email sign-in code' }).click();
  const summary = page.getByTestId('welcome-codes-summary');
  await expect(summary).toContainText('Sign-in codes emailed: 1.');
  await expect(page.getByTestId('sign-in-slips')).toHaveCount(0);
  await page.getByRole('dialog').getByRole('button', { name: 'Close' }).last().click();

  await page.getByTestId('employee-access-card').getByRole('button', { name: 'Print sign-in slip' }).click();
  // Noah now has a code (the mock moved him on), so Give new code confirms first.
  await page.getByTestId('employee-access-card').getByRole('button', { name: 'Give new code' }).click();
  await expect(page.getByTestId('sign-in-slip').getByTestId('slip-code')).toHaveText('4821 7730');
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([
    { employeeIds: [42] }, { employeeIds: [42], delivery: 'print' },
  ]);
  await expectNoCodeStored(page, ['48217730']);
});

test('Add Employee with email delivery offers Email sign-in code first', async ({ page }) => {
  const { writes } = await openPeople(page, { emailDelivery: true });
  await page.getByRole('button', { name: 'Add Employee' }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.locator('label', { hasText: 'English full name' }).locator('input').fill('Mona Kamal');
  await dialog.getByTestId('work-email-local-part').fill('mona.kamal');
  await dialog.getByRole('button', { name: 'Create Employee' }).click();
  await expect(dialog.getByTestId('employee-added')).toContainText('Mona Kamal has been added.');
  await expect(dialog.getByRole('button', { name: 'Print sign-in slip' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Email sign-in code' }).click();
  await expect(page.getByTestId('welcome-codes-summary')).toContainText('Sign-in codes emailed: 1.');
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [50] }]);
});

test('a mixed result prints what must be printed and says how many were emailed', async ({ page }) => {
  const { writes, errors } = await openPeople(page, { emailDelivery: true });
  for (const name of ['Noah Williams', 'Khalid Noor']) await page.getByRole('checkbox', { name: `Select ${name}` }).check();
  await page.getByRole('button', { name: 'Print sign-in slips (2)' }).click();
  const slips = page.getByTestId('sign-in-slips');
  await expect(slips.getByTestId('sign-in-slip')).toHaveCount(1);
  await expect(slips.getByTestId('slip-code')).toHaveText('3141 5926');
  await expect(slips.getByTestId('slips-emailed')).toHaveText('Sign-in codes emailed: 1.');
  await expect(slips.getByTestId('slips-delivery-note')).toHaveText('You entered these work emails, so print the slips and hand them over in person.');
  await expect(slips.getByText('English server text')).toHaveCount(0);
  expect(writes.filter((w) => w.path === '/api/employee-access/codes').map((w) => w.body)).toEqual([{ employeeIds: [42, 48] }]);
  await expectNoCodeStored(page, ['31415926']);
  expect(errors).toEqual([]);
});
