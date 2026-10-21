import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { translate } from '../src/i18n/translations';
import type {
  CompanySetupContext, CompanySetupDraft, CompanySetupDraftBody, CompanySetupPlan, CompanySetupResult, PlanItem,
} from '../src/api/companySetup';

/**
 * Create-company wizard — real browser interactions against an in-memory implementation of the
 * /api/company-setup contract (docs/design/company-setup-wizard.md). This proves the UI journey and
 * the request shapes (autosave versions, Idempotency-Key, dry-run before Create); server rules,
 * authorization and atomicity are proven by the backend's CompanySetup tests, not here.
 *
 * Collected by the Browser Pilot `chromium` project like setup-experience.spec.ts. Locally:
 *   npx next dev -p 5174   (never :5173 — that is a prebuilt image)
 *   npx playwright test -c e2e/playwright.company-setup-wizard.config.ts
 * WIZARD_SHOTS_DIR=<dir> also saves a screenshot of every step.
 */

type Locale = 'en' | 'ar';
type Persona = 'groupAdmin' | 'groupHrManager' | 'companyAdmin';

interface BootOptions {
  locale?: Locale;
  persona?: Persona;
  /** Cost-centre codes the planner reports as clashing with a sister company. */
  clashingCostCentres?: string[];
  context?: Partial<CompanySetupContext>;
  /** Admin role ceiling says the viewer cannot assign Admin. */
  adminRefused?: boolean;
}

const sister = { id: 'sister-1', legalNameEn: 'Sister Trading KSA', countryCode: 'SA' };

/** Persists across page loads and sign-out, like the server would. */
class ContractServer {
  drafts = new Map<string, CompanySetupDraft>();
  puts: { id: string; version: number; step: number; body: CompanySetupDraftBody }[] = [];
  commits: { key: string | undefined; draftId: string; version: number }[] = [];
  dryRuns: CompanySetupDraftBody[] = [];
  results = new Map<string, CompanySetupResult>();
  errors: string[] = [];
  sequence = 0;
  constructor(public options: BootOptions) {}

  get scopeGroup() { return this.options.persona !== 'companyAdmin'; }

  context(): CompanySetupContext {
    const base: CompanySetupContext = {
      canCreate: this.scopeGroup,
      refusalCode: this.scopeGroup ? null : 'not_group_scope',
      refusalMessage: this.scopeGroup ? null : 'Only a group administrator can create a company.',
      creationMode: 'GroupSelfServiceWithinLimit', accountType: 'Group',
      companiesUsed: 2, companiesAllowed: 5, isGroupScope: this.scopeGroup,
      sisterCompanies: [sister],
      sharedCatalog: {
        departments: [{ code: 'HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية' }, { code: 'FIN', nameEn: 'Finance', nameAr: 'المالية' }],
        designations: [{ code: 'ACC', nameEn: 'Accountant', nameAr: 'محاسب' }],
        grades: [{ code: 'G1', nameEn: 'Grade 1', nameAr: 'الدرجة 1', currency: 'SAR' }],
      },
      gradesCurrency: 'SAR',
    };
    return { ...base, ...this.options.context };
  }

  defaults(country: string): CompanySetupDraftBody {
    return {
      startFrom: 'defaults',
      company: { legalNameEn: '', legalNameAr: '', countryCode: country, currency: country === 'SA' ? 'SAR' : 'AED', registrationNumber: '', firstPayrollMonth: '2026-11', workEmailPattern: 'first.last' },
      branches: [{ code: 'HQ', nameEn: 'Head Office', nameAr: 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [] }],
      costCentres: [], departments: [{ code: 'HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية' }], designations: [], grades: [],
      rules: {
        workWeek: { weekendDays: 'Fri,Sat', override: false }, holidayCalendar: { usePack: true }, leavePolicies: { usePack: true },
        employeeNumbering: { prefix: 'EMP', useCountryPrefix: false, useBranchPrefix: false, useDepartmentPrefix: false, useYear: false, paddingLength: 4, nextSequence: 1 },
      },
      companyAdmin: { mode: 'groupOnly' },
    };
  }

