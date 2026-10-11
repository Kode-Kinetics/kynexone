import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Setup → GL & rates → ERP API access, against synthetic intercepted API responses (no backend, no real tenant).
 * Run from frontend with a dev server up:
 *   npx next dev -p 5523 &
 *   GL_API_KEYS_BASE_URL=http://127.0.0.1:5523 npx playwright test -c e2e/playwright.gl-api-keys.config.ts
 * Proves the UI contract (list, create + one-time reveal, revoke, refusals, Arabic), not server authorization.
 */

const DAY = 86_400_000;
const inDays = (n: number) => new Date(Date.now() + n * DAY).toISOString();

const companySa = {
  id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active',
};
const companyAe = {
  id: 'co-ae', legalNameEn: 'Masar Services UAE', legalNameAr: '', tradeName: 'Masar UAE',
  countryCode: 'AE', jurisdiction: 'UAE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active',
};

const READ = 'finance.gl.exports.read';
const CONFIRM = 'finance.gl.exports.confirm';
const PLAINTEXT = 'kx_live_NeW12345.Zq9-s3cr3t-only-once-7fG2';

interface Key {
  id: string; name: string; prefix: string; companyId: string | null; companyName: string | null; scopes: string[];
  expiresAtUtc: string; ipAllowList: string[]; createdAtUtc: string; createdByName: string | null;
  lastUsedAtUtc: string | null; lastUsedIp: string | null; revokedAtUtc: string | null; revokedByName: string | null;
  status: 'Active' | 'Expired' | 'Revoked';
}

function seedKeys(): Key[] {
  return [
    {
      id: 'k-sap', name: 'SAP production', prefix: 'kx_live_AbC12xYz', companyId: null, companyName: null, scopes: [READ, CONFIRM],
      expiresAtUtc: inDays(200), ipAllowList: ['203.0.113.0/24'], createdAtUtc: '2026-09-01T08:00:00Z', createdByName: 'Omar Maker',
      lastUsedAtUtc: '2026-10-09T06:15:00Z', lastUsedIp: '203.0.113.14', revokedAtUtc: null, revokedByName: null, status: 'Active',
    },
    {
      id: 'k-oracle', name: 'Oracle test', prefix: 'kx_live_OrA77qq1', companyId: 'co-sa', companyName: 'Masar Trading KSA', scopes: [READ],
      expiresAtUtc: inDays(10), ipAllowList: [], createdAtUtc: '2026-09-20T08:00:00Z', createdByName: 'Omar Maker',
      lastUsedAtUtc: null, lastUsedIp: null, revokedAtUtc: null, revokedByName: null, status: 'Active',
    },
    {
      id: 'k-old', name: 'Old SAP key', prefix: 'kx_live_OlD00aa9', companyId: null, companyName: null, scopes: [READ],
      expiresAtUtc: inDays(40), ipAllowList: [], createdAtUtc: '2026-05-01T08:00:00Z', createdByName: 'Omar Maker',
      lastUsedAtUtc: '2026-08-30T10:00:00Z', lastUsedIp: '198.51.100.3', revokedAtUtc: '2026-09-01T09:00:00Z', revokedByName: 'Omar Maker', status: 'Revoked',
    },
    {
      id: 'k-pilot', name: 'Pilot integration', prefix: 'kx_live_PiL01bb2', companyId: 'co-ae', companyName: 'Masar Services UAE', scopes: [READ],
      expiresAtUtc: '2026-08-01T00:00:00Z', ipAllowList: [], createdAtUtc: '2026-05-01T08:00:00Z', createdByName: null,
      lastUsedAtUtc: null, lastUsedIp: null, revokedAtUtc: null, revokedByName: null, status: 'Expired',
    },
  ];
}

interface Options {
  locale?: 'en' | 'ar';
  /** GET /api-keys answers 403 with this reason. */
  refuse?: 'mfa_required' | 'admin_required';
  empty?: boolean;
}

interface Recorded { method: string; path: string; body: unknown }

