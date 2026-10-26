import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * WP-F phase 2, screen 1: the cost-centre master under Setup → Cost centres (parent tree, functional class, ERP code,
 * company, the M-8 "in use" dialog, the M-9 code lock), and the department import's links-to-clear gate. Every API
 * request is intercepted, so this proves the UI contract (states, wiring, wording, EN and AR), not server authorisation.
 *   npx next dev -p 5197 &
 *   npx playwright test -c e2e/playwright.cost-centres-master.config.ts
 * CC_SHOTS_DIR, when set, also receives the review screenshots.
 */

const companySa = { id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA', countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const companyAe = { id: 'co-ae', legalNameEn: 'Masar Services UAE', legalNameAr: '', tradeName: 'Masar UAE', countryCode: 'AE', jurisdiction: 'UAE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active' };
const ZERO = '00000000-0000-0000-0000-000000000000';

interface Cc { id: string; companyId: string | null; code: string; name: string; nameAr?: string | null; isActive: boolean; parentId?: string | null; functionalClass?: string | null; glSegment?: string | null }

const seed = (): Cc[] => [
  { id: 'cc-sales', companyId: 'co-sa', code: 'SALES', name: 'Sales', nameAr: 'المبيعات', isActive: true, functionalClass: 'SELLING', glSegment: 'S100' },
  { id: 'cc-sales-ry', companyId: 'co-sa', code: 'SALES-RY', name: 'Riyadh Sales', nameAr: 'مبيعات الرياض', isActive: true, parentId: 'cc-sales', functionalClass: 'SELLING', glSegment: 'S110' },
  { id: 'cc-sales-ry-n', companyId: 'co-sa', code: 'SALES-RY-N', name: 'Riyadh North', isActive: true, parentId: 'cc-sales-ry' },
  { id: 'cc-ops', companyId: 'co-sa', code: 'OPS', name: 'Operations', isActive: false, functionalClass: 'OPERATIONS' },
  { id: 'cc-orphan', companyId: null, code: 'ORPHAN', name: 'Old group cost centre', isActive: true },
  { id: 'cc-ae', companyId: 'co-ae', code: 'AE-OPS', name: 'UAE operations', isActive: true },
];

interface Options {
  locale?: 'en' | 'ar';
  /** organization.costcenter.manage (Admin, HR Manager). */
  manage?: boolean;
  /** finance.gl.manage: may set the functional class. */
  finance?: boolean;
  empty?: boolean;
  listDown?: boolean;
}

interface Recorded { method: string; path: string; body: any; headers: Record<string, string> }

async function boot(page: Page, options: Options = {}, url = '/setup?tab=costCenters') {
  const manage = options.manage ?? true;
  const writes: Recorded[] = [];
  const errors: string[] = [];
  const rows = options.empty ? [] : seed();
  const ctl = {
    /** The next write answers this once. */
    refuse: null as null | { status: number; body: unknown },
    listDown: !!options.listDown,
    previewLinks: 0,
    previewRows: [] as any[],
    importRefusal: null as null | { status: number; body: unknown },
  };
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'cc-master-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });
  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const u = new URL(request.url());
    const p = u.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200, headers?: Record<string, string>) => route.fulfill({ status, json, headers });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return request.postData(); } })();
    if (method !== 'GET') writes.push({ method, path: p, body, headers: request.headers() });

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-hr', tenantId: 'cc-tenant', tenantSlug: 'cc-fixture', fullName: 'Huda HR', roles: [manage ? 'HR Manager' : 'HR Officer'],
        permissions: ['organization.read', 'organization.write', 'dashboard.read', ...(manage ? ['organization.costcenter.manage'] : []), ...(options.finance ? ['finance.gl.read', 'finance.gl.manage'] : [])],
        accountType: 'Group', isGroupScope: true,
        companies: [companySa, companyAe].map((c) => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: c.countryCode, isActive: true })),
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged([companySa, companyAe]));

    if (p === '/api/organization/cost-centers' && method === 'GET') {
      if (ctl.listDown) return reply({ code: 'internal_error', message: 'boom', traceId: 't-1' }, 500);
      return reply(paged(rows));
    }
    if (p === '/api/organization/cost-centers' && method === 'POST') {
      if (ctl.refuse) { const r = ctl.refuse; ctl.refuse = null; return reply(r.body, r.status); }
      const b = body as Cc;
      const created = { ...b, id: `cc-new-${rows.length}`, companyId: b.companyId ?? null, parentId: b.parentId && b.parentId !== ZERO ? b.parentId : null, functionalClass: b.functionalClass || null, glSegment: b.glSegment || null };
      rows.push(created as Cc);
      return reply(created, 201);
    }
    const one = p.match(/^\/api\/cost-centers\/([^/]+)$/);
    if (one && method === 'PUT') {
      if (ctl.refuse) { const r = ctl.refuse; ctl.refuse = null; return reply(r.body, r.status); }
      const row = rows.find((r) => r.id === one[1]);
      const b = body as Cc;
      if (row) {
        Object.assign(row, { code: b.code, name: b.name, nameAr: b.nameAr, isActive: b.isActive, companyId: b.companyId ?? row.companyId });
        if (b.parentId !== undefined && b.parentId !== null) row.parentId = b.parentId === ZERO ? null : b.parentId;
        if (b.functionalClass !== undefined && b.functionalClass !== null) row.functionalClass = b.functionalClass || null;
        if (b.glSegment !== undefined && b.glSegment !== null) row.glSegment = b.glSegment || null;
      }
      return reply(row ?? {});
    }
    if (one && method === 'DELETE') {
      if (ctl.refuse) { const r = ctl.refuse; ctl.refuse = null; return reply(r.body, r.status); }
      const i = rows.findIndex((r) => r.id === one[1]);
      if (i >= 0) rows.splice(i, 1);
      return route.fulfill({ status: 204 });
    }
    if (p === '/api/cost-centers/export') return route.fulfill({ status: 200, contentType: 'text/csv', body: 'code,name\n' });
    if (p === '/api/cost-centers/import-template') return route.fulfill({ status: 200, contentType: 'text/csv', body: 'code,name\n' });

    // ── departments (the import gate) ──
    if (p === '/api/departments' && method === 'GET') return reply(paged([{ id: 'd1', code: 'FIN', nameEn: 'Finance', nameAr: '', isActive: true, companyId: 'co-sa', costCenterId: 'cc-sales' }]));
    if (p === '/api/departments/import-template') return route.fulfill({ status: 200, contentType: 'text/csv', body: 'Code,NameEn\n' });
    if (p === '/api/departments/import-preview') {
      return route.fulfill({
        status: 200, contentType: 'application/json', headers: { 'X-Links-To-Clear': String(ctl.previewLinks) },
        body: JSON.stringify({ received: ctl.previewRows.length, wouldCreate: 0, wouldUpdate: ctl.previewRows.length, wouldSkip: 0, rows: ctl.previewRows }),
      });
    }
    if (p === '/api/departments/import') {
      if (ctl.importRefusal) { const r = ctl.importRefusal; ctl.importRefusal = null; return reply(r.body, r.status); }
      return reply({ received: ctl.previewRows.length, created: 0, updated: ctl.previewRows.length, skipped: 0, rows: [], errors: [] });
    }
    if (p === '/api/grades' || p === '/api/branches' || p === '/api/designations') return reply(paged([]));
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    if (p.startsWith('/api/finance/')) return reply([]);
    return reply(paged([]));
  });
  await page.goto(url);
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  return { writes, errors, ctl, rows };
}