  plan(body: CompanySetupDraftBody): CompanySetupPlan {
    const catalog = this.context().sharedCatalog;
    const has = (list: { code: string }[], code: string) => list.some(c => c.code.toUpperCase() === code.toUpperCase());
    const items: PlanItem[] = [
      { area: 'company', code: body.company.registrationNumber, label: body.company.legalNameEn, action: 'create' },
      { area: 'cutover', code: body.company.firstPayrollMonth, label: body.company.firstPayrollMonth, action: 'create' },
      ...body.branches.flatMap(b => [
        { area: 'branch' as const, code: b.code, label: b.nameEn, action: 'create' as const },
        ...b.locations.map(l => ({ area: 'location' as const, code: l.code, label: l.nameEn, action: 'create' as const })),
      ]),
      ...body.costCentres.map(c => (this.options.clashingCostCentres ?? []).includes(c.code)
        ? { area: 'costCentre' as const, code: c.code, label: c.nameEn, action: 'conflict' as const, reason: `Cost centre ${c.code} is already used by ${sister.legalNameEn}. Use another code.` }
        : { area: 'costCentre' as const, code: c.code, label: c.nameEn, action: 'create' as const }),
      ...body.departments.map(d => ({ area: 'department' as const, code: d.code, label: d.nameEn, action: has(catalog.departments, d.code) ? 'reuse' as const : 'create' as const })),
      ...body.designations.map(d => ({ area: 'designation' as const, code: d.code, label: d.titleEn, action: has(catalog.designations, d.code) ? 'reuse' as const : 'create' as const })),
      ...body.grades.map(g => ({ area: 'grade' as const, code: g.code, label: g.nameEn, action: has(catalog.grades, g.code) ? 'reuse' as const : 'create' as const })),
      { area: 'workWeek', code: body.rules.workWeek.weekendDays, label: 'Working week', action: body.rules.workWeek.override ? 'create' : 'reuse' },
      { area: 'gccSetting', code: body.company.countryCode, label: 'End-of-service (Saudi Labour Law)', labelAr: 'مكافأة نهاية الخدمة (نظام العمل السعودي)', action: 'create' },
      ...(body.rules.holidayCalendar.usePack ? [{ area: 'holidayCalendar' as const, code: 'SA-2026', label: 'Saudi public holidays 2026', action: 'reuse' as const }] : []),
      ...(body.rules.leavePolicies.usePack ? [
        { area: 'leavePolicy' as const, code: 'ANNUAL', label: 'Annual leave (21 days)', action: 'create' as const },
        { area: 'leavePolicy' as const, code: 'SICK', label: 'Sick leave', action: 'create' as const },
      ] : []),
      { area: 'employeeIdRule', code: body.rules.employeeNumbering.prefix, label: 'Employee numbering', action: 'create' },
      ...(body.companyAdmin.mode !== 'groupOnly' ? [{ area: 'companyAdmin' as const, code: body.companyAdmin.email ?? body.companyAdmin.existingUserId ?? '', label: body.companyAdmin.fullNameEn ?? 'Existing user', action: 'create' as const }] : []),
    ];
    return { ok: !items.some(i => i.action === 'conflict'), items, problems: [], warnings: [] };
  }

  summaries() {
    return [...this.drafts.values()].filter(d => d.status === 'Open').map(d => ({ id: d.id, legalNameEn: d.body.company.legalNameEn, step: d.step, updatedAtUtc: d.updatedAtUtc, createdByName: d.createdByName }));
  }

  /** Simulates a save from another tab or device. */
  touchElsewhere(id: string) {
    const d = this.drafts.get(id)!;
    this.drafts.set(id, { ...d, version: d.version + 1, body: { ...d.body, company: { ...d.body.company, tradeName: 'Saved elsewhere' } }, updatedAtUtc: new Date().toISOString() });
  }