async function boot(page: Page, options: Options = {}) {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  const keys: Key[] = options.empty ? [] : seedKeys();
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'gl-api-keys-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    const w = window as unknown as { __copied: string | null };
    w.__copied = null;
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: async (s: string) => { w.__copied = s; } },
    });
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => {
      try { return request.postDataJSON(); } catch { return request.postData(); }
    })();
    if (method !== 'GET' && (p.startsWith('/api/finance/gl/api-keys') || p.startsWith('/api/auth/mfa'))) writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-admin', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Omar Maker', roles: ['Admin'],
        permissions: ['organization.read', 'organization.write', 'dashboard.read', 'finance.gl.read', 'finance.gl.manage'],
        accountType: 'Group', isGroupScope: true,
        companies: [companySa, companyAe].map((c) => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: c.countryCode, isActive: true })),
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/auth/mfa/enrollment/start') return reply({ enrollmentToken: 'enrol-token', expiresInSeconds: 600 });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged([companySa, companyAe]));

    // ── ERP API keys ──
    if (p === '/api/finance/gl/api-keys/openapi.json') return reply({ openapi: '3.0.1', info: { title: 'KynexOne ERP API', version: '1' }, paths: {} });
    if (p === '/api/finance/gl/api-keys') {
      if (options.refuse) {
        const message = options.refuse === 'mfa_required' ? 'Two-step sign-in is required to manage API keys.' : 'Only administrators can manage API keys.';
        return reply({ error: options.refuse, message }, 403);
      }
      if (method === 'GET') return reply({ keys });
      const b = body as { name: string; companyId: string | null; scopes: string[]; expiresAtUtc: string; ipAllowList: string[] };
      if (keys.some((k) => k.name === b.name && k.status === 'Active')) {
        return reply({ error: 'invalid_request', message: 'An active key with this name already exists.', field: 'name' }, 400);
      }
      const key: Key = {
        id: 'k-new', name: b.name, prefix: 'kx_live_NeW12345', companyId: b.companyId,
        companyName: b.companyId === 'co-sa' ? 'Masar Trading KSA' : b.companyId === 'co-ae' ? 'Masar Services UAE' : null,
        scopes: b.scopes, expiresAtUtc: b.expiresAtUtc, ipAllowList: b.ipAllowList, createdAtUtc: new Date().toISOString(),
        createdByName: 'Omar Maker', lastUsedAtUtc: null, lastUsedIp: null, revokedAtUtc: null, revokedByName: null, status: 'Active',
      };
      keys.unshift(key);
      return reply({ key, plaintext: PLAINTEXT }, 201);
    }
    if (p.startsWith('/api/finance/gl/api-keys/') && method === 'DELETE') {
      const id = decodeURIComponent(p.split('/').pop() ?? '');
      const key = keys.find((k) => k.id === id);
      if (!key) return reply({ error: 'not_found', message: 'API key not found.' }, 404);
      Object.assign(key, { status: 'Revoked', revokedAtUtc: new Date().toISOString(), revokedByName: 'Omar Maker' });
      return reply({ key });
    }

    // ── The rest of GL & rates (the default sub-tab loads before ours is chosen) ──
    if (p === '/api/finance/gl/resolve') return reply({ companyId: null, rows: [] });
    if (p === '/api/finance/gl/control-accounts/health') return reply({ generatedAtUtc: '2026-10-10T08:00:00Z', companyId: null, healthy: true, violations: [], accounts: [] });
    if (p.startsWith('/api/finance/gl/')) return reply([]);
    if (p === '/api/organization/cost-centers' || p === '/api/grades' || p === '/api/branches' || p === '/api/departments') return reply(paged([]));
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/setup?tab=glMapping');
  const ar = options.locale === 'ar';
  await expect(page.getByRole('heading', { level: 1, name: ar ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  await page.getByRole('tab', { name: ar ? 'وصول نظام تخطيط الموارد عبر الواجهة البرمجية' : 'ERP API access', exact: true }).click();
  return { writes, errors, keys };
}

/** GL_API_KEYS_SHOTS_DIR, when set, receives review screenshots. */
async function shot(page: Page, name: string) {
  const dir = process.env.GL_API_KEYS_SHOTS_DIR;
  if (!dir) return;
  await page.screenshot({ path: `${dir}/${name}.png`, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

const panel = (page: Page) => page.getByRole('region', { name: 'ERP API access', exact: true });
const row = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name });

test('lists active, revoked and expired keys in plain words, active first, with the expiry warning', async ({ page }) => {
  const state = await boot(page);
  const p = panel(page);
  await expect(p.getByText('An API key lets your ERP read the journal export files created here', { exact: false })).toBeVisible();
  await expect(p.getByText('To rotate, create a new key, update your ERP, then revoke the old one.')).toBeVisible();
  for (const header of ['Name', 'Key starts with', 'Company', 'What it can do', 'Expires', 'Last used', 'Status', 'Actions']) {
    await expect(p.getByRole('columnheader', { name: header, exact: true })).toBeVisible();
  }

  const sap = row(page, 'SAP production');
  await expect(sap).toContainText('kx_live_AbC12xYz');
  await expect(sap).toContainText('All companies');
  await expect(sap).toContainText('Read journal exports');
  await expect(sap).toContainText('Confirm or reject journal exports');
  await expect(sap).toContainText('203.0.113.14');
  await expect(sap).toContainText('Active');
  await expect(sap.getByRole('button', { name: 'Revoke SAP production', exact: true })).toBeVisible();

  const oracle = row(page, 'Oracle test');
  await expect(oracle).toContainText('Masar Trading KSA');
  await expect(oracle).toContainText('Never used');
  await expect(oracle).toContainText('Expires in 10 days');
  await expect(p.getByText('1 key expires within 14 days.', { exact: false })).toBeVisible();

  const revoked = row(page, 'Old SAP key');
  await expect(revoked).toContainText('Revoked');
  await expect(revoked.getByRole('button')).toHaveCount(0);
  await expect(revoked).toHaveClass(/text-slate-400/);
  const expired = row(page, 'Pilot integration');
  await expect(expired).toContainText('Expired');
  await expect(expired.getByRole('button')).toHaveCount(0);

  // Active keys come first, the one expiring soonest on top.
  const order = await p.locator('tbody tr').evaluateAll((rows) => rows.map((r) => r.getAttribute('data-key-id')));
  expect(order.slice(0, 2)).toEqual(['k-oracle', 'k-sap']);
  await shot(page, 'erp-api-en-list');

  // The API description is a labelled download, fetched with the session's auth.
  const download = page.waitForEvent('download');
  await p.getByRole('button', { name: 'Download API description', exact: true }).click();
  expect((await download).suggestedFilename()).toBe('kynexone-erp-api.json');
  expect(state.errors).toEqual([]);
});

test('create sends the contract, reveals the key once with a copy button, and forgets it on close', async ({ page }) => {
  const state = await boot(page);
  const p = panel(page);
  await p.getByRole('button', { name: 'Create API key', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Create ERP API key' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('Read journal exports')).toBeChecked();
  await expect(dialog.getByLabel('Confirm or reject journal exports')).not.toBeChecked();
  await expect(dialog.getByLabel('Expires after (days)')).toHaveValue('90');
  await expect(dialog.getByText('Leave empty to allow any address. Example: 203.0.113.0/24', { exact: false })).toBeVisible();
  await expect(dialog.getByText(/The key stops working on/)).toBeVisible();

  // Client-side checks mirror the contract and send nothing.
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Enter a name for the key.');
  await dialog.getByLabel('Key name').fill('Oracle Fusion');
  await dialog.getByLabel('Expires after (days)').fill('400');
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Choose an expiry between 1 and 365 days.');
  await dialog.getByLabel('Expires after (days)').fill('90');
  await dialog.getByLabel('Allowed addresses (optional)').fill('not-an-ip');
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('not-an-ip is not an IP address or range.');
  expect(state.writes).toEqual([]);

  // The server's 400 is shown verbatim.
  await dialog.getByLabel('Key name').fill('SAP production');
  await dialog.getByLabel('Allowed addresses (optional)').fill('203.0.113.0/24\n\n  198.51.100.7  \n');
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  await expect(dialog.getByRole('alert')).toHaveText('An active key with this name already exists.');

  await dialog.getByLabel('Key name').fill('Oracle Fusion');
  await dialog.getByLabel('Company').selectOption('co-sa');
  await dialog.getByLabel('Confirm or reject journal exports').check();
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();

  const reveal = page.getByRole('dialog', { name: 'Your new ERP API key' });
  await expect(reveal).toBeVisible();
  const posts = state.writes.filter((w) => w.method === 'POST' && w.path === '/api/finance/gl/api-keys');
  expect(posts).toHaveLength(2);
  const sent = posts[1].body as { name: string; companyId: string | null; scopes: string[]; expiresAtUtc: string; ipAllowList: string[] };
  expect(sent.name).toBe('Oracle Fusion');
  expect(sent.companyId).toBe('co-sa');
  expect(sent.scopes).toEqual([READ, CONFIRM]);
  expect(sent.ipAllowList).toEqual(['203.0.113.0/24', '198.51.100.7']);
  const days = (new Date(sent.expiresAtUtc).getTime() - Date.now()) / DAY;
  expect(days).toBeGreaterThan(89.9);
  expect(days).toBeLessThan(90.1);

  await expect(reveal.getByText('This is the only time the key is shown. Store it in your ERP now. If you lose it, revoke it and create a new one.')).toBeVisible();
  await shot(page, 'erp-api-en-reveal');
  const field = reveal.getByLabel('API key for Oracle Fusion');
  await expect(field).toHaveValue(PLAINTEXT);
  await expect(field).toHaveAttribute('readonly', '');
  await reveal.getByRole('button', { name: 'Copy key', exact: true }).click();
  await expect(reveal.getByText('Copied', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => (window as unknown as { __copied: string | null }).__copied)).toBe(PLAINTEXT);

  await reveal.getByRole('button', { name: 'I have stored the key', exact: true }).click();
  await expect(reveal).toHaveCount(0);
  await expect(row(page, 'Oracle Fusion')).toContainText('kx_live_NeW12345');
  await expect(row(page, 'Oracle Fusion')).toContainText('Masar Trading KSA');
  expect(await page.content()).not.toContain(PLAINTEXT);
  expect(state.errors).toEqual([]);
});

test('revoke asks first, names the key, then calls DELETE and shows the key as revoked', async ({ page }) => {
  const state = await boot(page);
  const sap = row(page, 'SAP production');
  await sap.getByRole('button', { name: 'Revoke SAP production', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Revoke SAP production?' });
  await expect(dialog).toContainText('Your ERP will be refused immediately the next time it uses this key.');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(state.writes).toEqual([]);

  await sap.getByRole('button', { name: 'Revoke SAP production', exact: true }).click();
  await page.getByRole('dialog', { name: 'Revoke SAP production?' }).getByRole('button', { name: 'Revoke key', exact: true }).click();
  await expect.poll(() => state.writes.map((w) => `${w.method} ${w.path}`)).toEqual(['DELETE /api/finance/gl/api-keys/k-sap']);
  await expect(sap).toContainText('Revoked');
  await expect(sap.getByRole('button')).toHaveCount(0);
  await expect(page.getByText('SAP production is revoked. Your ERP can no longer use it.')).toBeVisible();
  expect(state.errors).toEqual([]);
});

test('without two-step sign-in the panel says so and links to setting it up, not to an empty list', async ({ page }) => {
  const state = await boot(page, { refuse: 'mfa_required' });
  const p = panel(page);
  await expect(p.getByText('Turn on two-step sign-in to manage ERP API keys')).toBeVisible();
  await expect(p.getByText('No ERP API keys yet')).toHaveCount(0);
  await expect(p.getByRole('button', { name: 'Create API key' })).toHaveCount(0);
  await shot(page, 'erp-api-mfa-required');
  expect(state.errors).toEqual([]);
  await p.getByRole('button', { name: 'Set up two-step sign-in', exact: true }).click();
  await expect.poll(() => state.writes.map((w) => w.path)).toContain('/api/auth/mfa/enrollment/start');
  // The sign-in page runs the enrolment step (a dev server may compile /login on first visit).
  await expect(page).toHaveURL(/\/login/, { timeout: 60_000 });
});

test('a non-admin refusal is a plain message without actions', async ({ page }) => {
  const state = await boot(page, { refuse: 'admin_required' });
  await expect(panel(page).getByText('Only an administrator can manage ERP API keys.', { exact: false })).toBeVisible();
  await expect(panel(page).getByRole('button', { name: 'Create API key' })).toHaveCount(0);
  await expect(panel(page).getByRole('button', { name: 'Set up two-step sign-in' })).toHaveCount(0);
  expect(state.errors).toEqual([]);
});

test('an empty list says no keys exist yet and offers Create', async ({ page }) => {
  const state = await boot(page, { empty: true });
  const p = panel(page);
  await expect(p.getByText('No ERP API keys yet')).toBeVisible();
  await expect(p.getByRole('columnheader')).toHaveCount(0);
  await p.getByRole('button', { name: 'Create API key', exact: true }).last().click();
  await expect(page.getByRole('dialog', { name: 'Create ERP API key' })).toBeVisible();
  expect(state.errors).toEqual([]);
});

test('create, reveal and revoke read right to left in Arabic', async ({ page }) => {
  const state = await boot(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const p = page.getByRole('region', { name: 'وصول نظام تخطيط الموارد عبر الواجهة البرمجية', exact: true });
  await expect(p.getByRole('columnheader', { name: 'بداية المفتاح', exact: true })).toBeVisible();
  await expect(row(page, 'SAP production')).toContainText('جميع الشركات');
  await expect(row(page, 'SAP production')).toContainText('تأكيد ملفات تصدير القيود أو رفضها');
  await expect(row(page, 'Old SAP key')).toContainText('ملغى');
  await expect(row(page, 'Oracle test')).toContainText('لم يُستخدم قط');

  await p.getByRole('button', { name: 'إنشاء مفتاح واجهة برمجية', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'إنشاء مفتاح واجهة برمجية لنظام تخطيط الموارد' });
  await dialog.getByLabel('اسم المفتاح').fill('Oracle Fusion');
  await dialog.getByRole('button', { name: 'إنشاء المفتاح', exact: true }).click();
  const reveal = page.getByRole('dialog', { name: 'مفتاح الواجهة البرمجية الجديد' });
  await expect(reveal.getByText('هذه هي المرة الوحيدة التي يظهر فيها المفتاح.', { exact: false })).toBeVisible();
  await expect(reveal.getByLabel('مفتاح الواجهة البرمجية لـ Oracle Fusion')).toHaveValue(PLAINTEXT);
  await reveal.getByRole('button', { name: 'نسخ المفتاح', exact: true }).click();
  await expect(reveal.getByText('تم النسخ', { exact: true })).toBeVisible();
  await reveal.getByRole('button', { name: 'لقد حفظت المفتاح', exact: true }).click();
  await expect(reveal).toHaveCount(0);
  expect(await page.content()).not.toContain(PLAINTEXT);
  const sent = state.writes.find((w) => w.method === 'POST')!.body as { companyId: string | null; scopes: string[]; ipAllowList: string[] };
  expect(sent).toMatchObject({ companyId: null, scopes: [READ], ipAllowList: [] });

  await row(page, 'Oracle Fusion').getByRole('button', { name: 'إلغاء المفتاح Oracle Fusion', exact: true }).click();
  await page.getByRole('dialog', { name: 'إلغاء المفتاح Oracle Fusion؟' }).getByRole('button', { name: 'إلغاء المفتاح نهائيًا', exact: true }).click();
  await expect.poll(() => state.writes.filter((w) => w.method === 'DELETE').map((w) => w.path)).toEqual(['/api/finance/gl/api-keys/k-new']);
  await expect(row(page, 'Oracle Fusion')).toContainText('ملغى');
  await shot(page, 'erp-api-ar-list');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  expect(state.errors).toEqual([]);
});

test('the panel fits a phone-width screen and has no serious accessibility violations', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const state = await boot(page);
  await expect(row(page, 'SAP production')).toBeVisible();
  await shot(page, 'erp-api-phone');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  const result = await new AxeBuilder({ page }).include('#setup-setting-content').analyze();
  const serious = result.violations
    .filter((v) => v.impact === 'critical' || v.impact === 'serious')
    .map((v) => `${v.id}: ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`);
  expect(serious).toEqual([]);
  expect(state.errors).toEqual([]);
});
