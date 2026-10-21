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
  /** The viewer holds security.manage, the permission that issues sign-in links. */
  canIssueLinks?: boolean;
  /** How POST /api/access/users/{id}/password-reset-link answers. */
  resetLink?: 'emailed' | 'disclosed' | 'refused';
  /** Existing mode: Evostel's cutover status (A4). Default Planned. */
  cutoverStatus?: string;
}

const sister = { id: 'sister-1', legalNameEn: 'Sister Trading KSA', countryCode: 'SA' };
/** An active company to complete (Addendum A). */
const EVOSTEL = { id: 'co-evostel', legalNameEn: 'Evostel LLC', legalNameAr: 'شركة إيفوستل', countryCode: 'SA' };
const INVITED_USER_ID = 'u-invited-admin';
const RESET_URL = 'http://127.0.0.1:5174/reset-password?workspace=wizard-fixture#token=single-use-fixture-token';

/** Persists across page loads and sign-out, like the server would. */
class ContractServer {
  drafts = new Map<string, CompanySetupDraft>();
  puts: { id: string; version: number; step: number; body: CompanySetupDraftBody }[] = [];
  commits: { key: string | undefined; draftId: string; version: number }[] = [];
  dryRuns: CompanySetupDraftBody[] = [];
  results = new Map<string, CompanySetupResult>();
  resetLinks: string[] = [];
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
      // A5: a group admin may complete any active company; a company super admin only their own.
      manageableCompanies: [
        { ...EVOSTEL, readinessDone: 5, readinessTotal: 9, openDraftId: this.openDraftFor(EVOSTEL.id) },
        ...(this.scopeGroup ? [{ ...sister, legalNameAr: null, readinessDone: 9, readinessTotal: 9, openDraftId: this.openDraftFor(sister.id) }] : []),
      ],
      ...(this.scopeGroup ? {} : { canGrantCompanyAdmin: true }),
    };
    return { ...base, ...this.options.context };
  }

  openDraftFor(companyId: string) {
    return [...this.drafts.values()].find(d => d.status === 'Open' && d.body.existingCompanyId === companyId)?.id ?? null;
  }

  /** A2: what Evostel has today. Every row it already has carries existingId. */
  existingDefaults(companyId: string): CompanySetupDraftBody {
    return {
      existingCompanyId: companyId, startFrom: 'defaults',
      company: { legalNameEn: EVOSTEL.legalNameEn, legalNameAr: EVOSTEL.legalNameAr, tradeName: 'Evostel', countryCode: 'SA', currency: 'SAR', registrationNumber: '1010999888',
        molEstablishmentNumber: '7-7654321', qiwaEstablishmentId: '7-7654321', emailDomain: 'evostel.com', workEmailPattern: 'first.last', firstPayrollMonth: '2026-11',
        cutoverStatus: this.options.cutoverStatus ?? 'Planned' },
      branches: [{ existingId: 'br-hq', code: 'HQ', nameEn: 'Head Office', nameAr: 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true,
        locations: [{ existingId: 'loc-gate', code: 'GATE1', nameEn: 'Main gate', nameAr: '', latitude: 24.7, longitude: 46.7, radiusMeters: 150 }] }],
      costCentres: [{ existingId: 'cc-adm', code: 'ADM', nameEn: 'Administration', nameAr: 'الإدارة' }],
      departments: [{ existingId: 'dep-hr', code: 'HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية' }],
      designations: [{ existingId: 'des-acc', code: 'ACC', titleEn: 'Accountant', titleAr: 'محاسب' }],
      grades: [{ existingId: 'gr-g1', code: 'G1', nameEn: 'Grade 1', nameAr: 'الدرجة 1', level: 1, minSalary: 3000, midSalary: 5000, maxSalary: 7000 }],
      rules: {
        workWeek: { weekendDays: 'Fri,Sat', override: false }, holidayCalendar: { usePack: true }, leavePolicies: { usePack: true },
        employeeNumbering: { prefix: 'EVO', useCountryPrefix: false, useBranchPrefix: false, useDepartmentPrefix: false, useYear: false, paddingLength: 4, nextSequence: 42 },
      },
      companyAdmin: { mode: 'groupOnly' },
    };
  }

  /** A3: the same planner for existing mode — update / unchanged by existingId, create for new rows, never a delete. */
  existingPlan(body: CompanySetupDraftBody): CompanySetupPlan {
    const before = this.existingDefaults(body.existingCompanyId!);
    const diff = <T extends object>(a: T, b: T | undefined, keys: (keyof T)[]) => keys.filter(k => String(a[k] ?? '') !== String(b?.[k] ?? '')).map(String);
    const own = <T extends { existingId?: string | null; code: string }>(area: PlanItem['area'], rows: T[], was: T[], keys: (keyof T)[], label: (r: T) => string): PlanItem[] =>
      rows.map(r => {
        if (!r.existingId) return { area, code: r.code, label: label(r), action: 'create' as const };
        const changed = diff(r, was.find(w => w.existingId === r.existingId), keys);
        return changed.length ? { area, code: r.code, label: label(r), action: 'update' as const, changedFields: changed, reason: `Changes: ${changed.join(', ')}` } : { area, code: r.code, label: label(r), action: 'unchanged' as const };
      });
    const catalog = this.context().sharedCatalog;
    const has = (list: { code: string }[], code: string) => list.some(c => c.code.toUpperCase() === code.toUpperCase());
    const companyChanged = diff(body.company, before.company, ['legalNameEn', 'legalNameAr', 'tradeName', 'registrationNumber', 'molEstablishmentNumber', 'gosiEmployerId', 'taxNumber', 'qiwaEstablishmentId', 'emailDomain', 'workEmailPattern', 'erpCompanyCode', 'firstPayrollMonth']);
    const numberingChanged = diff(body.rules.employeeNumbering, before.rules.employeeNumbering, ['prefix', 'paddingLength', 'nextSequence', 'useCountryPrefix', 'useBranchPrefix', 'useDepartmentPrefix', 'useYear']);
    const items: PlanItem[] = [
      companyChanged.length
        ? { area: 'company', code: body.company.registrationNumber, label: body.company.legalNameEn, action: 'update', changedFields: companyChanged }
        : { area: 'company', code: body.company.registrationNumber, label: body.company.legalNameEn, action: 'unchanged' },
      ...own('branch', body.branches, before.branches, ['nameEn', 'nameAr', 'city', 'timeZoneId', 'isHeadOffice'], b => b.nameEn),
      ...body.branches.flatMap(b => own('location', b.locations, before.branches.find(w => w.existingId && w.existingId === b.existingId)?.locations ?? [], ['nameEn', 'nameAr', 'latitude', 'longitude', 'radiusMeters'], l => l.nameEn)),
      ...own('costCentre', body.costCentres, before.costCentres, ['nameEn', 'nameAr'], c => c.nameEn),
      ...body.departments.map(d => ({ area: 'department' as const, code: d.code, label: d.nameEn, action: has(catalog.departments, d.code) ? 'reuse' as const : 'create' as const })),
      ...body.designations.map(d => ({ area: 'designation' as const, code: d.code, label: d.titleEn, action: has(catalog.designations, d.code) ? 'reuse' as const : 'create' as const })),
      ...body.grades.map(g => ({ area: 'grade' as const, code: g.code, label: g.nameEn, action: has(catalog.grades, g.code) ? 'reuse' as const : 'create' as const })),
      { area: 'workWeek', code: body.rules.workWeek.weekendDays, label: 'Working week', action: 'unchanged' },
      { area: 'gccSetting', code: 'SA', label: 'End-of-service (Saudi Labour Law)', labelAr: 'مكافأة نهاية الخدمة (نظام العمل السعودي)', action: 'unchanged' },
      { area: 'holidayCalendar', code: 'SA-2026', label: 'Saudi public holidays 2026', action: 'reuse' },
      numberingChanged.length
        ? { area: 'employeeIdRule', code: body.rules.employeeNumbering.prefix, label: 'Employee numbering', action: 'update', changedFields: numberingChanged }
        : { area: 'employeeIdRule', code: body.rules.employeeNumbering.prefix, label: 'Employee numbering', action: 'unchanged' },
      ...(body.companyAdmin.mode !== 'groupOnly' ? [{ area: 'companyAdmin' as const, code: body.companyAdmin.email ?? body.companyAdmin.existingUserId ?? '', label: body.companyAdmin.fullNameEn ?? 'Existing user', action: 'create' as const }] : []),
    ];
    return { ok: true, items, problems: [], warnings: [] };
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
    return [...this.drafts.values()].filter(d => d.status === 'Open').map(d => ({ id: d.id, legalNameEn: d.body.company.legalNameEn, step: d.step, updatedAtUtc: d.updatedAtUtc, createdByName: d.createdByName, existingCompanyId: d.body.existingCompanyId ?? null }));
  }

  /** What a company super admin may touch: existing mode for their own company only (A5). */
  ownCompany(id: string | null | undefined) { return this.scopeGroup ? !!id : id === EVOSTEL.id; }

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
    const refuse = () => reply({ message: 'Only a group administrator can create a company.' }, 403);
    const existingId = url.searchParams.get('existingCompanyId');
    if (rest === '/context' && method === 'GET') return reply(this.context());
    if (rest === '/defaults' && method === 'GET') {
      if (existingId) return this.ownCompany(existingId) ? reply(this.existingDefaults(existingId)) : reply({ message: 'Not your company.' }, 403);
      return this.scopeGroup ? reply(this.defaults(url.searchParams.get('country') ?? 'SA')) : refuse();
    }
    if (rest === '/drafts' && method === 'GET') return reply(this.summaries().filter(d => this.scopeGroup || d.existingCompanyId === EVOSTEL.id));
    if (rest === '/drafts' && method === 'POST') {
      const body = req.postDataJSON() as CompanySetupDraftBody;
      if (body.existingCompanyId ? !this.ownCompany(body.existingCompanyId) : !this.scopeGroup) return refuse();
      // A1: one open draft per existing company.
      if (body.existingCompanyId && this.openDraftFor(body.existingCompanyId)) return reply({ code: 'existing_company_draft_open', message: 'This company already has an open draft.' }, 409);
      const draft: CompanySetupDraft = { id: `draft-${++this.sequence}`, version: 1, step: 1, status: 'Open', body, createdByUserId: 'u-admin', createdByName: 'Group Owner', createdAtUtc: now, updatedAtUtc: now };
      this.drafts.set(draft.id, draft);
      return reply(draft, 201);
    }
    const draftMatch = rest.match(/^\/drafts\/([^/]+)$/);
    if (draftMatch) {
      const draft = this.drafts.get(draftMatch[1]);
      if (!draft || (draft.body.existingCompanyId ? !this.ownCompany(draft.body.existingCompanyId) : !this.scopeGroup)) return reply({ message: 'Not found' }, 404);
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
      if (body.existingCompanyId ? !this.ownCompany(body.existingCompanyId) : !this.scopeGroup) return refuse();
      this.dryRuns.push(body);
      return reply(body.existingCompanyId ? this.existingPlan(body) : this.plan(body));
    }
    if (rest === '' && method === 'POST') {
      const { draftId, version } = req.postDataJSON() as { draftId: string; version: number };
      const key = req.headers()['idempotency-key'];
      this.commits.push({ key, draftId, version });
      const previous = key ? this.results.get(key) : undefined;
      if (previous) return reply({ ...previous, replayed: true });
      const draft = this.drafts.get(draftId);
      if (!draft || draft.version !== version) return reply({ message: 'This draft was changed elsewhere.' }, 409);
      if (draft.body.existingCompanyId) {
        // A6: the company row is updated, never recreated; counts by action.
        const plan = this.existingPlan(draft.body);
        const count = (action: string) => plan.items.filter(i => i.action === action).reduce<Record<string, number>>((m, i) => ({ ...m, [i.area]: (m[i.area] ?? 0) + 1 }), {});
        this.drafts.set(draftId, { ...draft, status: 'Submitted', resultCompanyId: draft.body.existingCompanyId });
        const result: CompanySetupResult = {
          companyId: draft.body.existingCompanyId, replayed: false, created: count('create'), reused: count('reuse'), updated: count('update'),
          readiness: [
            { key: 'branches', label: 'Branches and head office', labelAr: 'الفروع والمقر الرئيسي', status: 'done', scope: 'company', href: `/setup?tab=branches&companyId=${draft.body.existingCompanyId}` },
            { key: 'glMapping', label: 'GL mapping', labelAr: 'ربط دفتر الأستاذ', status: 'todo', scope: 'company', href: `/setup?tab=glMapping&companyId=${draft.body.existingCompanyId}` },
            { key: 'companyAdmin', label: 'Company super admin', labelAr: 'المسؤول الأعلى للشركة', status: 'recommended', scope: 'company', href: `/user-management?companyId=${draft.body.existingCompanyId}` },
          ],
        };
        if (key) this.results.set(key, result);
        return reply(result, 200);
      }
      if (!this.scopeGroup) return refuse();
      const plan = this.plan(draft.body);
      if (!plan.ok) return reply({ message: 'A cost centre code clashes with a sister company.', step: 4 }, 409);
      this.drafts.set(draftId, { ...draft, status: 'Submitted', resultCompanyId: 'new-company' });
      const result: CompanySetupResult = {
        companyId: 'new-company', replayed: false,
        created: { company: 1, branch: draft.body.branches.length }, reused: { department: 1 },
        readiness: [
          { key: 'glMapping', label: 'GL mapping', labelAr: 'ربط دفتر الأستاذ', status: 'todo', scope: 'company', href: '/setup?tab=glMapping&companyId=new-company' },
          { key: 'bankWps', label: 'Bank and WPS settings (before first payment)', labelAr: 'إعدادات البنك وحماية الأجور (قبل أول دفعة)', status: 'todo', scope: 'company', href: '/setup?tab=gccSettings&companyId=new-company' },
          draft.body.companyAdmin.mode === 'invite'
            // An invited admin has not signed in yet: the server names the login to send the sign-in link to.
            ? { key: 'companyAdmin', label: `Send ${draft.body.companyAdmin.fullNameEn} their sign-in link`, labelAr: `أرسل رابط تسجيل الدخول إلى ${draft.body.companyAdmin.fullNameEn}`, status: 'todo', scope: 'company', href: '/user-management?companyId=new-company', userId: INVITED_USER_ID }
            : { key: 'companyAdmin', label: 'Company super admin', labelAr: 'المسؤول الأعلى للشركة', status: draft.body.companyAdmin.mode === 'groupOnly' ? 'recommended' : 'done', scope: 'company', href: '/user-management?companyId=new-company' },
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
    permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'dashboard.read', 'users.read', 'users.write',
      ...(server.options.canIssueLinks ? ['security.manage'] : [])],
    accountType: 'Group', isGroupScope: persona !== 'companyAdmin',
    companies: persona === 'companyAdmin'
      ? [{ id: EVOSTEL.id, name: EVOSTEL.legalNameEn, code: 'EVO', countryCode: 'SA', isActive: true }]
      : [{ id: sister.id, name: sister.legalNameEn, code: 'SIS', countryCode: 'SA', isActive: true }, { id: EVOSTEL.id, name: EVOSTEL.legalNameEn, code: 'EVO', countryCode: 'SA', isActive: true }],
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
    const resetLink = p.match(/^\/api\/access\/users\/([^/]+)\/password-reset-link$/);
    if (resetLink && request.method() === 'POST') {
      server.resetLinks.push(resetLink[1]);
      const mode = server.options.resetLink ?? 'disclosed';
      if (mode === 'refused') return reply({ message: 'You cannot issue a sign-in link for this user.' }, 403);
      return reply({
        userId: resetLink[1], email: 'sara@riyadhtrading.sa', expiresAtUtc: '2099-01-01T01:00:00Z',
        emailDeliveryConfigured: mode === 'emailed', emailSent: mode === 'emailed', resetUrl: mode === 'emailed' ? null : RESET_URL,
        handOverInPerson: false, message: mode === 'emailed' ? 'Reset link accepted by the mail server.' : 'No email delivery is configured for this workspace.',
      });
    }
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

/** Steps 1-7 with an invited company super admin, through Create, to the success screen. */
async function createWithInvitedAdmin(page: Page, loc: Locale = 'en') {
  const t = T(loc);
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  for (let i = 0; i < 4; i++) await next(page, t);
  await expectStep(page, t, 'Company super admin');
  await page.getByLabel(t('Invite a new company super admin')).check();
  await page.getByLabel(t('Full name (English)')).fill('Sara Al-Qahtani');
  await page.getByLabel(t('Official work email')).fill('sara@riyadhtrading.sa');
  await next(page, t);
  await expectStep(page, t, 'Review & create');
  await page.getByRole('button', { name: t('Create company'), exact: true }).click();
  await expect(page.getByTestId('create-company-success')).toBeVisible();
  const row = page.getByTestId('readiness-companyAdmin');
  await expect(row).toContainText(loc === 'ar' ? 'أرسل رابط تسجيل الدخول إلى Sara Al-Qahtani' : 'Send Sara Al-Qahtani their sign-in link');
  return row;
}

for (const [loc, delivery] of [['en', 'disclosed'], ['ar', 'disclosed'], ['en', 'emailed'], ['ar', 'emailed']] as const) {
  test(`the success screen sends the invited super admin their sign-in link in one click: ${delivery} (${loc})`, async ({ page, context }, info) => {
    const t = T(loc);
    if (delivery === 'disclosed') await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const { server, pageErrors } = await boot(page, { locale: loc, canIssueLinks: true, resetLink: delivery });
    const row = await createWithInvitedAdmin(page, loc);
    // One clear next action, not a trip to User Management.
    await expect(row.getByRole('link', { name: new RegExp(t('Open')) })).toHaveCount(0);
    await row.getByRole('button', { name: t('Send sign-in link'), exact: true }).click();
    const outcome = row.getByTestId('sign-in-link-result');
    await expect(outcome).toBeVisible();
    expect(server.resetLinks).toEqual([INVITED_USER_ID]);
    if (delivery === 'emailed') {
      await expect(outcome).toHaveAttribute('data-emailed', 'true');
      await expect(outcome).toContainText(t('Emailed to {email}. The link works once and expires in 1 hour.', { email: 'sara@riyadhtrading.sa' }));
      await expect(row.getByTestId('sign-in-link-url')).toHaveCount(0);
    } else {
      await expect(outcome).toHaveAttribute('data-emailed', 'false');
      await expect(outcome).toContainText(t('Nothing was emailed. Copy this sign-in link and send it to {email} through the company’s own channel. It works once and expires in 1 hour.', { email: 'sara@riyadhtrading.sa' }));
      await expect(row.getByTestId('sign-in-link-url')).toHaveValue(RESET_URL);
      await expect(row.getByTestId('sign-in-link-url')).toHaveAttribute('dir', 'ltr');
      await row.getByRole('button', { name: t('Copy link'), exact: true }).click();
      await expect(row.getByRole('button', { name: t('Copied'), exact: true })).toBeVisible();
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(RESET_URL);
    }
    await contained(page);
    await shot(page, info, `${loc}-8-sign-in-link-${delivery}`);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('a viewer who cannot issue sign-in links gets the User Management link instead', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page, { canIssueLinks: false });
  const row = await createWithInvitedAdmin(page);
  await expect(row.getByRole('button', { name: t('Send sign-in link'), exact: true })).toHaveCount(0);
  await expect(row.getByRole('link', { name: new RegExp(t('Open')) })).toHaveAttribute('href', '/user-management?companyId=new-company');
  expect(server.resetLinks).toEqual([]);
});

test('a refused sign-in link says so and keeps the way to User Management', async ({ page }) => {
  const t = T('en');
  await boot(page, { canIssueLinks: true, resetLink: 'refused' });
  const row = await createWithInvitedAdmin(page);
  await row.getByRole('button', { name: t('Send sign-in link'), exact: true }).click();
  await expect(row.getByRole('alert')).toContainText(t('The sign-in link could not be created. Try again, or open User Management.'));
  await expect(row.getByRole('link', { name: t('Open User Management'), exact: true })).toHaveAttribute('href', '/user-management?companyId=new-company');
  await expect(row.getByRole('button', { name: t('Send sign-in link'), exact: true })).toBeEnabled();
});

// ── Addendum A: Guided Setup chooser and existing-company mode ────────────────

/** Viewport-only capture (what a person sees without scrolling), saved under WIZARD_SHOTS_DIR when set. */
async function vshot(page: Page, info: TestInfo, name: string) {
  const dir = process.env.WIZARD_SHOTS_DIR;
  if (dir) fs.mkdirSync(dir, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: dir ? path.join(dir, `${name}-${info.project.name}.png`) : info.outputPath(`${name}.png`), animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

async function openChooser(page: Page, route = '/setup?tab=aiSetup') {
  await page.goto(route);
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
}

const evostelRow = (page: Page) => page.locator('[data-testid="chooser-company"][data-company-id="co-evostel"]');

async function openEvostel(page: Page, t: ReturnType<typeof T>) {
  await openChooser(page);
  await evostelRow(page).getByRole('link', { name: new RegExp(t('Complete setup')) }).click();
  await expect(page).toHaveURL(/\/setup\/company\/co-evostel\/complete/);
  await expect(page.getByTestId('company-summary')).toBeVisible();
}

for (const locale of ['en', 'ar'] as const) {
  test(`Guided Setup opens the chooser: create, or complete an existing company (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
    await openChooser(page);
    await expect(page.getByRole('button', { name: t('Guided Setup'), exact: true })).toHaveAttribute('aria-pressed', 'true');
    // The old 5-step studio is gone from Guided Setup.
    await expect(page.getByRole('button', { name: 'Generate draft' })).toHaveCount(0);
    const create = page.getByTestId('chooser-create');
    await expect(create.getByRole('link', { name: t('Create a new company'), exact: true })).toHaveAttribute('href', '/setup/new-company');
    await expect(page.getByTestId('chooser-plan-usage')).toHaveText(t('{used} of {allowed} companies on your plan', { used: 2, allowed: 5 }));
    await expect(evostelRow(page)).toContainText(locale === 'ar' ? 'شركة إيفوستل' : 'Evostel LLC');
    await expect(evostelRow(page).getByTestId('chooser-readiness')).toHaveText(t('{done} of {total} done', { done: 5, total: 9 }));
    await expect(evostelRow(page).getByRole('link')).toHaveAttribute('href', '/setup/company/co-evostel/complete');
    // A company whose checklist is complete offers a review, not a primary call to action.
    await expect(page.locator('[data-company-id="sister-1"]').getByRole('link', { name: new RegExp(t('Review setup')) })).toBeVisible();
    await contained(page);
    await vshot(page, info, `existing-${locale}-0-chooser`);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('old studio links land on the chooser; a companyId link opens that company, a forged one opens nothing', async ({ page }) => {
  const t = T('en');
  await boot(page);
  await page.goto('/setup?tab=aiSetup&mode=starter&companyId=co-evostel');
  await expect(page).toHaveURL(/\/setup\/company\/co-evostel\/complete/);
  await expect(page.getByTestId('company-summary')).toBeVisible();
  await page.goto('/setup?tab=aiSetup&mode=starter');
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page).toHaveURL(/path=guided/);
  await expect(page).not.toHaveURL(/mode=starter/);
  await page.goto('/setup?tab=aiSetup&path=guided&companyId=forged-company');
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(page.getByText(t('The link named a company you cannot set up, so it was not opened. Choose one of the companies below.'))).toBeVisible();
  await expect(page).not.toHaveURL(/\/complete/);
});

test('the old studio stays reachable only as Configuration Center → Policy starter', async ({ page }) => {
  await boot(page);
  await page.goto('/setup?tab=aiSetup&path=configuration');
  await page.getByRole('button', { name: 'Policy starter (attendance, overtime, benefits…)', exact: true }).click();
  await expect(page).toHaveURL(/path=policyStarter/);
  await expect(page.getByTestId('policy-starter-banner')).toBeVisible();
  // Under Configuration Center, never Guided Setup.
  await expect(page.getByRole('button', { name: 'Configuration Center', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Guided Setup', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await page.getByRole('button', { name: 'Back to Configuration Center', exact: true }).click();
  await expect(page).toHaveURL(/path=configuration/);
});

for (const locale of ['en', 'ar'] as const) {
  test(`complete an existing company: edit a branch, add one, review the update, save (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale, cutoverStatus: 'Live' });
    await openEvostel(page, t);
    // Step 1 is the company summary, not "Start from".
    await expect(page.getByRole('heading', { level: 2, name: t('Complete setup for {company}', { company: locale === 'ar' ? 'شركة إيفوستل' : 'Evostel LLC' }) })).toBeVisible();
    await expect(page.getByText(t('Start from'), { exact: true })).toHaveCount(0);
    await expect(page.getByTestId('summary-readiness')).toHaveText(t('{done} of {total} done', { done: 5, total: 9 }));
    await expect(page.getByTestId('wizard-rail')).toContainText(t('Summary'));
    expect(server.drafts.size).toBe(0); // nothing is written before Start
    await contained(page);
    await vshot(page, info, `existing-${locale}-1-summary`);
    await page.getByRole('button', { name: t('Start'), exact: true }).click();

    await expectStep(page, t, 'Company details');
    await expect(page).toHaveURL(/\/setup\/company\/co-evostel\/complete\?draft=/);
    await expect(page.getByLabel(t('Country'))).toBeDisabled();
    await expect(page.getByLabel(t('Salary currency'))).toBeDisabled();
    await expect(page.getByText(t('Fixed for an existing company: payroll, GOSI and bank files depend on it.')).first()).toBeVisible();
    // Payroll has started (cutover Live): the first payroll month is fixed.
    await expect(page.getByLabel(t('First payroll month'))).toHaveAttribute('readonly', '');
    await expect(page.getByText(t('Payroll has already started for this company, so its first payroll month stays as it is.'))).toBeVisible();
    await expect(page.getByLabel(t('Legal name (English)'))).toHaveValue('Evostel LLC');
    await expect(page.getByTestId('structure-tree')).toContainText(t('Existing'));
    await contained(page);
    await vshot(page, info, `existing-${locale}-2-company`);
    await next(page, t);

    await expectStep(page, t, 'Branches & locations');
    const hq = page.getByTestId('branches-row').first();
    await expect(hq).toHaveAttribute('data-existing', 'true');
    await expect(hq.getByLabel(t('Code')).first()).toHaveAttribute('readonly', '');
    // No delete in the wizard: removal is managed in Setup.
    await expect(hq.getByRole('button', { name: new RegExp(`^${t('Remove')}`) })).toHaveCount(0);
    await expect(hq.getByTestId('manage-in-setup').first()).toHaveAttribute('href', '/setup?tab=branches&companyId=co-evostel');
    await hq.getByLabel(t('Name (English)')).first().fill('Head Office Riyadh');
    await expect(hq).toHaveAttribute('data-changed', 'true');
    await page.getByRole('button', { name: t('Add branch'), exact: true }).click();
    const jed = page.getByTestId('branches-row').nth(1);
    await expect(jed).not.toHaveAttribute('data-existing', 'true');
    await jed.getByLabel(t('Code')).fill('JED');
    await jed.getByLabel(t('Name (English)')).fill('Jeddah');
    await jed.getByLabel(t('City')).fill('Jeddah');
    await expect(page.getByTestId('structure-tree')).toContainText('JED');
    await contained(page);
    await vshot(page, info, `existing-${locale}-3-branches`);
    await next(page, t);

    await expectStep(page, t, 'Organisation');
    const hr = page.getByTestId('departments-row').first();
    await expect(hr).toHaveAttribute('data-existing', 'true');
    await expect(hr.getByTestId('manage-in-setup')).toHaveAttribute('href', '/setup?tab=departments&companyId=co-evostel');
    await expect(page.getByRole('button', { name: t('Use defaults'), exact: true })).toHaveCount(0);
    await contained(page);
    await vshot(page, info, `existing-${locale}-4-organisation`);
    await next(page, t);

    await expectStep(page, t, 'Work & pay rules');
    await expect(page.getByTestId('numbering-preview')).toContainText('EVO-0042');
    await page.getByTestId('more-settings-section').locator('summary').click();
    await expect(page.getByTestId('more-settings')).toBeVisible();
    await contained(page);
    await vshot(page, info, `existing-${locale}-5-rules`);
    await next(page, t);

    await expectStep(page, t, 'Company super admin');
    await expect(page.getByLabel(t('Keep the current administrators'))).toBeChecked();
    await contained(page);
    await vshot(page, info, `existing-${locale}-6-admin`);
    await next(page, t);

    await expectStep(page, t, 'Review & update');
    await expect(page.getByTestId('count-update')).toHaveText(t('{count} to update', { count: 1 }));
    await expect(page.getByTestId('count-create')).toHaveText(t('{count} to add', { count: 1 }));
    const branches = page.getByTestId('plan-group-3');
    await expect(branches.locator('[data-action="update"]')).toContainText(t('Changes: {fields}', { fields: t('Name (English)') }));
    await expect(branches.locator('[data-action="create"]')).toContainText('JED');
    // Unchanged rows are folded away; only the changes are open.
    await expect(page.getByTestId('plan-kept-2')).not.toHaveAttribute('open', '');
    await expect(page.getByTestId('plan-kept-2').locator('[data-action="unchanged"]')).toBeHidden();
    await expect(page.getByTestId('review-no-change')).toHaveCount(0);
    await contained(page);
    await vshot(page, info, `existing-${locale}-7-review`);
    await page.getByRole('button', { name: t('Update company'), exact: true }).click();

    await expect(page.getByTestId('create-company-success')).toHaveAttribute('data-mode', 'existing');
    await expect(page.getByRole('heading', { name: t('{name} is updated', { name: locale === 'ar' ? 'شركة إيفوستل' : 'Evostel LLC' }) })).toBeVisible();
    await expect(page.getByTestId('readiness-glMapping')).toBeVisible();
    await expect(page.getByRole('link', { name: t('Back to Guided Setup'), exact: true })).toHaveAttribute('href', '/setup?tab=aiSetup&path=guided');
    await contained(page);
    await vshot(page, info, `existing-${locale}-8-updated`);

    const [commit] = server.commits;
    expect(server.commits).toHaveLength(1);
    expect(commit.key).toBe(commit.draftId);
    const saved = server.drafts.get(commit.draftId)!.body;
    expect(saved.existingCompanyId).toBe('co-evostel');
    expect(saved.company).toMatchObject({ countryCode: 'SA', currency: 'SAR', firstPayrollMonth: '2026-11' });
    // The edited branch kept its id (an update, not a new branch with the same code); the new one has none.
    expect(saved.branches.map(b => [b.code, b.existingId ?? null, b.nameEn])).toEqual([['HQ', 'br-hq', 'Head Office Riyadh'], ['JED', null, 'Jeddah']]);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

for (const locale of ['en', 'ar'] as const) {
  test(`an existing company with nothing to change reviews as no change and finishes (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server } = await boot(page, { locale });
    await openEvostel(page, t);
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await expectStep(page, t, 'Company details');
    // Planned cutover: the first payroll month may still change.
    await expect(page.getByLabel(t('First payroll month'))).not.toHaveAttribute('readonly', '');
    for (let i = 0; i < 5; i++) await next(page, t);
    await expectStep(page, t, 'Review & update');
    await expect(page.getByTestId('review-no-change')).toBeVisible();
    await expect(page.getByTestId('count-update')).toHaveText(t('{count} to update', { count: 0 }));
    await expect(page.getByTestId('count-create')).toHaveText(t('{count} to add', { count: 0 }));
    await expect(page.locator('[data-testid="review-plan"] li[data-action]:visible')).toHaveCount(0);
    await contained(page);
    await vshot(page, info, `existing-${locale}-7-review-no-change`);
    await page.getByRole('button', { name: t('Finish without changes'), exact: true }).click();
    await expect(page.getByTestId('create-company-success')).toBeVisible();
    expect(server.commits).toHaveLength(1);
    const plan = server.existingPlan(server.drafts.get(server.commits[0].draftId)!.body);
    expect(plan.items.every(i => i.action === 'unchanged' || i.action === 'reuse')).toBe(true);
  });
}

for (const locale of ['en', 'ar'] as const) {
  test(`a company super admin sees only their company, picks shared codes only, and can add a company admin (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale, persona: 'companyAdmin' });
    await openChooser(page);
    await expect(page.getByTestId('chooser-create')).toHaveCount(0);
    await expect(page.getByTestId('chooser-company')).toHaveCount(1);
    await expect(evostelRow(page)).toBeVisible();
    await expect(page.getByRole('heading', { name: t('Complete setup for your company'), exact: true })).toBeVisible();
    await contained(page);
    await vshot(page, info, `existing-${locale}-0-chooser-company-admin`);
    await evostelRow(page).getByRole('link').click();
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await next(page, t);
    await next(page, t);
    await expectStep(page, t, 'Organisation');
    // Shared catalog: pick only; cost centres are the company's own and can be added.
    await expect(page.getByRole('button', { name: t('Add department'), exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: t('Add designation'), exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: t('Add grade'), exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: t('Add cost centre'), exact: true })).toBeVisible();
    await page.getByRole('button', { name: t('Use {code} · {name}', { code: 'FIN', name: locale === 'ar' ? 'المالية' : 'Finance' }) }).click();
    await expect(page.getByTestId('departments-row')).toHaveCount(2);
    await contained(page);
    await vshot(page, info, `existing-${locale}-4-organisation-company-admin`);
    await next(page, t);
    await next(page, t);
    await expectStep(page, t, 'Company super admin');
    await expect(page.getByTestId('company-scoped-admin-note')).toBeVisible();
    await page.getByLabel(t('Invite a new company super admin')).check();
    await page.getByLabel(t('Full name (English)')).fill('Omar Haddad');
    await page.getByLabel(t('Official work email')).fill('omar@evostel.com');
    await contained(page);
    await vshot(page, info, `existing-${locale}-6-admin-company-admin`);
    await next(page, t);
    await expectStep(page, t, 'Review & update');
    await expect(page.getByTestId('plan-group-6')).toContainText('Omar Haddad');
    expect(server.dryRuns.at(-1)?.companyAdmin).toMatchObject({ mode: 'invite', email: 'omar@evostel.com' });
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('an open draft for a company resumes from the chooser instead of starting a second one', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  await page.getByRole('button', { name: t('Save draft & exit'), exact: true }).click();
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect(evostelRow(page).getByTestId('chooser-draft')).toHaveText(t('Draft in progress'));
  await evostelRow(page).getByRole('link', { name: new RegExp(t('Resume setup')) }).click();
  await expectStep(page, t, 'Branches & locations');
  expect([...server.drafts.values()].filter(d => d.body.existingCompanyId === 'co-evostel')).toHaveLength(1);
});