  async handle(route: Route, rest: string): Promise<void> {
    const req = route.request();
    const method = req.method();
    const url = new URL(req.url());
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const now = new Date().toISOString();
    if (!this.scopeGroup && rest !== '/context') return reply({ message: 'Only a group administrator can create a company.' }, 403);
    if (rest === '/context' && method === 'GET') return reply(this.context());
    if (rest === '/defaults' && method === 'GET') return reply(this.defaults(url.searchParams.get('country') ?? 'SA'));
    if (rest === '/drafts' && method === 'GET') return reply(this.summaries());
    if (rest === '/drafts' && method === 'POST') {
      const body = req.postDataJSON() as CompanySetupDraftBody;
      const draft: CompanySetupDraft = { id: `draft-${++this.sequence}`, version: 1, step: 1, status: 'Open', body, createdByUserId: 'u-admin', createdByName: 'Group Owner', createdAtUtc: now, updatedAtUtc: now };
      this.drafts.set(draft.id, draft);
      return reply(draft, 201);
    }
    const draftMatch = rest.match(/^\/drafts\/([^/]+)$/);
    if (draftMatch) {
      const draft = this.drafts.get(draftMatch[1]);
      if (!draft) return reply({ message: 'Not found' }, 404);
      if (method === 'GET') return reply(draft);
      if (method === 'DELETE') { this.drafts.set(draft.id, { ...draft, status: 'Discarded' }); return route.fulfill({ status: 204 }); }
      if (method === 'PUT') {
        const put = req.postDataJSON() as { version: number; step: number; body: CompanySetupDraftBody };
        this.puts.push({ id: draft.id, ...put });
        if (put.version !== draft.version) return reply({ error: 'draft_version_conflict', message: 'This draft was changed elsewhere.' }, 409);
        const saved = { ...draft, version: draft.version + 1, step: put.step, body: put.body, updatedAtUtc: now };
        this.drafts.set(draft.id, saved);
        return reply(saved);
      }
    }
    if (rest === '/dry-run' && method === 'POST') {
      const body = req.postDataJSON() as CompanySetupDraftBody;
      this.dryRuns.push(body);
      return reply(this.plan(body));
    }
    if (rest === '' && method === 'POST') {
      const { draftId, version } = req.postDataJSON() as { draftId: string; version: number };
      const key = req.headers()['idempotency-key'];
      this.commits.push({ key, draftId, version });
      const previous = key ? this.results.get(key) : undefined;
      if (previous) return reply({ ...previous, replayed: true });
      const draft = this.drafts.get(draftId);
      if (!draft || draft.version !== version) return reply({ message: 'This draft was changed elsewhere.' }, 409);
      const plan = this.plan(draft.body);
      if (!plan.ok) return reply({ message: 'A cost centre code clashes with a sister company.', step: 4 }, 409);
      this.drafts.set(draftId, { ...draft, status: 'Submitted', resultCompanyId: 'new-company' });
      const result: CompanySetupResult = {
        companyId: 'new-company', replayed: false,
        created: { company: 1, branch: draft.body.branches.length }, reused: { department: 1 },
        readiness: [
          { key: 'glMapping', label: 'GL mapping', labelAr: 'ربط دفتر الأستاذ', status: 'todo', scope: 'company', href: '/setup?tab=glMapping&companyId=new-company' },
          { key: 'bankWps', label: 'Bank and WPS settings (before first payment)', labelAr: 'إعدادات البنك وحماية الأجور (قبل أول دفعة)', status: 'todo', scope: 'company', href: '/setup?tab=gccSettings&companyId=new-company' },
          { key: 'companyAdmin', label: 'Company super admin', labelAr: 'المسؤول الأعلى للشركة', status: draft.body.companyAdmin.mode === 'groupOnly' ? 'recommended' : 'done', scope: 'company', href: '/user-management?companyId=new-company' },
          { key: 'smtp', label: 'Outgoing email (SMTP)', labelAr: 'البريد الصادر (SMTP)', status: 'todo', scope: 'workspace', href: '/setup?tab=emailConfig' },
        ],
      };
      if (key) this.results.set(key, result);
      return reply(result, 201);
    }
    this.errors.push(`unexpected ${method} /api/company-setup${rest}`);
    return reply({ message: 'unexpected' }, 500);
  }
}

function me(server: ContractServer) {
  const persona = server.options.persona ?? 'groupAdmin';
  const roles = persona === 'groupHrManager' ? ['HR Manager'] : ['Admin'];
  return {
    id: 'u-admin', tenantId: 'wizard-tenant', tenantSlug: 'wizard-fixture', fullName: 'Group Owner', email: 'owner@wizard.test',
    roles,
    permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'dashboard.read', 'users.read', 'users.write'],
    accountType: 'Group', isGroupScope: persona !== 'companyAdmin',
    companies: [{ id: sister.id, name: sister.legalNameEn, code: 'SIS', countryCode: 'SA', isActive: true }],
  };
}