async function shot(page: Page, info: TestInfo, name: string) {
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.CC_SHOTS_DIR;
  if (dir) { fs.mkdirSync(dir, { recursive: true }); fs.copyFileSync(file, path.join(dir, `${name}.png`)); }
}

const tid = (page: Page, id: string) => page.getByTestId(id);
const lastWrite = (w: Recorded[], method: string) => w.filter((x) => x.method === method).at(-1)!;
const axeSeriousOrWorse = async (page: Page, scope: string) => {
  const result = await new AxeBuilder({ page }).include(scope).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => `${n.html.slice(0, 100)} ${n.any.map((a) => a.message).join(';')}`).join(' | ')}`);
};
const openMenu = async (page: Page, code: string) => {
  await tid(page, `cc-master-row-${code}`).getByRole('button', { name: /More actions|المزيد من الإجراءات/ }).click();
};

// ═════════════════════════════ Screen 1: the list ═════════════════════════════

test('the tree shows parents with their children indented, expands and collapses with aria-expanded, and marks each row\'s level', async ({ page }, info) => {
  const { errors } = await boot(page);
  const table = tid(page, 'cc-master-table');
  await expect(table.getByRole('columnheader', { name: 'Code', exact: true })).toBeVisible();
  for (const h of ['Name', 'ERP code', 'Functional class', 'Company', 'Status']) await expect(table.getByRole('columnheader', { name: h, exact: true })).toBeVisible();
  await expect(tid(page, 'cc-master-view-tree')).toHaveAttribute('aria-pressed', 'true');
  await expect(tid(page, 'cc-master-row-SALES')).toHaveAttribute('aria-level', '1');
  await expect(tid(page, 'cc-master-row-SALES-RY')).toHaveCount(0); // collapsed
  const expand = tid(page, 'cc-master-expand-SALES');
  await expect(expand).toHaveAttribute('aria-expanded', 'false');
  await expand.click();
  await expect(expand).toHaveAttribute('aria-expanded', 'true');
  await expect(tid(page, 'cc-master-row-SALES-RY')).toHaveAttribute('aria-level', '2');
  await expect(tid(page, 'cc-master-row-SALES-RY-N')).toHaveCount(0);
  await tid(page, 'cc-master-expand-SALES-RY').click();
  await expect(tid(page, 'cc-master-row-SALES-RY-N')).toHaveAttribute('aria-level', '3');
  await expect(tid(page, 'cc-master-row-SALES')).toContainText('S100');
  await expect(tid(page, 'cc-master-row-SALES')).toContainText('Selling');
  await shot(page, info, 'cc-en-master-tree');
  expect(errors).toEqual([]);
});

test('search or any filter switches to a flat list and says so; a filter with no match says that, not "no cost centres yet"', async ({ page }) => {
  await boot(page);
  await tid(page, 'cc-master-search').fill('riyadh');
  await expect(page.getByText('Showing flat results')).toBeVisible();
  await expect(tid(page, 'cc-master-row-SALES-RY')).toBeVisible();
  await expect(tid(page, 'cc-master-row-SALES-RY-N')).toBeVisible();
  await expect(tid(page, 'cc-master-row-SALES')).toHaveCount(0);
  await tid(page, 'cc-master-search').fill('zzz');
  await expect(page.getByText('No cost centres match your filters.')).toBeVisible();
  await tid(page, 'cc-master-search').fill('');
  await expect(page.getByText('Showing flat results')).toHaveCount(0);
  await tid(page, 'cc-master-filter-status').selectOption('inactive');
  await expect(tid(page, 'cc-master-row-OPS')).toBeVisible();
  await expect(tid(page, 'cc-master-row-SALES')).toHaveCount(0);
  await tid(page, 'cc-master-filter-status').selectOption('');
  await tid(page, 'cc-master-filter-class').selectOption('SELLING');
  await expect(tid(page, 'cc-master-row-SALES')).toBeVisible();
  await expect(tid(page, 'cc-master-row-OPS')).toHaveCount(0);
  await tid(page, 'cc-master-filter-class').selectOption('');
  await tid(page, 'cc-master-filter-company').selectOption('co-ae');
  await expect(tid(page, 'cc-master-row-AE-OPS')).toBeVisible();
  await expect(tid(page, 'cc-master-row-SALES')).toHaveCount(0);
});

test('a cost centre with no company carries a rose chip that says it cannot be used for posting; inactive reads as text', async ({ page }) => {
  await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await expect(tid(page, 'cc-master-nocompany-chip-ORPHAN')).toContainText('No company: cannot be used for posting. Assign a company.');
  await expect(tid(page, 'cc-master-row-OPS')).toContainText('Inactive');
});

test('the load failing shows the reason and Retry, never the empty text', async ({ page }) => {
  const { ctl } = await boot(page, { listDown: true });
  await expect(page.getByRole('alert').filter({ hasText: 'This could not be loaded.' })).toBeVisible();
  await expect(page.getByText('No cost centres yet. Add one, or import a CSV.')).toHaveCount(0);
  ctl.listDown = false;
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(tid(page, 'cc-master-row-SALES')).toBeVisible();
});

test('no cost centres yet: a message and a primary Add button', async ({ page }) => {
  await boot(page, { empty: true });
  await expect(page.getByText('No cost centres yet. Add one, or import a CSV.')).toBeVisible();
  await expect(tid(page, 'cc-master-new')).toBeVisible();
});

test('without organization.costcenter.manage: New, Import and the row actions are gone and a visible sentence says why', async ({ page }) => {
  await boot(page, { manage: false });
  await expect(tid(page, 'cc-master-readonly-note')).toHaveText('You can view cost centres but not change them. Changing them needs the Manage cost centres permission.');
  await expect(tid(page, 'cc-master-new')).toHaveCount(0);
  await expect(tid(page, 'cc-master-import')).toHaveCount(0);
  await expect(tid(page, 'cc-master-row-SALES').getByRole('button')).toHaveCount(1); // only the expand button
});

// ═════════════════════════════ Screen 1: create and edit ═════════════════════════════

test('New cost centre: the code format is checked before anything is sent, the parent list is this company\'s only, Capitalisable is shown disabled', async ({ page }, info) => {
  const { writes, errors } = await boot(page, { finance: true });
  await tid(page, 'cc-master-new').click();
  const dialog = page.getByRole('dialog', { name: 'New cost centre' });
  await tid(page, 'cc-form-company').selectOption('co-sa');
  await tid(page, 'cc-form-code').fill('-bad code');
  await tid(page, 'cc-form-name').fill('Marketing');
  await tid(page, 'cc-form-save').click();
  await expect(dialog).toContainText('Start with a letter or number and use only letters, numbers, . - _ (max 30).');
  expect(writes).toHaveLength(0);
  const parents = await tid(page, 'cc-form-parent').locator('option').allTextContents();
  expect(parents.join('|')).toContain('SALES');
  expect(parents.join('|')).not.toContain('AE-OPS');
  expect(parents.join('|')).not.toContain('OPS —');
  await expect(tid(page, 'cc-form-class').locator('option[value="CAPITALISABLE"]')).toBeDisabled();
  await shot(page, info, 'cc-en-master-form');

  await tid(page, 'cc-form-code').fill('MKT');
  await tid(page, 'cc-form-name-ar').fill('التسويق');
  await tid(page, 'cc-form-parent').selectOption('cc-sales');
  await tid(page, 'cc-form-class').selectOption('SELLING');
  await tid(page, 'cc-form-erp-code').fill('S200');
  await expect(tid(page, 'cc-form-reason')).toBeVisible(); // a class on a new cost centre is a class change
  await tid(page, 'cc-form-save').click();
  await expect(dialog).toContainText('Enter a reason for this change.');
  await tid(page, 'cc-form-reason').fill('New selling team');
  await tid(page, 'cc-form-save').click();
  await expect(dialog).toHaveCount(0);
  expect(lastWrite(writes, 'POST').body).toMatchObject({ companyId: 'co-sa', code: 'MKT', name: 'Marketing', nameAr: 'التسويق', isActive: true, parentId: 'cc-sales', functionalClass: 'SELLING', glSegment: 'S200', reason: 'New selling team' });
  await expect(page.getByRole('status').filter({ hasText: 'Cost centre saved.' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('Edit: an untouched parent, class and ERP code are not sent; clearing the parent sends the empty Guid; the parent list leaves out the item and its children', async ({ page }) => {
  const { writes } = await boot(page, { finance: true });
  await tid(page, 'cc-master-view-flat').click();
  await tid(page, 'cc-master-row-SALES-RY').getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit cost centre SALES-RY' });
  const parents = await tid(page, 'cc-form-parent').locator('option').allTextContents();
  expect(parents.join('|')).not.toContain('SALES-RY —');
  expect(parents.join('|')).not.toContain('SALES-RY-N');
  await tid(page, 'cc-form-name').fill('Riyadh Sales Team');
  await tid(page, 'cc-form-save').click();
  await expect(dialog).toHaveCount(0);
  const first = lastWrite(writes, 'PUT').body;
  expect(first).toMatchObject({ code: 'SALES-RY', name: 'Riyadh Sales Team', isActive: true });
  expect(first).not.toHaveProperty('parentId');
  expect(first).not.toHaveProperty('functionalClass');
  expect(first).not.toHaveProperty('glSegment');
  expect(first).not.toHaveProperty('reason');

  await tid(page, 'cc-master-row-SALES-RY').getByRole('button', { name: 'Edit', exact: true }).click();
  await tid(page, 'cc-form-parent').selectOption('');
  await tid(page, 'cc-form-erp-code').fill('');
  await tid(page, 'cc-form-save').click();
  await expect(dialog).toHaveCount(0);
  expect(lastWrite(writes, 'PUT').body).toMatchObject({ parentId: ZERO, glSegment: '' });
});

test('changing the functional class asks for a reason; without finance.gl.manage the class is read-only with the reason in text', async ({ page }) => {
  const { writes } = await boot(page, { finance: true });
  await tid(page, 'cc-master-view-flat').click();
  await tid(page, 'cc-master-row-SALES').getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(tid(page, 'cc-form-reason')).toHaveCount(0);
  await tid(page, 'cc-form-class').selectOption('COGS');
  await expect(tid(page, 'cc-form-reason')).toBeVisible();
  await tid(page, 'cc-form-save').click();
  await expect(page.getByRole('dialog')).toContainText('Enter a reason for this change.');
  expect(writes).toHaveLength(0);
  await tid(page, 'cc-form-reason').fill('Policy change');
  await tid(page, 'cc-form-save').click();
  expect(lastWrite(writes, 'PUT').body).toMatchObject({ functionalClass: 'COGS', reason: 'Policy change' });
});

test('without finance.gl.manage the functional class cannot be changed, and the form says why', async ({ page }) => {
  await boot(page, { finance: false });
  await tid(page, 'cc-master-view-flat').click();
  await tid(page, 'cc-master-row-SALES').getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(tid(page, 'cc-form-class')).toBeDisabled();
  await expect(page.getByRole('dialog')).toContainText('Only finance can change the functional class.');
});

test('M-9: when the server says the code is locked, its sentence shows next to the code, and code and company become read-only', async ({ page }) => {
  const { ctl } = await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await tid(page, 'cc-master-row-SALES').getByRole('button', { name: 'Edit', exact: true }).click();
  await tid(page, 'cc-form-code').fill('SALES-X');
  ctl.refuse = { status: 409, body: { error: 'cost_centre_code_locked', message: 'The code of cost centre SALES cannot change because journal lines already carry it. You can rename it.', messageAr: 'لا يمكن تغيير رمز مركز التكلفة SALES.' } };
  await tid(page, 'cc-form-save').click();
  await expect(tid(page, 'cc-form-code-locked-note')).toHaveText('The code of cost centre SALES cannot change because journal lines already carry it. You can rename it.');
  await expect(tid(page, 'cc-form-code')).toHaveValue('SALES');
  await expect(tid(page, 'cc-form-code')).toHaveAttribute('readonly', '');
  await expect(tid(page, 'cc-form-company')).toBeDisabled();
});

test('other refusals show the server\'s sentence in the form and keep what was typed', async ({ page }) => {
  const { ctl } = await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await tid(page, 'cc-master-row-SALES').getByRole('button', { name: 'Edit', exact: true }).click();
  await tid(page, 'cc-form-name').fill('Sales and marketing');
  ctl.refuse = { status: 409, body: { error: 'locked_into_approved_run', message: 'A payroll run for this company is awaiting finance review or lock; reopen it first.', messageAr: 'x' } };
  await tid(page, 'cc-form-save').click();
  await expect(tid(page, 'cc-form-error')).toHaveText('A payroll run for this company is awaiting finance review or lock; reopen it first.');
  await expect(tid(page, 'cc-form-name')).toHaveValue('Sales and marketing');
});

// ═════════════════════════════ Screen 1: deactivate and delete (M-8) ═════════════════════════════

const inUse = { status: 409, body: { error: 'cost_centre_in_use', message: 'Cost centre SALES is used by 2 department(s)…', messageAr: 'مركز التكلفة SALES مستخدم…', counts: { department: 2, employee: 14, position: 0, mapping: 1, companyDefault: 1, activeChild: 1 } } };

test('Deactivate asks for a reason, and a 409 in use shows what uses it with counts and links', async ({ page }, info) => {
  const { ctl, writes } = await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await openMenu(page, 'SALES');
  await page.getByRole('menuitem', { name: 'Deactivate', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: 'Deactivate SALES?' });
  await expect(confirm).toContainText('Posted journal lines keep it. It can no longer be assigned.');
  await confirm.getByRole('button', { name: 'Deactivate', exact: true }).click();
  await expect(confirm).toContainText('Enter a reason for this change.');
  expect(writes).toHaveLength(0);
  await tid(page, 'cc-form-reason').fill('Merged into Riyadh');
  ctl.refuse = inUse;
  await confirm.getByRole('button', { name: 'Deactivate', exact: true }).click();
  const dialog = tid(page, 'cc-inuse-dialog');
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Cost centre SALES is in use' })).toBeVisible();
  await expect(tid(page, 'cc-inuse-count-departments')).toHaveText('2 departments');
  await expect(tid(page, 'cc-inuse-count-employees')).toHaveText('14 employees');
  await expect(tid(page, 'cc-inuse-count-positions')).toHaveCount(0); // zero is left out
  await expect(tid(page, 'cc-inuse-count-rules')).toHaveText('1 posting rule');
  await expect(tid(page, 'cc-inuse-count-children')).toHaveText('1 child cost centre');
  await expect(tid(page, 'cc-inuse-count-default')).toHaveText('Company default');
  await expect(tid(page, 'cc-inuse-count-departments').getByRole('link')).toHaveAttribute('href', /tab=departments/);
  await expect(dialog).toContainText('Move them to another cost centre first.');
  await shot(page, info, 'cc-en-master-in-use');
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(dialog).toHaveCount(0);
  // The reason travelled with the deactivation.
  expect(lastWrite(writes, 'PUT').body).toMatchObject({ isActive: false, reason: 'Merged into Riyadh' });
});

test('Deactivate with nothing using it just deactivates', async ({ page }) => {
  const { writes } = await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await openMenu(page, 'SALES-RY-N');
  await page.getByRole('menuitem', { name: 'Deactivate', exact: true }).click();
  await tid(page, 'cc-form-reason').fill('Closed');
  await page.getByRole('dialog', { name: 'Deactivate SALES-RY-N?' }).getByRole('button', { name: 'Deactivate', exact: true }).click();
  await expect(tid(page, 'cc-master-row-SALES-RY-N')).toContainText('Inactive');
  expect(lastWrite(writes, 'PUT').body).toMatchObject({ isActive: false, reason: 'Closed' });
});

test('Delete confirms, then a 409 in use shows the same counts; a delete with no use removes the row', async ({ page }) => {
  const { ctl } = await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  await openMenu(page, 'SALES');
  await page.getByRole('menuitem', { name: 'Delete', exact: true }).click();
  ctl.refuse = inUse;
  await page.getByRole('dialog', { name: 'Delete SALES?' }).getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(tid(page, 'cc-inuse-dialog')).toBeVisible();
  await tid(page, 'cc-inuse-dialog').getByRole('button', { name: 'Close', exact: true }).first().click();
  await openMenu(page, 'ORPHAN');
  await page.getByRole('menuitem', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete ORPHAN?' }).getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(tid(page, 'cc-master-row-ORPHAN')).toHaveCount(0);
});

test('the row menu opens with the keyboard, moves with the arrow keys and closes with Escape', async ({ page }) => {
  await boot(page);
  await tid(page, 'cc-master-view-flat').click();
  const trigger = tid(page, 'cc-master-row-SALES').getByRole('button', { name: /More actions/ });
  await expect(trigger).toHaveAttribute('aria-haspopup', 'menu');
  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('menuitem', { name: 'Deactivate', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Delete', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu')).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

// ═════════════════════════════ Screen 1: Arabic and accessibility ═════════════════════════════

test('Arabic: RTL, translated headings, classes and refusals; codes stay left to right', async ({ page }, info) => {
  const { ctl } = await boot(page, { locale: 'ar', finance: true });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const table = tid(page, 'cc-master-table');
  await expect(table.getByRole('columnheader', { name: 'الرمز', exact: true })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'رمز ERP', exact: true })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'التصنيف الوظيفي', exact: true })).toBeVisible();
  await tid(page, 'cc-master-expand-SALES').click();
  await expect(tid(page, 'cc-master-row-SALES')).toContainText('البيع والتسويق');
  expect(await tid(page, 'cc-master-row-SALES').locator('bdi[dir="ltr"]').first().textContent()).toBe('SALES');
  await shot(page, info, 'cc-ar-master-tree');
  await tid(page, 'cc-master-row-SALES').getByRole('button', { name: 'تعديل', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'تعديل مركز التكلفة SALES' })).toBeVisible();
  await shot(page, info, 'cc-ar-master-form');
  ctl.refuse = { status: 409, body: { error: 'locked_into_approved_run', message: 'x', messageAr: 'هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل؛ أعد فتحها أولًا.' } };
  await tid(page, 'cc-form-name').fill('مبيعات');
  await tid(page, 'cc-form-save').click();
  await expect(tid(page, 'cc-form-error')).toHaveText('هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل؛ أعد فتحها أولًا.');
});

test('Arabic: the in-use dialog is translated, with plural counts', async ({ page }, info) => {
  const { ctl } = await boot(page, { locale: 'ar' });
  await tid(page, 'cc-master-view-flat').click();
  await openMenu(page, 'SALES');
  await page.getByRole('menuitem', { name: 'حذف', exact: true }).click();
  ctl.refuse = inUse;
  await page.getByRole('dialog', { name: 'حذف SALES؟' }).getByRole('button', { name: 'حذف', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'مركز التكلفة SALES قيد الاستخدام' })).toBeVisible();
  await expect(tid(page, 'cc-inuse-count-departments')).toHaveText('قسمان');
  await expect(tid(page, 'cc-inuse-count-employees')).toHaveText('14 موظفًا');
  await shot(page, info, 'cc-ar-master-in-use');
});

test('axe: the list and the open form have no serious or critical violations', async ({ page }) => {
  await boot(page, { finance: true });
  await tid(page, 'cc-master-expand-SALES').click();
  expect(await axeSeriousOrWorse(page, '[data-testid="cc-master-table"]')).toEqual([]);
  await tid(page, 'cc-master-new').click();
  await expect(page.getByRole('dialog', { name: 'New cost centre' })).toBeVisible();
  await page.waitForTimeout(600);
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

// ═════════════════════════════ Department import: links to clear ═════════════════════════════

const csvFile = (name = 'departments.csv') => ({ name, mimeType: 'text/csv', buffer: Buffer.from('Code,NameEn,CostCenterCode,ParentDepartmentCode\nFIN,Finance,[CLEAR],\n') });
const clearRows = [
  { rowNumber: 2, entityCode: 'FIN', entityName: 'Finance', status: 1, errors: [], warnings: ['Clears the cost centre link of this existing department.'] },
  { rowNumber: 3, entityCode: 'HR', entityName: 'People', status: 1, errors: [], warnings: ['Clears the parent department link of this existing department.'] },
];

async function chooseFile(page: Page) {
  await page.locator('input[type="file"]').first().setInputFiles(csvFile());
}

test('the department import preview counts the links the file would clear, and Confirm needs the acknowledgement and a reason', async ({ page }, info) => {
  const { ctl, writes } = await boot(page, {}, '/setup?tab=departments');
  ctl.previewLinks = 2;
  ctl.previewRows = clearRows;
  await chooseFile(page);
  const dialog = page.getByRole('dialog', { name: /Review import/ });
  const warning = tid(page, 'cc-import-clear-warning');
  await expect(warning).toContainText('This file would clear 2 existing cost-centre or parent links.');
  await expect(dialog).toContainText('Clears the cost centre link of this existing department.');
  await expect(dialog).toContainText('Clears the parent department link of this existing department.');
  const confirm = dialog.getByRole('button', { name: 'Confirm import', exact: true });
  await expect(confirm).toBeDisabled();
  await tid(page, 'cc-import-clear-ack').check();
  await expect(confirm).toBeDisabled();
  await tid(page, 'cc-import-clear-reason').fill('Restructure');
  await expect(confirm).toBeEnabled();
  await shot(page, info, 'cc-en-department-import-clear');
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
  await confirm.click();
  await expect.poll(() => writes.filter((w) => w.path === '/api/departments/import').length).toBe(1);
  expect(lastWrite(writes, 'POST').body).toMatchObject({ acknowledgeClearedLinks: true, reason: 'Restructure' });
});

test('a file that clears nothing shows no gate, and the commit carries neither acknowledgement nor reason', async ({ page }) => {
  const { ctl, writes } = await boot(page, {}, '/setup?tab=departments');
  ctl.previewLinks = 0;
  ctl.previewRows = [{ rowNumber: 2, entityCode: 'FIN', entityName: 'Finance', status: 0, errors: [], warnings: [] }];
  await chooseFile(page);
  const dialog = page.getByRole('dialog', { name: /Review import/ });
  await expect(dialog.getByRole('button', { name: 'Confirm import', exact: true })).toBeEnabled();
  await expect(tid(page, 'cc-import-clear-warning')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Confirm import', exact: true }).click();
  await expect.poll(() => writes.filter((w) => w.path === '/api/departments/import').length).toBe(1);
  const body = lastWrite(writes, 'POST').body;
  expect(body).not.toHaveProperty('acknowledgeClearedLinks');
  expect(body).not.toHaveProperty('reason');
});

test('the import help says blank means unchanged and [CLEAR] removes the link, in the preview and on the template button', async ({ page }) => {
  const { ctl } = await boot(page, {}, '/setup?tab=departments');
  ctl.previewRows = [{ rowNumber: 2, entityCode: 'FIN', entityName: 'Finance', status: 0, errors: [], warnings: [] }];
  const help = 'Leave a cost-centre or parent cell blank to keep the current value. Write [CLEAR] to remove it.';
  await expect(page.getByRole('button', { name: 'CSV import template', exact: true })).toHaveAttribute('title', new RegExp(help.replace(/[[\]]/g, '\\$&')));
  await chooseFile(page);
  await expect(tid(page, 'cc-import-help')).toHaveText(help);
});

test('a refusal on commit (the server says the links were not acknowledged) is shown, not swallowed', async ({ page }) => {
  const { ctl } = await boot(page, {}, '/setup?tab=departments');
  ctl.previewLinks = 1;
  ctl.previewRows = [clearRows[0]];
  await chooseFile(page);
  await tid(page, 'cc-import-clear-ack').check();
  await tid(page, 'cc-import-clear-reason').fill('x');
  ctl.importRefusal = { status: 409, body: { error: 'links_clear_not_acknowledged', message: 'This file would clear 1 existing cost-centre or parent link(s). Review the preview and confirm to continue.' } };
  await page.getByRole('dialog', { name: /Review import/ }).getByRole('button', { name: 'Confirm import', exact: true }).click();
  await expect(page.getByText(/This file would clear 1 existing cost-centre or parent link/).first()).toBeVisible();
});

test('Arabic: the links-to-clear gate and help are translated', async ({ page }, info) => {
  const { ctl } = await boot(page, { locale: 'ar' }, '/setup?tab=departments');
  ctl.previewLinks = 2;
  ctl.previewRows = clearRows;
  await chooseFile(page);
  await expect(tid(page, 'cc-import-clear-warning')).toContainText('سيمسح هذا الملف');
  await expect(tid(page, 'cc-import-help')).toContainText('[CLEAR]');
  await shot(page, info, 'cc-ar-department-import-clear');
});