async function boot(page: Page, options: BootOptions = {}, server = new ContractServer(options)) {
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.addInitScript(({ locale }) => {
    if (!sessionStorage.getItem('wizard-boot')) {
      localStorage.setItem('zayra_access_token', 'wizard-fixture-token');
      sessionStorage.setItem('wizard-boot', '1');
    }
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (p.startsWith('/api/company-setup')) return server.handle(route, p.slice('/api/company-setup'.length));
    if (p === '/api/auth/me') return reply(me(server));
    if (p === '/api/auth/login') return reply({ accessToken: 'wizard-fixture-token-2', refreshToken: 'wizard-refresh-2', expiresAtUtc: '2099-01-01T00:00:00Z', user: me(server) });
    if (p === '/api/auth/logout') return route.fulfill({ status: 204 });
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/access/ceiling') return reply({
      userId: 'u-admin', isAdmin: true, heldPermissions: me(server).permissions,
      roles: [{ roleId: 'r-admin', name: 'Admin', canAssign: !server.options.adminRefused, assignRefusalEn: server.options.adminRefused ? 'Only an administrator can give the Admin role.' : null }],
    });
    if (p === '/api/access/users') return reply(paged([
      { id: 'u-sara', email: 'sara@riyadhtrading.sa', fullName: 'Sara Al-Qahtani', status: 'Active', isActive: true, isLocked: false, mustChangePassword: false, roles: ['HR Manager'], accessMode: 'Standard', createdAtUtc: '2026-01-01T00:00:00Z', phoneNumber: '' },
    ]));
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged([{ ...sister, legalNameAr: '', tradeName: 'Sister', jurisdiction: 'SA', registrationNumber: '1010000001', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active', taxNumber: '', wpsEmployerId: '', gosiEmployerId: '', qiwaEstablishmentId: '', emailDomain: '', workEmailPattern: 'first.last' }]));
    if (p === '/api/country-packs/available') return reply([]);
    return reply(paged([]));
  });
  return { server, pageErrors };
}

const T = (locale: Locale) => (key: string, params?: Record<string, string | number>) => translate(locale, key, params);

async function contained(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'page must not scroll sideways').toBe(true);
}

async function shot(page: Page, info: TestInfo, name: string) {
  const dir = process.env.WIZARD_SHOTS_DIR;
  const file = dir ? path.join(dir, `${name}-${info.project.name}.png`) : info.outputPath(`${name}.png`);
  if (dir) fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

async function expectStep(page: Page, t: ReturnType<typeof T>, heading: string) {
  await expect(page.getByRole('heading', { level: 2, name: t(heading), exact: true })).toBeVisible();
}

async function next(page: Page, t: ReturnType<typeof T>) {
  await page.getByRole('button', { name: t('Next'), exact: true }).click();
}

async function openWizardFromCompanies(page: Page, t: ReturnType<typeof T>) {
  await page.goto('/setup?tab=companies');
  await page.getByRole('button', { name: t('Create company'), exact: true }).click();
  await expect(page).toHaveURL(/\/setup\/new-company/);
  await expectStep(page, t, 'Create a company');
}

async function startAndFillCompany(page: Page, t: ReturnType<typeof T>) {
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await page.getByLabel(t('Legal name (English)')).fill('Riyadh Trading Company');
  await page.getByLabel(t('Legal name (Arabic)')).fill('شركة الرياض للتجارة');
  await page.getByLabel(t('Commercial registration (CR) number')).fill('1010123456');
  await page.getByLabel(t('MOL establishment number')).fill('7-1234567');
  await page.getByLabel(t('Work email domain (optional)')).fill('riyadhtrading.sa');
}

for (const locale of ['en', 'ar'] as const) {
  test(`seven-step happy path creates the company in one commit (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
    await openWizardFromCompanies(page, t);
    await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
    await expect(page.getByTestId('plan-usage')).toContainText(t('{used} of {allowed} companies on your plan', { used: 2, allowed: 5 }));
    await contained(page);
    await shot(page, info, `${locale}-1-start`);

    await startAndFillCompany(page, t);
    await expect(page.getByLabel(t('Legal name (Arabic)'))).toHaveAttribute('dir', 'rtl');
    // SA locks the currency to SAR and mirrors the MOL number into Qiwa.
    await expect(page.getByLabel(t('Salary currency'))).toHaveValue('SAR');
    await expect(page.getByLabel(t('Salary currency'))).toBeDisabled();
    await expect(page.getByLabel(t('Qiwa establishment ID (optional)'))).toHaveValue('7-1234567');
    // The tree names the company in the reader's language.
    await expect(page.getByTestId('structure-tree')).toContainText(locale === 'ar' ? 'شركة الرياض للتجارة' : 'Riyadh Trading Company');
    await contained(page);
    await shot(page, info, `${locale}-2-company`);
    await next(page, t);

    await expectStep(page, t, 'Branches & locations');
    await expect(page.getByTestId('branches-row')).toHaveCount(1);
    await expect(page.getByText(t('No locations yet. Until you add one, employees can punch in at any geofenced site in the workspace. Add a location to each branch, or add them later in Setup → Locations.'))).toBeVisible();
    const hq = page.getByTestId('branches-row').first();
    await hq.locator('summary').click();
    await hq.getByRole('button', { name: t('Add location'), exact: true }).click();
    const location = page.getByTestId('branches.0.locations-row').first();
    await location.getByLabel(t('Code')).fill('GATE1');
    await location.getByLabel(t('Name (English)')).fill('Main gate');
    await expect(location.getByLabel(t('Name (Arabic)'))).toHaveAttribute('dir', 'rtl');
    await expect(page.getByTestId('structure-tree')).toContainText('GATE1');
    await contained(page);
    await shot(page, info, `${locale}-3-branches`);
    await next(page, t);

    await expectStep(page, t, 'Organisation');
    await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
    const cc = page.getByTestId('costCentres-row').first();
    await cc.getByLabel(t('Code')).fill('ADM');
    await cc.getByLabel(t('Name (English)')).fill('Administration');
    await cc.getByLabel(t('Name (Arabic)')).fill('الإدارة');
    // HR came from the defaults and is in the shared catalog: a read-only summary, never an editor.
    const hr = page.getByTestId('departments-row').first();
    await expect(hr).toHaveAttribute('data-reused', 'true');
    await expect(hr).toContainText(t('Reused'));
    await expect(hr).toContainText('Human Resources');
    await expect(hr).toContainText('الموارد البشرية');
    await expect(hr.locator('input, select')).toHaveCount(0);
    await expect(hr.getByRole('button', { name: new RegExp(t('Remove from this company')) })).toBeVisible();
    await page.getByRole('button', { name: t('Use {code} · {name}', { code: 'FIN', name: locale === 'ar' ? 'المالية' : 'Finance' }) }).click();
    await expect(page.getByTestId('departments-row').nth(1).locator('input, select')).toHaveCount(0);
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const ops = page.getByTestId('departments-row').nth(2);
    await ops.getByLabel(t('Code')).fill('OPS');
    await ops.getByLabel(t('Name (English)')).fill('Operations');
    await ops.getByLabel(t('Cost centre')).selectOption('ADM');
    await expect(page.getByTestId('structure-tree')).toContainText(t('Shared group catalog'));
    await contained(page);
    await shot(page, info, `${locale}-4-organisation`);
    await next(page, t);

    await expectStep(page, t, 'Work & pay rules');
    await expect(page.getByTestId('work-week-mode')).toHaveText(t('This company follows the country working week.'));
    await page.getByLabel(t('Prefix')).fill('RTC');
    await expect(page.getByTestId('numbering-preview')).toContainText('RTC-0001');
    // Plan labels come in the reader's language when the server sends labelAr.
    await expect(page.getByText(locale === 'ar' ? 'مكافأة نهاية الخدمة (نظام العمل السعودي)' : 'End-of-service (Saudi Labour Law)')).toBeVisible();
    await contained(page);
    await shot(page, info, `${locale}-5-rules`);
    await next(page, t);

    await expectStep(page, t, 'Company super admin');
    await page.getByLabel(t('Invite a new company super admin')).check();
    await page.getByLabel(t('Full name (English)')).fill('Sara Al-Qahtani');
    await page.getByLabel(t('Official work email')).fill('sara@gmail.com');
    await next(page, t);
    // Off-domain email blocks Next on the field, with what to do next.
    await expect(page.locator('#ccw-companyAdmin-email-error')).toHaveText(t('Use an address on {domain}. KynexOne never creates mailboxes, so the administrator signs in with their company email.', { domain: 'riyadhtrading.sa' }));
    await expect(page.getByLabel(t('Official work email'))).toBeFocused();
    await page.getByLabel(t('Official work email')).fill('sara@riyadhtrading.sa');
    await contained(page);
    await shot(page, info, `${locale}-6-admin`);
    await next(page, t);

    await expectStep(page, t, 'Review & create');
    await expect(page.getByTestId('review-plan')).toBeVisible();
    await expect(page.getByTestId('plan-group-4')).toContainText('Human Resources');
    await expect(page.getByTestId('plan-group-6')).toContainText('Sara Al-Qahtani');
    const create = page.getByRole('button', { name: t('Create company'), exact: true });
    await expect(create).toBeEnabled();
    await contained(page);
    await shot(page, info, `${locale}-7-review`);
    await create.click();

    await expect(page.getByTestId('create-company-success')).toBeVisible();
    await expect(page.getByRole('heading', { name: t('{name} is created', { name: locale === 'ar' ? 'شركة الرياض للتجارة' : 'Riyadh Trading Company' }) })).toBeVisible();
    const gl = page.getByTestId('readiness-glMapping');
    await expect(gl).toContainText(locale === 'ar' ? 'ربط دفتر الأستاذ' : 'GL mapping');
    await expect(gl.getByRole('link')).toHaveAttribute('href', '/setup?tab=glMapping&companyId=new-company');
    await expect(page.getByTestId('company-readiness')).toContainText(t('Applies to all companies'));
    await contained(page);
    await shot(page, info, `${locale}-8-created`);

    // One commit, keyed by the draft, naming the version the server last saved.
    expect(server.commits).toHaveLength(1);
    const [commit] = server.commits;
    expect(commit.key).toBe(commit.draftId);
    expect(commit.version).toBe(server.drafts.get(commit.draftId)!.version);
    const saved = server.drafts.get(commit.draftId)!.body;
    expect(saved.company).toMatchObject({ legalNameEn: 'Riyadh Trading Company', registrationNumber: '1010123456', molEstablishmentNumber: '7-1234567', qiwaEstablishmentId: '7-1234567', currency: 'SAR' });
    expect(saved.branches[0].locations).toEqual([expect.objectContaining({ code: 'GATE1', nameEn: 'Main gate' })]);
    expect(saved.departments.map(d => d.code)).toEqual(['HR', 'FIN', 'OPS']);
    expect(saved.companyAdmin).toMatchObject({ mode: 'invite', email: 'sara@riyadhtrading.sa' });
    expect(server.dryRuns.length).toBeGreaterThan(0);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('save draft, sign out, sign in, resume where it stopped', async ({ page }, info) => {
  test.skip(info.project.name === 'mobile', 'The user menu with Sign out is desktop-only (TopBar).');
  const t = T('en');
  const { server, pageErrors } = await boot(page);
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await expect(page.getByTestId('autosave-status')).toHaveAttribute('data-state', 'saved', { timeout: 10_000 });
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  await page.getByRole('button', { name: t('Save draft & exit'), exact: true }).click();
  await expect(page).toHaveURL(/\/setup\?tab=companies/);
  const [draft] = [...server.drafts.values()];
  expect(draft.step).toBe(3);
  expect(draft.body.company.legalNameEn).toBe('Riyadh Trading Company');
  // Every autosave named the version it was based on, and none was refused.
  expect(server.puts.every((put, i) => put.version === i + 1)).toBe(true);

  await page.getByRole('button', { name: 'Open user menu' }).click();
  await page.locator('header').getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page).toHaveURL(/\/login/);
  await page.locator('#li-em').fill('owner@wizard.test');
  await page.locator('#li-pw').fill('a-fixture-password');
  await page.locator('#li-pw').press('Enter');
  await expect(page).not.toHaveURL(/\/login/);

  await page.goto('/setup?tab=companies');
  const card = page.getByTestId('company-drafts-card');
  await expect(card).toContainText('Riyadh Trading Company');
  await expect(card).toContainText(t('Stopped at step {step}: {title}', { step: 3, title: t('Branches & locations') }));
  await card.getByRole('link', { name: /Resume/ }).click();
  await expectStep(page, t, 'Branches & locations');
  await page.getByRole('button', { name: t('Back'), exact: true }).click();
  await expect(page.getByLabel(t('Legal name (English)'))).toHaveValue('Riyadh Trading Company');
  await expect(page.getByLabel(t('MOL establishment number'))).toHaveValue('7-1234567');
  expect(server.errors).toEqual([]);
  expect(pageErrors).toEqual([]);
});

test('a draft saved elsewhere stops autosave and offers a reload', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await expect(page.getByTestId('autosave-status')).toHaveAttribute('data-state', 'saved', { timeout: 10_000 });
  const [draft] = [...server.drafts.values()];
  server.touchElsewhere(draft.id);
  await page.getByLabel(t('Trade name (optional)')).fill('My edit');
  await expect(page.getByText(t('This draft was changed elsewhere — reload'))).toBeVisible({ timeout: 10_000 });
  const putsAtConflict = server.puts.length;
  await page.getByRole('button', { name: t('Reload draft'), exact: true }).click();
  await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue('Saved elsewhere');
  await expect(page.getByText(t('This draft was changed elsewhere — reload'))).toHaveCount(0);
  expect(server.puts.length).toBe(putsAtConflict); // the stale copy was never re-sent
});

test('a conflict in the plan blocks Create until it is fixed', async ({ page }, info) => {
  const t = T('en');
  const { server } = await boot(page, { clashingCostCentres: ['ADM'] });
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await next(page, t);
  await next(page, t);
  await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
  const cc = page.getByTestId('costCentres-row').first();
  await cc.getByLabel(t('Code')).fill('ADM');
  await cc.getByLabel(t('Name (English)')).fill('Administration');
  await next(page, t);
  await next(page, t);
  await next(page, t); // step 6: group super admins (default) is valid
  await expectStep(page, t, 'Review & create');
  await expect(page.getByTestId('review-plan')).toContainText('Cost centre ADM is already used by Sister Trading KSA. Use another code.');
  await expect(page.getByRole('button', { name: t('Create company'), exact: true })).toBeDisabled();
  await expect(page.getByText(t('Create is available once every conflict above is resolved.'))).toBeVisible();
  await shot(page, info, 'en-7-review-conflict');
  await page.getByRole('button', { name: t('Go to {step}', { step: t('Organisation') }) }).click();
  await expectStep(page, t, 'Organisation');
  await page.getByTestId('costCentres-row').first().getByLabel(t('Code')).fill('ADM2');
  for (let i = 0; i < 3; i++) await next(page, t);
  await expectStep(page, t, 'Review & create');
  await expect(page.getByRole('button', { name: t('Create company'), exact: true })).toBeEnabled();
  expect(server.commits).toEqual([]);
});

test('a company-scoped admin is refused at step 1 and never sees the super admin step', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page, { persona: 'companyAdmin' });
  await page.goto('/setup/new-company');
  await expectStep(page, t, 'Create a company');
  await expect(page.getByText(t('A new company cannot be created right now.'))).toBeVisible();
  await expect(page.getByText(t('Only a group administrator can create a company. Ask your group super admin.'))).toBeVisible();
  await expect(page.getByTestId('wizard-rail').getByRole('listitem')).toHaveCount(6);
  await expect(page.getByTestId('wizard-rail')).not.toContainText(t('Company super admin'));
  await expect(page.getByRole('button', { name: t('Start'), exact: true })).toBeDisabled();
  expect([...server.drafts.values()]).toEqual([]);
});

for (const variant of ['group HR manager', 'admin whose role ceiling refuses Admin', 'admin the server says cannot grant it'] as const) {
  test(`the super admin step is hidden for a ${variant}, and the draft keeps group-only administration`, async ({ page }) => {
    const t = T('en');
    const { server } = await boot(page, variant === 'group HR manager' ? { persona: 'groupHrManager' }
      : variant === 'admin whose role ceiling refuses Admin' ? { adminRefused: true }
      : { context: { canGrantCompanyAdmin: false } });
    await openWizardFromCompanies(page, t);
    await expect(page.getByTestId('wizard-rail').getByRole('listitem')).toHaveCount(6);
    await expect(page.getByTestId('wizard-rail')).not.toContainText(t('Company super admin'));
    await startAndFillCompany(page, t);
    for (let i = 0; i < 4; i++) await next(page, t);
    await expectStep(page, t, 'Review & create');
    await expect(page.getByTestId('review-plan')).toBeVisible();
    await expect(page.getByTestId('plan-group-6')).toHaveCount(0);
    await expect.poll(() => server.dryRuns.at(-1)?.companyAdmin).toEqual({ mode: 'groupOnly' });
  });
}

test('the wizard is axe-clean on the company step and the review in Arabic', async ({ page }) => {
  const t = T('ar');
  await boot(page, { locale: 'ar' });
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  const company = await new AxeBuilder({ page }).include('[data-testid="create-company-wizard"]').analyze();
  expect(company.violations.filter(v => v.impact === 'critical' || v.impact === 'serious')).toEqual([]);
});

test('the server answer on granting a company super admin wins over the role ceiling', async ({ page }) => {
  const t = T('en');
  await boot(page, { adminRefused: true, context: { canGrantCompanyAdmin: true } });
  await openWizardFromCompanies(page, t);
  await expect(page.getByTestId('wizard-rail').getByRole('listitem')).toHaveCount(7);
  await expect(page.getByTestId('wizard-rail')).toContainText(t('Company super admin'));
});

for (const locale of ['en', 'ar'] as const) {
  test(`use defaults asks inline before replacing entered rows, and a row typed past a shared code stays editable (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    await boot(page, { locale });
    await openWizardFromCompanies(page, t);
    await startAndFillCompany(page, t);
    await next(page, t);
    // Untouched defaults apply without a question.
    await page.getByRole('button', { name: t('Use defaults'), exact: true }).click();
    await expect(page.getByTestId('use-defaults-confirm')).toHaveCount(0);
    await page.getByRole('button', { name: t('Add branch'), exact: true }).click();
    const jeddah = page.getByTestId('branches-row').nth(1);
    await jeddah.getByLabel(t('Code')).fill('JED');
    await page.getByRole('button', { name: t('Use defaults'), exact: true }).click();
    const confirm = page.getByTestId('use-defaults-confirm');
    await expect(confirm).toContainText(t('Replace the {count} rows in {section} with the defaults? What you entered in this section will be removed.', { count: 2, section: t('Branches') }));
    await shot(page, info, `${locale}-3-use-defaults-confirm`);
    await confirm.getByRole('button', { name: t('Keep my rows'), exact: true }).click();
    await expect(page.getByTestId('branches-row')).toHaveCount(2);
    await page.getByRole('button', { name: t('Use defaults'), exact: true }).click();
    await page.getByTestId('use-defaults-confirm').getByRole('button', { name: t('Replace with defaults'), exact: true }).click();
    await expect(page.getByTestId('branches-row')).toHaveCount(1);
    await next(page, t);

    await expectStep(page, t, 'Organisation');
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const typed = page.getByTestId('departments-row').nth(1);
    const code = typed.getByLabel(t('Code'));
    await code.pressSequentially('FINANCE');
    await expect(code).toHaveValue('FINANCE');
    await expect(code).toBeFocused();
    await expect(page.getByTestId('departments-row').nth(1)).not.toHaveAttribute('data-reused', 'true');
    // A section with entered rows asks too.
    await typed.getByLabel(t('Name (English)')).fill('Finance and accounts');
    await page.getByRole('heading', { level: 3, name: t('Departments'), exact: true }).locator('xpath=../..').getByRole('button', { name: t('Use defaults'), exact: true }).click();
    await expect(page.getByTestId('use-defaults-confirm')).toBeVisible();
    await shot(page, info, `${locale}-4-use-defaults-confirm`);
  });
}

for (const locale of ['en', 'ar'] as const) test(`the app shell stays in its place while the wizard scrolls, viewport captures (${locale})`, async ({ page }, info) => {
  test.skip(info.project.name === 'mobile', 'Desktop shell: sidebar and top bar.');
  {
    const t = T(locale);
    await boot(page, { locale });
    await openWizardFromCompanies(page, t);
    await startAndFillCompany(page, t);
    const check = async (name: string) => {
      for (const where of ['top', 'bottom'] as const) {
        await page.evaluate(w => window.scrollTo(0, w === 'top' ? 0 : document.documentElement.scrollHeight), where);
        await page.waitForTimeout(150);
        // The point in the middle of the form belongs to the wizard, not to the shell drawn over it.
        const owner = await page.evaluate(() => {
          const card = document.querySelector('[data-testid="create-company-wizard"]')!.getBoundingClientRect();
          const x = card.left + card.width / 2;
          const y = Math.min(Math.max(card.top + 40, 120), window.innerHeight - 40);
          const el = document.elementFromPoint(x, y);
          return el?.closest('[data-testid="create-company-wizard"]') ? 'wizard' : el?.closest('header, aside, nav')?.tagName ?? el?.tagName;
        });
        expect(owner, `${name} ${where}`).toBe('wizard');
        const dir = process.env.WIZARD_SHOTS_DIR;
        await page.screenshot({ path: dir ? path.join(dir, `${locale}-${name}-viewport-${where}-desktop.png`) : info.outputPath(`${locale}-${name}-${where}.png`), animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
      }
    };
    await check('2-company');
    await next(page, t);
    await next(page, t);
    await expectStep(page, t, 'Organisation');
    await check('4-organisation');
  }
});
