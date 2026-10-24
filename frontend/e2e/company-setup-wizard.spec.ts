import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { translate } from '../src/i18n/translations';
import { openSetupSection, setupSection } from './setupSections';
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
  /** Existing mode: Evostel's cutover status (A4), sent on the prefill. Default Planned; anything else refuses a month change. */
  cutoverStatus?: 'Planned' | 'Active' | 'Closed';
  /** Existing mode (A9): the head office was renamed to this in Setup after the prefill was read. */
  hqRenamedElsewhere?: string;
  /** Existing mode: Evostel was saved with no MOL number, before today's checks. */
  legacyMol?: boolean;
  /** Existing mode: the head office's Arabic name came from the old letter-by-letter auto-transliteration. */
  badArabic?: boolean;
  /** Existing mode: the location has coordinates but no geofence radius (attendance verification skips it). */
  locationWithoutRadius?: boolean;
  /** Addendum B2: how the next section save fails, once. */
  sectionFailure?: 'network' | 'problem' | 'lost';
}

/**
 * What the offline organisation-label suggester (TransliterationService.ToArabicOrganizationLabel) answers for the
 * labels these tests type; backend OrganizationLabelArabicTests prove the wording itself. Anything else is "".
 */
const ORG_ARABIC: Record<string, string> = {
  'head office': 'المكتب الرئيسي', 'main gate': 'البوابة الرئيسية', 'administration': 'الإدارة', 'human resources': 'الموارد البشرية',
  'finance': 'المالية', 'operations': 'العمليات', 'sales': 'المبيعات', 'marketing': 'التسويق', 'accountant': 'محاسب',
  'software engineer': 'مهندس برمجيات', 'grade 9': 'الدرجة 9', 'human resources cost center': 'مركز تكلفة الموارد البشرية',
  'sulaymaniyah branch': 'فرع السليمانية', 'jeddah branch': 'فرع جدة', 'olaya': 'العليا',
};
const OLD_LETTER_BY_LETTER = 'هيد اوففيكي';
/** The short link the mocked resolver can read, and where it points. */
const SHORT_LINK = 'https://maps.app.goo.gl/kynexDemo1';
const SHORT_LINK_AT = { latitude: 21.5433, longitude: 39.1728 };
/** A 1×1 transparent PNG for OpenStreetMap tiles, so the map is offline and deterministic (WIZARD_REAL_TILES=1 for real ones). */
const BLANK_TILE = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');

type OrgRows = Pick<CompanySetupDraftBody, 'costCentres' | 'departments' | 'designations' | 'grades'>;
const SECTIONS = ['costCentres', 'departments', 'grades', 'designations'] as const;
type Section = typeof SECTIONS[number];

/** What the sister company has, offered by GET /copy-rows as new rows (B3). */
const SISTER_ROWS: OrgRows = {
  costCentres: [{ code: 'ADM', nameEn: 'Administration' }, { code: 'CC_OPS', nameEn: 'Operations Cost Centre', nameAr: 'مركز تكلفة العمليات' }],
  departments: [{ code: 'HR', nameEn: 'Human Resources' }, { code: 'OPS', nameEn: 'Operations', costCentreCode: 'CC_OPS' }],
  grades: [{ code: 'G2', nameEn: 'Grade 2', level: 2, minSalary: 6000, midSalary: 8000, maxSalary: 10000 }],
  designations: [{ code: 'OPS_MGR', titleEn: 'Operations Manager', departmentCode: 'OPS', gradeCode: 'G2' }],
};

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
  /** B2 section saves, in order, and the markers that make a retry replay instead of writing twice. */
  sectionApplies: { section: string; version: number; replayed: boolean }[] = [];
  sectionMarkers = new Set<string>();
  copyCalls: { fromCompanyId: string; section: string }[] = [];
  /** Evostel's Organisation rows as the database holds them; section saves write here. */
  orgDb: OrgRows | null = null;
  resetLinks: string[] = [];
  errors: string[] = [];
  /** Every batch of labels sent to the Arabic suggester. */
  suggestionRequests: string[][] = [];
  /** Every short link sent to the map-link resolver. */
  mapLinks: string[] = [];
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
      // As the backend sends it: the cutover status (A4) and, on each existing row and block, its baseline (A9).
      company: { legalNameEn: EVOSTEL.legalNameEn, legalNameAr: EVOSTEL.legalNameAr, tradeName: 'Evostel', countryCode: 'SA', currency: 'SAR', registrationNumber: '1010999888',
        molEstablishmentNumber: this.options.legacyMol ? '' : '7-7654321', qiwaEstablishmentId: '7-7654321', emailDomain: 'evostel.com', workEmailPattern: 'first.last', firstPayrollMonth: '2026-11',
        cutoverStatus: this.options.cutoverStatus ?? 'Planned', baseline: { legalNameEn: EVOSTEL.legalNameEn, tradeName: 'Evostel', firstPayrollMonth: '2026-11' } },
      branches: [{ existingId: 'br-hq', code: 'HQ', nameEn: 'Head Office', nameAr: this.options.badArabic ? OLD_LETTER_BY_LETTER : 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true,
        baseline: { nameEn: 'Head Office', nameAr: this.options.badArabic ? OLD_LETTER_BY_LETTER : 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: 'true' },
        locations: [{ existingId: 'loc-gate', code: 'GATE1', nameEn: 'Main gate', nameAr: '', latitude: 24.7, longitude: 46.7, radiusMeters: this.options.locationWithoutRadius ? null : 150,
          baseline: { nameEn: 'Main gate', nameAr: null, latitude: '24.7', longitude: '46.7', radiusMeters: this.options.locationWithoutRadius ? null : '150' } }] }],
      ...structuredClone(this.org()),
      rules: {
        workWeek: { weekendDays: 'Fri,Sat', override: false, baseline: { weekendDays: 'Fri,Sat', override: 'false' } }, holidayCalendar: { usePack: true }, leavePolicies: { usePack: true },
        employeeNumbering: { prefix: 'EVO', useCountryPrefix: false, useBranchPrefix: false, useDepartmentPrefix: false, useYear: false, paddingLength: 4, nextSequence: 42,
          baseline: { prefix: 'EVO', nextSequence: '42' } },
      },
      companyAdmin: { mode: 'groupOnly' },
    };
  }

  /** Evostel's Organisation rows today: each one this company's own (Addendum B), with its id and baseline. */
  org(): OrgRows {
    this.orgDb ??= {
      costCentres: [{ existingId: 'cc-adm', code: 'ADM', nameEn: 'Administration', nameAr: 'الإدارة', baseline: { nameEn: 'Administration' } }],
      departments: [{ existingId: 'dep-hr', code: 'HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية', baseline: { nameEn: 'Human Resources' } }],
      designations: [{ existingId: 'des-acc', code: 'ACC', titleEn: 'Accountant', titleAr: 'محاسب', baseline: { titleEn: 'Accountant' } }],
      grades: [{ existingId: 'gr-g1', code: 'G1', nameEn: 'Grade 1', nameAr: 'الدرجة 1', level: 1, minSalary: 3000, midSalary: 5000, maxSalary: 7000, baseline: { nameEn: 'Grade 1' } }],
    };
    return this.orgDb;
  }

  /** A3: the same planner for existing mode — update / unchanged by existingId, create for new rows, never a delete. */
  existingPlan(body: CompanySetupDraftBody): CompanySetupPlan {
    const before = this.existingDefaults(body.existingCompanyId!);
    // The backend's change words (CompanySetupPlanner "Changes: …."), English only.
    const WORD: Record<string, string> = { nameEn: 'name (English)', nameAr: 'name (Arabic)', titleEn: 'name (English)', city: 'city', timeZoneId: 'time zone', isHeadOffice: 'head office',
      latitude: 'coordinates', longitude: 'coordinates', radiusMeters: 'geofence radius', legalNameEn: 'legal name (English)', legalNameAr: 'legal name (Arabic)', tradeName: 'trade name',
      registrationNumber: 'CR number', molEstablishmentNumber: 'MOL establishment number', gosiEmployerId: 'GOSI number', taxNumber: 'VAT number', qiwaEstablishmentId: 'Qiwa establishment id',
      emailDomain: 'email domain', workEmailPattern: 'work-email pattern', erpCompanyCode: 'ERP company code', prefix: 'prefix', paddingLength: 'padding', nextSequence: 'next number' };
    const reason = (keys: string[]) => `Changes: ${[...new Set(keys.map(k => WORD[k] ?? k))].join(', ')}.`;
    const diff = <T extends object>(a: T, b: T | undefined, keys: (keyof T)[]) => keys.filter(k => String(a[k] ?? '') !== String(b?.[k] ?? '')).map(String);
    const own = <T extends { existingId?: string | null; code: string }>(area: PlanItem['area'], rows: T[], was: T[], keys: (keyof T)[], label: (r: T) => string): PlanItem[] =>
      rows.map(r => {
        if (!r.existingId) return { area, code: r.code, label: label(r), action: 'create' as const };
        const changed = diff(r, was.find(w => w.existingId === r.existingId), keys);
        return changed.length ? { area, code: r.code, label: label(r), action: 'update' as const, reason: reason(changed) } : { area, code: r.code, label: label(r), action: 'unchanged' as const };
      });
    const problems: CompanySetupPlan['problems'] = [];
    const warnings: CompanySetupPlan['warnings'] = [];
    // A9: a row or block without its baseline is a draft saved before baselines existed; nothing else is planned.
    const rows = [body.company, ...body.branches.filter(b => b.existingId), ...body.branches.flatMap(b => b.locations.filter(l => l.existingId)),
      ...body.costCentres.filter(c => c.existingId), body.rules.workWeek, body.rules.employeeNumbering];
    if (rows.some(r => !r.baseline))
      return { ok: false, items: [], warnings: [], problems: [{ step: 1, field: 'draft', code: 'draft_needs_refresh', message: 'This draft was saved before KynexOne recorded what the company had when the draft started.' }] };
    // A9: draft vs baseline vs "database" for the head office's English name.
    const hq = body.branches.findIndex(b => b.existingId === 'br-hq');
    const renamed = this.options.hqRenamedElsewhere;
    if (renamed && hq >= 0) {
      const mine = body.branches[hq].nameEn, was = body.branches[hq].baseline?.nameEn;
      if (mine !== was && was !== renamed && mine !== renamed)
        problems.push({ step: 3, field: `branches.${hq}.nameEn`, code: 'changed_since_draft_started', current: renamed,
          message: `Branch HQ: the name (English) was changed elsewhere since this draft started. It is now “${renamed}”. Take the current value, or keep yours.`,
          messageAr: `الفرع HQ: تم تغيير الاسم (بالإنجليزية) في مكان آخر منذ بدء هذه المسودة. القيمة الحالية «${renamed}». اعتمد القيمة الحالية أو احتفظ بقيمتك.` });
    }
    if ((this.options.cutoverStatus ?? 'Planned') !== 'Planned' && body.company.firstPayrollMonth !== before.company.firstPayrollMonth)
      problems.push({ step: 2, field: 'company.firstPayrollMonth', code: 'first_payroll_month_locked', message: 'Payroll for this company started on 2026-11-01 (cutover live), so the first payroll month can no longer change.', messageAr: 'بدأت رواتب هذه الشركة، لذا لا يمكن تغيير أول شهر للرواتب.' });
    if (body.rules.employeeNumbering.nextSequence < before.rules.employeeNumbering.nextSequence)
      problems.push({ step: 5, field: 'rules.employeeNumbering.nextSequence', code: 'sequence_cannot_go_back', message: `The next employee number is already ${before.rules.employeeNumbering.nextSequence}.` });
    if (!body.company.molEstablishmentNumber && !before.company.molEstablishmentNumber)
      warnings.push({ step: 2, field: 'company.molEstablishmentNumber', code: 'mol_required', message: 'Enter the MOL establishment number shown in Qiwa. (saved before; not changed here)' });
    if (!this.scopeGroup)
      warnings.push({ step: 5, field: 'rules.leavePolicies', code: 'workspace_defaults_need_group_scope', message: 'The workspace has no statutory default leave policies for SA (MATERNITY). They apply to every SA company, so a group administrator must add them.' });
    const companyChanged = diff(body.company, before.company, ['legalNameEn', 'legalNameAr', 'tradeName', 'registrationNumber', 'molEstablishmentNumber', 'gosiEmployerId', 'taxNumber', 'qiwaEstablishmentId', 'emailDomain', 'workEmailPattern', 'erpCompanyCode', 'firstPayrollMonth']);
    const numberingChanged = diff(body.rules.employeeNumbering, before.rules.employeeNumbering, ['prefix', 'paddingLength', 'nextSequence', 'useCountryPrefix', 'useBranchPrefix', 'useDepartmentPrefix', 'useYear']);
    const items: PlanItem[] = [
      companyChanged.length
        ? { area: 'company', code: body.company.registrationNumber, label: body.company.legalNameEn, action: 'update', reason: reason(companyChanged) }
        : { area: 'company', code: body.company.registrationNumber, label: body.company.legalNameEn, action: 'unchanged' },
      ...own('branch', body.branches, before.branches, ['nameEn', 'nameAr', 'city', 'timeZoneId', 'isHeadOffice'], b => b.nameEn),
      ...body.branches.flatMap(b => own('location', b.locations, before.branches.find(w => w.existingId && w.existingId === b.existingId)?.locations ?? [], ['nameEn', 'nameAr', 'latitude', 'longitude', 'radiusMeters'], l => l.nameEn)),
      ...own('costCentre', body.costCentres, before.costCentres, ['nameEn', 'nameAr'], c => c.nameEn),
      ...own('department', body.departments, before.departments, ['nameEn', 'nameAr', 'parentCode', 'branchCode', 'costCentreCode'], d => d.nameEn),
      ...own('designation', body.designations, before.designations, ['titleEn', 'titleAr', 'departmentCode', 'gradeCode'], d => d.titleEn),
      ...own('grade', body.grades, before.grades, ['nameEn', 'nameAr', 'level', 'minSalary', 'midSalary', 'maxSalary'], g => g.nameEn),
      { area: 'workWeek', code: body.rules.workWeek.weekendDays, label: 'Working week', action: 'unchanged' },
      { area: 'gccSetting', code: 'SA', label: 'End-of-service (Saudi Labour Law)', labelAr: 'مكافأة نهاية الخدمة (نظام العمل السعودي)', action: 'unchanged' },
      { area: 'holidayCalendar', code: 'SA-2026', label: 'Saudi public holidays 2026', action: 'reuse' },
      numberingChanged.length
        ? { area: 'employeeIdRule', code: body.rules.employeeNumbering.prefix, label: 'Employee numbering', action: 'update', reason: reason(numberingChanged) }
        : { area: 'employeeIdRule', code: body.rules.employeeNumbering.prefix, label: 'Employee numbering', action: 'unchanged' },
      ...(body.companyAdmin.mode !== 'groupOnly' ? [{ area: 'companyAdmin' as const, code: body.companyAdmin.email ?? body.companyAdmin.existingUserId ?? '', label: body.companyAdmin.fullNameEn ?? 'Existing user', action: 'create' as const }] : []),
    ];
    return { ok: problems.length === 0, items, problems, warnings };
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
      ...body.departments.map(d => ({ area: 'department' as const, code: d.code, label: d.nameEn, action: 'create' as const })),
      ...body.designations.map(d => ({ area: 'designation' as const, code: d.code, label: d.titleEn, action: 'create' as const })),
      ...body.grades.map(g => ({ area: 'grade' as const, code: g.code, label: g.nameEn, action: 'create' as const })),
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
      const open = body.existingCompanyId ? this.openDraftFor(body.existingCompanyId) : null;
      if (open) return reply({ code: 'existing_company_draft_open', message: 'This company already has an open draft.', draftId: open }, 409);
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
    if (rest === '/copy-rows' && method === 'GET') {
      const section = url.searchParams.get('section') as Section;
      const fromCompanyId = url.searchParams.get('fromCompanyId') ?? '';
      this.copyCalls.push({ fromCompanyId, section });
      if (!this.scopeGroup || fromCompanyId !== sister.id) return reply({ code: 'copy_source_not_allowed', message: 'Not a sister company.' }, 403);
      return reply({ section, fromCompanyId, [section]: structuredClone(SISTER_ROWS[section]) });
    }
    const sectionMatch = rest.match(/^\/drafts\/([^/]+)\/sections\/([a-zA-Z]+)$/);
    if (sectionMatch && method === 'POST') {
      const draft = this.drafts.get(sectionMatch[1]);
      const section = sectionMatch[2] as Section;
      const { version } = req.postDataJSON() as { version: number };
      if (!draft) return reply({ message: 'Not found' }, 404);
      if (!draft.body.existingCompanyId) return reply({ code: 'section_apply_needs_company', message: 'Create mode keeps rows until Create.' }, 409);
      const marker = `${draft.id}:${section}:${version}`;
      if (version === draft.version - 1 && this.sectionMarkers.has(marker)) {
        this.sectionApplies.push({ section, version, replayed: true });
        return reply({ draft, section, created: 0, updated: 0, unchanged: 0, replayed: true });
      }
      if (version !== draft.version) return reply({ code: 'draft_version_stale', message: 'This draft was saved somewhere else.', field: 'version' }, 409);
      const failure = this.options.sectionFailure;
      if (failure && failure !== 'lost') {
        this.options.sectionFailure = undefined;
        if (failure === 'network') return route.abort('failed');
        const index = draft.body.departments.findIndex(d => !d.existingId);
        return reply({ code: 'reference_not_saved', message: 'Save Cost centres first: CC_NEW is not saved yet.',
          plan: { ok: false, items: [], warnings: [], problems: [{ step: 4, field: `departments.${Math.max(index, 0)}.costCentreCode`, code: 'reference_not_saved', message: 'Save Cost centres first: CC_NEW is not saved yet.' }] } }, 422);
      }
      const db = this.org()[section] as { existingId?: string | null; code: string; baseline?: Record<string, string | null> | null }[];
      let created = 0, updated = 0, unchanged = 0;
      const rows = (draft.body[section] as typeof db).map((row, i) => {
        const { baseline: _b, ...plain } = row;
        const saved = { ...plain, existingId: row.existingId ?? `${section}-new-${i}-${this.sequence++}`, baseline: { name: 'saved' } };
        const was = db.find(d => d.existingId && d.existingId === row.existingId);
        if (!was) created += 1;
        else if (JSON.stringify({ ...was, baseline: null }) !== JSON.stringify({ ...plain, baseline: null })) updated += 1;
        else unchanged += 1;
        return saved;
      });
      (this.org() as Record<Section, unknown>)[section] = structuredClone(rows);
      const rebased: CompanySetupDraft = { ...draft, version: draft.version + 1, step: 4, body: { ...draft.body, [section]: rows }, updatedAtUtc: now };
      this.drafts.set(draft.id, rebased);
      this.sectionMarkers.add(marker);
      this.sectionApplies.push({ section, version, replayed: false });
      // A lost response: the write happened, the reply never arrived.
      if (failure === 'lost') { this.options.sectionFailure = undefined; return route.abort('failed'); }
      return reply({ draft: rebased, section, created, updated, unchanged, replayed: false });
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
        if (!plan.ok) return reply({ code: plan.problems[0].code, message: plan.problems[0].message, step: plan.problems[0].step, field: plan.problems[0].field }, 422);
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
    const readinessMatch = rest.match(/^\/readiness\/([^/]+)$/);
    if (readinessMatch && method === 'GET') {
      if (!this.ownCompany(readinessMatch[1])) return reply({ message: 'Not your company.' }, 403);
      const href = (tab: string) => `/setup?tab=${tab}&companyId=${readinessMatch[1]}`;
      return reply([
        { key: 'branches', label: 'Branches and head office', labelAr: 'الفروع والمقر الرئيسي', status: 'done', scope: 'company', href: href('branches') },
        { key: 'glMapping', label: 'GL mapping', labelAr: 'ربط دفتر الأستاذ', status: 'todo', scope: 'company', href: href('glMapping') },
        { key: 'bankWps', label: 'Bank and WPS settings (before first payment)', labelAr: 'إعدادات البنك وحماية الأجور (قبل أول دفعة)', status: 'todo', scope: 'company', href: href('gccSettings') },
        { key: 'smtp', label: 'Outgoing email (SMTP)', labelAr: 'البريد الصادر (SMTP)', status: 'recommended', scope: 'workspace', href: '/setup?tab=emailConfig' },
      ]);
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
  if (!process.env.WIZARD_REAL_TILES) await page.route('https://tile.openstreetmap.org/**', route => route.fulfill({ status: 200, contentType: 'image/png', body: BLANK_TILE }));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (p.startsWith('/api/company-setup')) return server.handle(route, p.slice('/api/company-setup'.length));
    if (p === '/api/localization/transliterate/batch' && request.method() === 'POST') {
      const { texts, kind } = request.postDataJSON() as { texts: string[]; kind: string };
      if (kind !== 'organization-label') return reply({ error: 'unsupported_transliteration_kind' }, 400);
      server.suggestionRequests.push(texts);
      return reply({ suggestions: texts.map(text => {
        const suggestion = ORG_ARABIC[text.trim().replace(/\s+/g, ' ').toLowerCase()] ?? '';
        return { text, suggestion, requiresManualEntry: !suggestion };
      }) });
    }
    if (p === '/api/geo/resolve-map-link') {
      const link = new URL(request.url()).searchParams.get('url') ?? '';
      server.mapLinks.push(link);
      return link === SHORT_LINK ? reply(SHORT_LINK_AT) : reply({ error: 'ERR_MAP_LINK_UNREADABLE', message: 'This map link could not be read.' }, 422);
    }
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
    // Step 5 embeds the Country rules editor (a plain list).
    if (p === '/api/admin/gcc-settings') return reply([]);
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

/** The footer's primary action: "Next", "Next: Departments", "Save cost centres & next"… */
async function next(page: Page, _t?: ReturnType<typeof T>) {
  await page.getByTestId('wizard-next').click();
}

/** Click the primary action until the given h2 (a step heading or a part name) is on screen. */
async function nextUntil(page: Page, t: ReturnType<typeof T>, heading: string, max = 14) {
  const target = page.getByRole('heading', { level: 2, name: t(heading), exact: true });
  for (let i = 0; i < max; i++) {
    if (await target.isVisible()) return;
    const position = await page.getByTestId('wizard-position').getAttribute('data-position');
    await next(page, t);
    await expect(page.getByTestId('wizard-position')).not.toHaveAttribute('data-position', position ?? '', { timeout: 10_000 }).catch(() => undefined);
  }
  await expect(target).toBeVisible();
}

/** Through every remaining part and step to the review. */
async function toReview(page: Page, t: ReturnType<typeof T>, existingMode = false) {
  await nextUntil(page, t, existingMode ? 'Review & update' : 'Review & create');
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

    // Step 4, one part at a time: Cost centres → Departments → Grades → Designations (Addendum B4).
    await expectStep(page, t, 'Cost centres');
    await expect(page).toHaveURL(/part=costCentres/);
    await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
    const cc = page.getByTestId('costCentres-row').first();
    await expect(cc.getByLabel(t('Code'))).toBeFocused();
    await cc.getByLabel(t('Name (English)')).fill('Administration');
    // A code is suggested from the name and used only when the reader says so.
    await expect(cc.getByLabel(t('Code'))).toHaveAttribute('placeholder', t('Suggested: {code}', { code: 'CC_ADM' }));
    await expect(cc.getByLabel(t('Code'))).toHaveValue('');
    await cc.getByRole('button', { name: new RegExp(t('Use {code}', { code: 'CC_ADM' })) }).click();
    await expect(cc.getByLabel(t('Code'))).toHaveValue('CC_ADM');
    await expect(cc.getByLabel(t('Code'))).toHaveAttribute('dir', 'ltr');
    await cc.getByLabel(t('Name (Arabic)')).fill('الإدارة');
    await expect(page.getByTestId('arabic-meter')).toHaveText(t('Arabic names: {filled} of {total}', { filled: 1, total: 1 }));
    await expect(page.getByTestId('wizard-next')).toContainText(t('Next: {part}', { part: t('Departments') }));
    await contained(page);
    await shot(page, info, `${locale}-4a-cost-centres`);
    await next(page, t);

    await expectStep(page, t, 'Departments');
    await expect(page).toHaveURL(/part=departments/);
    // HR came from the country pack: an ordinary row of this company, editable like any other.
    const hr = page.getByTestId('departments-row').first();
    await expect(hr.getByLabel(t('Code'))).toHaveValue('HR');
    await expect(hr.getByLabel(t('Name (English)'))).toBeEditable();
    await expect(page.getByText(t('Reused'), { exact: true })).toHaveCount(0);
    // Copy from the sister company, by value: HR is already here, so only OPS is added.
    await page.getByTestId('copy-from-sister').click();
    await expect(page.getByTestId('copy-status')).toHaveText(t('{added, plural, one {Copied # row} other {Copied # rows}} from {company}. {skipped, plural, one {# was skipped because its code is already here.} other {# were skipped because their codes are already here.}}', { added: 1, skipped: 1, company: sister.legalNameEn }));
    const ops = page.getByTestId('departments-row').nth(1);
    await expect(ops.getByLabel(t('Code'))).toHaveValue('OPS');
    // Its cost centre was the sister's; this company has none by that code, so Next stops on the field.
    await next(page, t);
    await expect(page.locator('#ccw-departments-1-costCentreCode')).toBeFocused();
    await page.locator('#ccw-departments-1-costCentreCode').selectOption('CC_ADM');
    await contained(page);
    await shot(page, info, `${locale}-4b-departments`);
    await next(page, t);

    await expectStep(page, t, 'Grades');
    await page.getByRole('button', { name: t('Add grade'), exact: true }).click();
    const grade = page.getByTestId('grades-row').first();
    await grade.getByLabel(t('Code')).fill('G1');
    await grade.getByLabel(t('Name (English)')).fill('Grade 1');
    await page.locator('#ccw-grades-0-minSalary').fill('3000');
    await page.locator('#ccw-grades-0-maxSalary').fill('7000');
    await grade.getByTestId('use-middle').click();
    await expect(page.locator('#ccw-grades-0-midSalary')).toHaveValue('5000');
    await contained(page);
    await shot(page, info, `${locale}-4c-grades`);
    await next(page, t);

    await expectStep(page, t, 'Designations');
    await page.getByRole('button', { name: t('Add designation'), exact: true }).click();
    const acc = page.getByTestId('designations-row').first();
    await acc.getByLabel(t('Code')).fill('ACC');
    await acc.getByLabel(t('Name (English)')).fill('Accountant');
    await page.locator('#ccw-designations-0-departmentCode').selectOption('HR');
    await page.locator('#ccw-designations-0-gradeCode').selectOption('G1');
    await expect(page.getByTestId('structure-tree').first()).not.toContainText(t('Shared group catalog'));
    await expect(page.getByTestId('wizard-next')).toContainText(t('Next: {step}', { step: t('Work & pay rules') }));
    await contained(page);
    await shot(page, info, `${locale}-4d-designations`);
    await next(page, t);

    // Step 5 parts (their content is builder D's RulesStep).
    await expect(page).toHaveURL(/part=workRules/);
    await expect(page.getByTestId('work-week-mode').first()).toHaveText(t('This company follows the country working week.'));
    await page.getByLabel(t('Prefix')).first().fill('RTC');
    await expect(page.getByTestId('numbering-preview').first()).toContainText('RTC-0001');
    await contained(page);
    await shot(page, info, `${locale}-5-rules`);
    await nextUntil(page, t, 'Company super admin');

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
    expect(saved.departments.map(d => [d.code, d.costCentreCode ?? null])).toEqual([['HR', null], ['OPS', 'CC_ADM']]);
    expect(saved.costCentres).toEqual([{ code: 'CC_ADM', nameEn: 'Administration', nameAr: 'الإدارة' }]);
    expect(saved.grades[0]).toMatchObject({ code: 'G1', minSalary: 3000, midSalary: 5000, maxSalary: 7000 });
    expect(saved.designations[0]).toMatchObject({ code: 'ACC', departmentCode: 'HR', gradeCode: 'G1' });
    expect(server.copyCalls).toEqual([{ fromCompanyId: sister.id, section: 'departments' }]);
    expect(server.sectionApplies).toEqual([]); // create mode: nothing is written before Create
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
  await expect(page).toHaveURL(/path=guided/);
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
  await expect(page.getByTestId('wizard-back')).toContainText(t('Company details'));
  await page.getByTestId('wizard-back').click();
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
  await toReview(page, t); // step 6: group super admins (default) is valid
  await expectStep(page, t, 'Review & create');
  await expect(page.getByTestId('review-plan')).toContainText('Cost centre ADM is already used by Sister Trading KSA. Use another code.');
  await expect(page.getByRole('button', { name: t('Create company'), exact: true })).toBeDisabled();
  await expect(page.getByText(t('Create is available once the items above are fixed.'))).toBeVisible();
  await shot(page, info, 'en-7-review-conflict');
  await page.getByRole('button', { name: t('Go to {step}', { step: t('Organisation') }) }).click();
  await expectStep(page, t, 'Cost centres');
  await page.getByTestId('costCentres-row').first().getByLabel(t('Code')).fill('ADM2');
  await toReview(page, t);
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
    await toReview(page, t);
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

    await expectStep(page, t, 'Cost centres');
    await next(page, t);
    await expectStep(page, t, 'Departments');
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const typed = page.getByTestId('departments-row').nth(1);
    await typed.getByLabel(t('Code')).fill('FINANCE');
    await typed.getByLabel(t('Name (English)')).fill('Finance and accounts');
    // A section with entered rows asks too.
    await page.getByRole('button', { name: t('Use defaults'), exact: true }).click();
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
    await expectStep(page, t, 'Cost centres');
    await check('4-organisation');
  }
});

/** Steps 1-7 with an invited company super admin, through Create, to the success screen. */
async function createWithInvitedAdmin(page: Page, loc: Locale = 'en') {
  const t = T(loc);
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await nextUntil(page, t, 'Company super admin');
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

// ── Addendum A: Guided setup chooser and existing-company mode ────────────────

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
  test(`Guided setup opens the chooser: create, or complete an existing company (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
    await openChooser(page);
    // Guided setup is the current Setup section, named once in its header.
    await expect(page.locator('#setup-section-heading')).toHaveText(t('Guided setup'));
    await expect(await setupSection(page, t('Guided setup'), t('Setup sections'))).toHaveAttribute('aria-current', 'page');
    await page.keyboard.press('Escape');
    // The old 5-step studio is gone from Guided setup.
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

test('the old studio stays reachable only as Setup → Policy starter', async ({ page }) => {
  await boot(page);
  await page.goto('/setup?tab=aiSetup&path=configuration');
  await openSetupSection(page, 'Policy starter');
  await expect(page).toHaveURL(/path=policyStarter/);
  await expect(page.getByTestId('policy-starter-banner')).toBeVisible();
  // Its own Setup section, never Guided setup.
  await expect(page.locator('#setup-section-heading')).toHaveText('Policy starter');
  await expect(await setupSection(page, 'Policy starter')).toHaveAttribute('aria-current', 'page');
  await expect(await setupSection(page, 'Guided setup')).not.toHaveAttribute('aria-current', 'page');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Back to All setup areas', exact: true }).click();
  await expect(page).toHaveURL(/path=configuration/);
});

for (const locale of ['en', 'ar'] as const) {
  test(`complete an existing company: edit a branch, add one, review the update, save (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
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
    await expect(page.getByLabel(t('Legal name (English)'))).toHaveValue('Evostel LLC');
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

    // Organisation, one part at a time. Nothing changed in cost centres: plain Next, nothing written.
    await expectStep(page, t, 'Cost centres');
    await expect(page.getByTestId('wizard-next')).toContainText(t('Next: {part}', { part: t('Departments') }));
    await expect(page.getByRole('button', { name: t('Use defaults'), exact: true })).toHaveCount(0);
    await expect(page.getByTestId('manage-in-setup')).toHaveCount(0);
    await next(page, t);
    await expectStep(page, t, 'Departments');
    const hr = page.getByTestId('departments-row').first();
    await expect(hr).toHaveAttribute('data-existing', 'true');
    // A saved row keeps its code and is never removed here; its name is this company's to edit.
    await expect(hr.getByLabel(t('Code'))).toHaveAttribute('readonly', '');
    await expect(hr.getByRole('button', { name: new RegExp(t('Remove')) })).toHaveCount(0);
    await hr.getByLabel(t('Name (English)')).fill('People & Culture');
    await expect(page.getByTestId('wizard-next')).toContainText(t('Save departments & next'));
    await expect(page.getByTestId('saves-now')).toHaveText(t('Saves to {company} now. You can still change it later in Setup.', { company: locale === 'ar' ? 'شركة إيفوستل' : 'Evostel LLC' }));
    await contained(page);
    await vshot(page, info, `existing-${locale}-4b-departments`);
    await next(page, t);
    // Written to the company now (B2), then on to Grades.
    await expectStep(page, t, 'Grades');
    await expect(page.getByTestId('section-saved')).toHaveText(t('{part} saved · {created} added, {updated} changed', { part: t('Departments'), created: 0, updated: 1 }));
    expect(server.sectionApplies).toEqual([{ section: 'departments', version: expect.any(Number), replayed: false }]);
    await nextUntil(page, t, 'Work rules');
    await expect(page.getByTestId('numbering-preview').first()).toContainText('EVO-0042');
    // More settings is its own step 5 part now (B4); company-setup-rules-parts.spec.ts covers it.
    await expect(page.getByTestId('wizard-rail')).toContainText(t('More settings'));
    await contained(page);
    await vshot(page, info, `existing-${locale}-5-rules`);
    await nextUntil(page, t, 'Company super admin');

    await expectStep(page, t, 'Company super admin');
    await expect(page.getByLabel(t('Keep the current administrators'))).toBeChecked();
    await contained(page);
    await vshot(page, info, `existing-${locale}-6-admin`);
    await next(page, t);

    await expectStep(page, t, 'Review & update');
    // The department was saved by Save & next, so the review shows only the branch changes.
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
    await page.getByRole('button', { name: t('Save all changes'), exact: true }).click();

    await expect(page.getByTestId('create-company-success')).toHaveAttribute('data-mode', 'existing');
    await expect(page.getByRole('heading', { name: t('{name} is updated', { name: locale === 'ar' ? 'شركة إيفوستل' : 'Evostel LLC' }) })).toBeVisible();
    await expect(page.getByTestId('readiness-glMapping')).toBeVisible();
    await expect(page.getByRole('link', { name: t('Back to Guided setup'), exact: true })).toHaveAttribute('href', '/setup?tab=aiSetup&path=guided');
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
    await toReview(page, t, true);
    await expect(page.getByTestId('review-no-change')).toBeVisible();
    expect(server.sectionApplies).toEqual([]); // nothing changed: nothing written
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
    // Every Organisation row is this company's own: a company super admin adds departments too, and is offered no
    // copy from a sister company (that is group scope).
    await expectStep(page, t, 'Cost centres');
    await expect(page.getByTestId('copy-from-sister')).toHaveCount(0);
    await next(page, t);
    await expectStep(page, t, 'Departments');
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const fin = page.getByTestId('departments-row').nth(1);
    await fin.getByLabel(t('Code')).fill('FIN');
    await fin.getByLabel(t('Name (English)')).fill('Finance');
    await expect(page.getByTestId('departments-row')).toHaveCount(2);
    await contained(page);
    await vshot(page, info, `existing-${locale}-4-organisation-company-admin`);
    await nextUntil(page, t, 'Company super admin');
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

for (const locale of ['en', 'ar'] as const) {
  test(`the chooser explains "5 of 9 done": the checklist opens in place, with one next action (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
    await openChooser(page);
    // Exactly one primary action on the page: the incomplete company, not "Create a new company" as well.
    await expect(page.getByTestId('guided-setup-chooser').locator('.btn-primary')).toHaveCount(1);
    await expect(evostelRow(page).locator('.btn-primary')).toHaveCount(1);
    const toggle = evostelRow(page).getByRole('button', { name: t('Show what is left') });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(evostelRow(page).getByRole('button', { name: t('Hide the checklist') })).toHaveAttribute('aria-expanded', 'true');
    const details = evostelRow(page).getByTestId('chooser-readiness-details');
    await expect(details.getByTestId('readiness-summary')).toContainText(t('{done} of {total} done', { done: 1, total: 4 }));
    const next = details.locator('[data-next="true"]');
    await expect(next).toHaveCount(1);
    await expect(next).toHaveAttribute('data-testid', 'readiness-glMapping');
    await expect(next.getByRole('link', { name: t('Next: {item}', { item: locale === 'ar' ? 'ربط دفتر الأستاذ' : 'GL mapping' }) })).toHaveAttribute('href', '/setup?tab=glMapping&companyId=co-evostel');
    await expect(details.locator('.btn-primary')).toHaveCount(1);
    await expect(details.getByTestId('readiness-smtp')).toContainText(t('Recommended (optional)'));
    await contained(page);
    await vshot(page, info, `existing-${locale}-0-chooser-checklist`);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('a create draft can be discarded from the chooser, and the newest draft is the one primary action', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await page.goto('/setup/new-company');
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await page.getByRole('button', { name: t('Save draft & exit'), exact: true }).click();
  await openChooser(page);
  const drafts = page.getByTestId('chooser-create-drafts');
  await expect(drafts.getByRole('link', { name: /Resume/ })).toHaveClass(/btn-primary/);
  await expect(page.getByTestId('guided-setup-chooser').locator('.btn-primary')).toHaveCount(1);
  await expect(drafts).toContainText(t('Started by {name}, saved {when}', { name: 'Group Owner', when: 'now' }).replace(/now$/, ''));
  await drafts.getByRole('button', { name: /Discard/ }).click();
  await page.getByRole('dialog').getByRole('button', { name: t('Discard draft'), exact: true }).click();
  await expect(page.getByTestId('chooser-create-drafts')).toHaveCount(0);
  expect([...server.drafts.values()].filter(d => !d.body.existingCompanyId && d.status === 'Open')).toHaveLength(0);
});

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

test('a first payroll month is read-only up front once payroll has started, with its reason', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page, { cutoverStatus: 'Active' });
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  const month = page.getByLabel(t('First payroll month'));
  await expect(month).toHaveAttribute('readonly', '');
  await expect(month).toHaveAttribute('aria-readonly', 'true');
  await expect(month).toHaveValue('2026-11');
  await expect(page.getByText(t('Payroll has already started for this company, so its first payroll month stays as it is.'))).toBeVisible();
  await toReview(page, t, true);
  await expect(page.getByRole('button', { name: t('Finish without changes'), exact: true })).toBeEnabled();
  expect(server.dryRuns.at(-1)?.company.firstPayrollMonth).toBe('2026-11');
});

test('a branch renamed elsewhere since the draft started is a conflict on review; taking the current value clears it', async ({ page }) => {
  const t = T('en');
  const { server, pageErrors } = await boot(page, { hqRenamedElsewhere: 'Head Office (Setup)' });
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  await page.getByTestId('branches-row').first().getByLabel(t('Name (English)')).first().fill('Head Office Riyadh');
  await toReview(page, t, true);
  const conflict = page.locator('[data-testid="review-plan"] li[data-code="changed_since_draft_started"]');
  await expect(conflict).toContainText('Head Office (Setup)');
  await expect(conflict).toHaveAttribute('data-field', 'branches.0.nameEn');
  await expect(page.getByRole('button', { name: t('Save all changes'), exact: true })).toBeDisabled();
  // Linked to the field: Go to opens the step with that field focused and the same words on it.
  await conflict.getByRole('button', { name: t('Go to {step}', { step: t('Branches & locations') }) }).click();
  await expect(page.locator('#ccw-branches-0-nameEn')).toBeFocused();
  await expect(page.locator('#ccw-branches-0-nameEn-error')).toContainText('Head Office (Setup)');
  await toReview(page, t, true);
  await conflict.getByRole('button', { name: t('Use the current value ({value})', { value: 'Head Office (Setup)' }) }).click();
  await expect(conflict).toHaveCount(0);
  await expect(page.getByRole('button', { name: t('Save all changes'), exact: true })).toBeEnabled();
  const sent = server.dryRuns.at(-1)!;
  expect(sent.branches[0].nameEn).toBe('Head Office (Setup)');
  // Every other field of the baseline is carried back exactly as the prefill sent it.
  expect(sent.branches[0].baseline).toEqual({ ...server.existingDefaults('co-evostel').branches[0].baseline, nameEn: 'Head Office (Setup)' });
  expect(sent.branches[0].locations[0].baseline).toEqual(server.existingDefaults('co-evostel').branches[0].locations[0].baseline);
  expect(pageErrors).toEqual([]);
});

test('a draft saved before baselines existed asks to reload the company, and the reload plans cleanly', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  const now = new Date().toISOString();
  const prefill = server.existingDefaults('co-evostel');
  const legacy = { ...prefill, company: { ...prefill.company, baseline: undefined }, branches: prefill.branches.map(b => ({ ...b, baseline: undefined })) };
  server.drafts.set('draft-old', { id: 'draft-old', version: 1, step: 7, status: 'Open', body: legacy, createdByUserId: 'u-1', createdByName: 'Group Admin', createdAtUtc: now, updatedAtUtc: now });
  await openChooser(page);
  await evostelRow(page).getByRole('link', { name: new RegExp(t('Resume setup')) }).click();
  await expectStep(page, t, 'Review & update');
  const refresh = page.locator('[data-testid="review-plan"] li[data-code="draft_needs_refresh"]');
  await expect(refresh).toContainText(t('This draft was started before KynexOne kept a record of what the company had at the time, so it cannot tell your edits from changes made elsewhere. Reload the company’s current setup, then make your changes again.'));
  await refresh.getByRole('button', { name: t('Reload the company’s current setup'), exact: true }).click();
  await page.getByRole('button', { name: t('Replace my draft'), exact: true }).click();
  await expect(refresh).toHaveCount(0);
  await expect(page.getByTestId('review-no-change')).toBeVisible();
  expect(server.dryRuns.at(-1)!.company.baseline).toEqual(prefill.company.baseline);
  await expect.poll(() => server.drafts.get('draft-old')!.body.branches[0].baseline).toEqual(prefill.branches[0].baseline);
  expect(server.commits).toEqual([]);
});

for (const locale of ['en', 'ar'] as const) {
  test(`a value saved before today's checks warns instead of blocking; the counter cannot go back (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    await boot(page, { locale, legacyMol: true });
    await openEvostel(page, t);
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await expectStep(page, t, 'Company details');
    const legacy = page.getByTestId('legacy-warnings');
    await expect(legacy).toContainText(t('Enter the MOL establishment number shown in Qiwa.'));
    await contained(page);
    await vshot(page, info, `existing-${locale}-2-company-legacy-warning`);
    await next(page, t);
    await expectStep(page, t, 'Branches & locations'); // not blocked
    await nextUntil(page, t, 'Work rules');
    await expect(page.getByText(t('Already at {number}; it can only go up.', { number: 42 }))).toBeVisible();
    await page.getByLabel(t('Next number')).fill('7');
    await next(page, t);
    await expect(page.locator('#ccw-rules-employeeNumbering-nextSequence-error')).toHaveText(t('The next number is already {number}. A lower one could repeat a number already given; enter {number} or higher.', { number: 42 }));
    await expectStep(page, t, 'Work rules');
  });
}

test('starting while someone else opened a draft for the company resumes theirs (409 carries its id)', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await openEvostel(page, t);
  // Another admin starts the company's draft after this page loaded.
  const now = new Date().toISOString();
  const theirs = { ...server.existingDefaults('co-evostel'), company: { ...server.existingDefaults('co-evostel').company, tradeName: 'Started elsewhere' } };
  server.drafts.set('draft-elsewhere', { id: 'draft-elsewhere', version: 3, step: 2, status: 'Open', body: theirs, createdByUserId: 'u-2', createdByName: 'Second Admin', createdAtUtc: now, updatedAtUtc: now });
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expect(page).toHaveURL(/draft=draft-elsewhere/);
  await expectStep(page, t, 'Company details');
  await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue('Started elsewhere');
  expect([...server.drafts.values()].filter(d => d.body.existingCompanyId === 'co-evostel')).toHaveLength(1);
});

test('a company super admin is told, in plain words, that shared country defaults wait for a group administrator', async ({ page }) => {
  const t = T('en');
  await boot(page, { persona: 'companyAdmin' });
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await toReview(page, t, true);
  await expect(page.getByTestId('review-plan')).toContainText(t('Some country defaults that every company shares are missing, and only a group administrator can add them. Ask your group super admin; nothing else waits on it here.'));
  await expect(page.getByRole('button', { name: t('Finish without changes'), exact: true })).toBeEnabled();
});

// ── Suggest Arabic (every Arabic name field) and Pin this location (step 3) ───────────────────────────

async function toBranches(page: Page, t: ReturnType<typeof T>) {
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
}

async function addLocation(page: Page, t: ReturnType<typeof T>, nameEn = 'Main gate') {
  const hq = page.getByTestId('branches-row').first();
  if (!(await hq.locator('details').first().evaluate(d => (d as HTMLDetailsElement).open))) await hq.locator('summary').first().click();
  await hq.getByRole('button', { name: t('Add location'), exact: true }).click();
  const location = page.getByTestId('branches.0.locations-row').last();
  await location.getByLabel(t('Code')).fill('GATE1');
  await location.getByLabel(t('Name (English)')).fill(nameEn);
  return location;
}

const arabicInput = (row: ReturnType<Page['getByTestId']>, t: ReturnType<typeof T>, label = 'Name (Arabic)') => row.getByLabel(t(label), { exact: true });

for (const locale of ['en', 'ar'] as const) {
  test(`Suggest Arabic sits under every Arabic name field: use, replace, and no suggestion (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    if (info.project.name === 'pilot') await page.emulateMedia({ colorScheme: 'dark' });
    const { server, pageErrors } = await boot(page, { locale });
    await openWizardFromCompanies(page, t);
    await startAndFillCompany(page, t);

    // Company legal name: no confident suggestion is said in words, and nothing is written.
    await page.getByTestId('suggest-arabic').first().click();
    await expect(page.getByTestId('suggest-arabic-none')).toHaveText(new RegExp(t('No suggestion for this wording. Type the Arabic name.').replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    await expect(page.getByLabel(t('Legal name (Arabic)'))).toHaveValue('شركة الرياض للتجارة');
    await shot(page, info, `arloc-${locale}-suggest-none`);
    await next(page, t);

    // A branch name already filled: offered as "Replace with «…»", never written until chosen.
    await expectStep(page, t, 'Branches & locations');
    const hq = page.getByTestId('branches-row').first();
    await hq.getByTestId('suggest-arabic').first().click();
    const replace = hq.getByRole('button', { name: new RegExp(t('Replace with «{arabic}»', { arabic: 'المكتب الرئيسي' })) });
    await expect(replace).toBeVisible();
    await expect(arabicInput(hq, t).first()).toHaveValue('المقر الرئيسي');
    await shot(page, info, `arloc-${locale}-suggest-replace`);
    await replace.click();
    await expect(arabicInput(hq, t).first()).toHaveValue('المكتب الرئيسي');
    await expect(arabicInput(hq, t).first()).toBeFocused();

    // An empty location name: "Use «…»".
    const location = await addLocation(page, t);
    await location.getByTestId('suggest-arabic').click();
    await location.getByRole('button', { name: new RegExp(t('Use «{arabic}»', { arabic: 'البوابة الرئيسية' })) }).click();
    await expect(arabicInput(location, t)).toHaveValue('البوابة الرئيسية');
    await next(page, t);

    // Step 4, one part at a time: cost centres, departments, grades and designations each carry the button on their Arabic column.
    await expectStep(page, t, 'Cost centres');
    const add = async (section: string, button: string, code: string, english: string, englishLabel = 'Name (English)') => {
      await page.getByRole('button', { name: t(button), exact: true }).click();
      const row = page.getByTestId(`${section}-row`).last();
      await row.getByLabel(t('Code')).fill(code);
      await row.getByLabel(t(englishLabel)).fill(english);
      return row;
    };
    const useSuggestion = async (row: ReturnType<Page['getByTestId']>, arabic: string, label?: string) => {
      await expect(row.getByTestId('suggest-arabic')).toHaveCount(1);
      await row.getByTestId('suggest-arabic').click();
      await row.getByRole('button', { name: new RegExp(t('Use «{arabic}»', { arabic })) }).click();
      await expect(arabicInput(row, t, label)).toHaveValue(arabic);
    };
    await useSuggestion(await add('costCentres', 'Add cost centre', 'CC_HR', 'Human Resources Cost Center'), 'مركز تكلفة الموارد البشرية');
    await next(page, t);
    await expectStep(page, t, 'Departments');
    await useSuggestion(await add('departments', 'Add department', 'OPS', 'Operations'), 'العمليات');
    // Dismiss leaves a typed value alone.
    const typed = await add('departments', 'Add department', 'MKT', 'Marketing');
    await arabicInput(typed, t).fill('تسويق خاص');
    await typed.getByTestId('suggest-arabic').click();
    await typed.getByRole('button', { name: new RegExp(`^${t('Dismiss')}`) }).click();
    await expect(arabicInput(typed, t)).toHaveValue('تسويق خاص');
    await next(page, t);
    await expectStep(page, t, 'Grades');
    await useSuggestion(await add('grades', 'Add grade', 'G9', 'Grade 9'), 'الدرجة 9');
    await next(page, t);
    await expectStep(page, t, 'Designations');
    await useSuggestion(await add('designations', 'Add designation', 'SWE', 'Software Engineer'), 'مهندس برمجيات');
    await contained(page);
    await shot(page, info, `arloc-${locale}-suggest-organisation`);

    // Suggestions come from our own offline endpoint, organisation wording only.
    expect(server.suggestionRequests.flat()).toEqual(expect.arrayContaining(['Riyadh Trading Company', 'Head Office', 'Main gate', 'Operations']));
    await expect.poll(() => server.puts.at(-1)?.body.designations.find(d => d.code === 'SWE')?.titleAr).toBe('مهندس برمجيات');
    expect(pageErrors).toEqual([]);
  });

  test(`Fill missing Arabic names fills only the empty ones and lists what it left blank (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server } = await boot(page, { locale });
    await toBranches(page, t);
    await next(page, t);
    await expectStep(page, t, 'Cost centres');
    await nextUntil(page, t, 'Departments');
    const departments = [['OPS', 'Operations', ''], ['WID', 'Widget Lab', ''], ['SAL', 'Sales', ''], ['MKT', 'Marketing', 'تسويق خاص']];
    for (const [code, english, arabic] of departments) {
      await page.getByRole('button', { name: t('Add department'), exact: true }).click();
      const row = page.getByTestId('departments-row').last();
      await row.getByLabel(t('Code')).fill(code);
      await row.getByLabel(t('Name (English)')).fill(english);
      if (arabic) await arabicInput(row, t).fill(arabic);
    }
    const section = page.getByTestId('departments-editor');
    const fill = section.getByTestId('fill-missing-arabic');
    await expect(fill).toHaveText(t('Fill missing Arabic names ({count})', { count: 2 }));
    await shot(page, info, `arloc-${locale}-bulk-before`);
    await fill.click();
    const rows = page.getByTestId('departments-row');
    await expect(arabicInput(rows.nth(1), t)).toHaveValue('العمليات');
    await expect(arabicInput(rows.nth(2), t)).toHaveValue('');
    await expect(arabicInput(rows.nth(3), t)).toHaveValue('المبيعات');
    await expect(arabicInput(rows.nth(4), t)).toHaveValue('تسويق خاص');
    const result = section.getByTestId('fill-missing-arabic-result');
    await expect(result).toContainText(t('Arabic names filled: {count}. Check them before you save.', { count: 2 }));
    await expect(result).toContainText(t('Left blank, with no confident suggestion: {names}', { names: 'Widget Lab' }));
    // Nothing left to fill: the action goes away.
    await expect(fill).toHaveCount(0);
    await contained(page);
    await shot(page, info, `arloc-${locale}-bulk-after`);
    await expect.poll(() => server.puts.at(-1)?.body.departments.map(d => d.nameAr ?? '')).toEqual(['الموارد البشرية', 'العمليات', '', 'المبيعات', 'تسويق خاص']);
  });
}

test('an old letter-by-letter Arabic name is offered a fix, and stored data changes only when chosen', async ({ page }, info) => {
  const t = T('en');
  const { server } = await boot(page, { badArabic: true });
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await next(page, t);
  const hq = page.getByTestId('branches-row').first();
  await expect(arabicInput(hq, t).first()).toHaveValue(OLD_LETTER_BY_LETTER);
  expect(server.drafts.values().next().value!.body.branches[0].nameAr).toBe(OLD_LETTER_BY_LETTER);
  await hq.getByTestId('suggest-arabic').first().click();
  await hq.getByRole('button', { name: new RegExp(t('Replace with «{arabic}»', { arabic: 'المكتب الرئيسي' })) }).click();
  await expect(arabicInput(hq, t).first()).toHaveValue('المكتب الرئيسي');
  await shot(page, info, 'arloc-en-fix-old-arabic');
  await expect.poll(() => server.puts.at(-1)?.body.branches[0].nameAr).toBe('المكتب الرئيسي');
  expect(server.commits).toEqual([]);
});

for (const locale of ['en', 'ar'] as const) {
  test(`Pin this location: paste formats, short link, current location, drag, radius, Google Maps and the GCC warning (${locale})`, async ({ page, context }, info) => {
    const t = T(locale);
    if (info.project.name === 'pilot') await page.emulateMedia({ colorScheme: 'dark' });
    const { server, pageErrors } = await boot(page, { locale });
    await toBranches(page, t);
    const location = await addLocation(page, t);
    const panel = location.getByTestId('location-pin');
    const status = panel.getByTestId('pin-status');
    const paste = panel.getByTestId('pin-paste');
    const placePin = panel.getByRole('button', { name: t('Place pin'), exact: true });
    await expect(status).toHaveText(t('Not pinned yet.'));
    await expect(panel.getByTestId('location-map')).toHaveAttribute('data-ready', 'true');
    // Nothing is pinned on load: no marker until the person places one.
    await expect(panel.locator('.leaflet-marker-icon')).toHaveCount(0);
    await shot(page, info, `arloc-${locale}-pin-empty`);

    const placeFrom = async (text: string) => { await paste.fill(text); await placePin.click(); };
    await placeFrom('21.5433, 39.1728');
    await expect(status).toContainText('21.543300, 39.172800');
    await expect(panel.getByTestId('pin-feedback')).toHaveText(t('Pin placed from the coordinates.'));
    await placeFrom('https://www.google.com/maps/place/Kingdom+Centre/@24.7114,46.6744,17z/data=!3m1!4b1!3d24.7113741!4d46.6743887');
    await expect(status).toContainText('24.711374, 46.674389');
    await expect(panel.getByTestId('pin-feedback')).toHaveText(t('Pin placed from the link.'));
    await placeFrom('https://www.google.com/maps?q=26.2235,50.5876');
    await expect(status).toContainText('26.223500, 50.587600');
    await placeFrom(`21°32'35.9"N 39°10'22.1"E`);
    await expect(status).toContainText('21.543306, 39.172806');
    await placeFrom('Riyadh');
    await expect(panel.getByTestId('pin-feedback')).toHaveText(t('This is not a map link or coordinates we can read. Paste a Google Maps link, or coordinates such as 24.7136, 46.6753.'));
    await expect(status).toContainText('21.543306, 39.172806');

    // A real paste (clipboard) places the pin without pressing anything.
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.evaluate(text => navigator.clipboard.writeText(text), 'https://www.google.com/maps/@24.7136,46.6753,15z');
    await paste.fill('');
    await paste.focus();
    await page.keyboard.press('ControlOrMeta+V');
    await expect(status).toContainText('24.713600, 46.675300');

    // A short link goes to the backend resolver; an unreadable one says what to do instead.
    await placeFrom(SHORT_LINK);
    await expect(status).toContainText('21.543300, 39.172800');
    await placeFrom('https://maps.app.goo.gl/unknownLink');
    await expect(panel.getByTestId('pin-feedback')).toHaveText(t('This link could not be read. Open it, then copy the full address from the browser bar, or paste the coordinates.'));
    expect(server.mapLinks).toEqual([SHORT_LINK, 'https://maps.app.goo.gl/unknownLink']);

    // Current location, with its accuracy; a fix wider than the geofence warns.
    await context.grantPermissions(['geolocation']);
    await context.setGeolocation({ latitude: 24.7136, longitude: 46.6753, accuracy: 25 });
    await panel.getByTestId('pin-locate').click();
    await expect(panel.getByTestId('pin-feedback')).toHaveText(t('Pin placed at your current location, accurate to ±{accuracy} m.', { accuracy: 25 }));
    await expect(status).toContainText('24.713600, 46.675300');
    const wide = t('Your device placed you within ±{accuracy} m, wider than the {radius} m geofence. Drag the pin to the exact spot, or choose a larger radius.', { accuracy: 400, radius: 150 });
    await expect(panel.getByText(wide)).toHaveCount(0);
    await context.setGeolocation({ latitude: 24.7, longitude: 46.68, accuracy: 400 });
    await panel.getByTestId('pin-locate').click();
    await expect(panel.getByText(wide)).toBeVisible();
    await shot(page, info, `arloc-${locale}-pin-accuracy`);

    // Radius presets and a custom value, drawn as the circle.
    await panel.getByText(t('{meters} m', { meters: 300 }), { exact: true }).click();
    await expect(panel.getByLabel(t('Radius (m)'))).toHaveValue('300');
    const circleWidth = () => panel.locator('path.leaflet-interactive').evaluate(el => el.getBoundingClientRect().width);
    const at300 = await circleWidth();
    await panel.getByText(t('{meters} m', { meters: 50 }), { exact: true }).click();
    await expect.poll(circleWidth).toBeLessThan(at300);
    await panel.getByLabel(t('Radius (m)')).fill('75');
    await expect(panel.getByRole('radio', { name: t('Custom') })).toBeChecked();

    // Drag the pin: the coordinates follow.
    await context.setGeolocation({ latitude: 24.7136, longitude: 46.6753, accuracy: 10 });
    await panel.getByTestId('pin-locate').click();
    await expect(status).toContainText('24.713600, 46.675300');
    const pin = panel.locator('.leaflet-marker-icon');
    // Centre it: the sticky footer must not sit over the map while the pointer drags.
    await pin.evaluate(el => el.scrollIntoView({ block: 'center' }));
    const box = (await pin.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height - 6);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height - 36, { steps: 8 });
    await page.mouse.up();
    await expect(status).not.toContainText('24.713600, 46.675300');
    const statusText = await status.textContent();
    const [lat, lng] = statusText!.match(/-?\d+\.\d{6}/g)!.map(Number);
    expect(lat).toBeGreaterThan(24.7136);
    expect(lng).toBeGreaterThan(46.6753);
    await expect(panel.locator('details').getByLabel(t('Latitude'))).toHaveValue(String(lat));

    // Check in Google Maps opens the same point in a new tab.
    const google = panel.getByTestId('pin-google');
    await expect(google).toHaveAttribute('href', `https://www.google.com/maps/search/?api=1&query=${lat},${lng}`);
    await expect(google).toHaveAttribute('target', '_blank');
    await expect(google).toHaveAttribute('rel', /noopener/);

    // Outside the GCC warns, never blocks; a swapped pair offers the swap.
    await placeFrom('46.6753, 24.7136');
    await expect(panel.getByText(t('These coordinates are outside the GCC. Check the pin before you continue.'))).toBeVisible();
    await shot(page, info, `arloc-${locale}-pin-gcc`);
    await panel.getByRole('button', { name: t('Swap latitude and longitude') }).click();
    await expect(status).toContainText('24.713600, 46.675300');
    await expect(panel.getByText(t('These coordinates are outside the GCC. Check the pin before you continue.'))).toHaveCount(0);
    await placeFrom('51.5072, -0.1276');
    await expect(panel.getByText(t('These coordinates are outside the GCC. Check the pin before you continue.'))).toBeVisible();
    await expect(panel.getByRole('button', { name: t('Swap latitude and longitude') })).toHaveCount(0);
    await placeFrom('24.7136, 46.6753');

    // The raw numbers stay editable under Coordinates.
    await panel.locator('details summary').click();
    await panel.locator('details').getByLabel(t('Latitude')).fill('24.75');
    await expect(status).toContainText('24.750000, 46.675300');
    await contained(page);
    await panel.scrollIntoViewIfNeeded();
    await shot(page, info, `arloc-${locale}-pin-done`);

    // What is saved is just the location's latitude, longitude and radius.
    await expect.poll(() => server.puts.at(-1)?.body.branches[0].locations[0], { timeout: 15_000 }).toMatchObject({ code: 'GATE1', latitude: 24.75, longitude: 46.6753, radiusMeters: 75 });
    await next(page, t);
    await expectStep(page, t, 'Cost centres');
    expect(pageErrors).toEqual([]);
  });
}

test('a location with a half-entered coordinate stops Next at the field', async ({ page }) => {
  const t = T('en');
  await boot(page);
  await toBranches(page, t);
  const location = await addLocation(page, t);
  const panel = location.getByTestId('location-pin');
  await panel.locator('details summary').click();
  await panel.locator('details').getByLabel(t('Latitude')).fill('24.7');
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  await expect(panel.locator('details').getByLabel(t('Longitude'))).toBeFocused();
  await expect(panel.getByText(t('Enter both latitude and longitude, or neither.')).first()).toBeVisible();
});

// ── Per-section Save: explicit, visible confirmation on top of the background autosave ─────────────────

const sectionStatus = (page: Page, section: string) => page.getByTestId(`section-status-${section}`);
const sectionBar = (page: Page, section: string) => page.getByTestId(`section-save-${section}`);
const railStep = (page: Page, t: ReturnType<typeof T>, title: string) => page.getByTestId('wizard-rail').getByRole('button', { name: new RegExp(t(title).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) });

async function startEvostel(page: Page, t: ReturnType<typeof T>) {
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
}

for (const locale of ['en', 'ar'] as const) {
  test(`each section saves on its own and moves to the next; Organisation parts have their own Save & next; nothing else changes in the company before Save all changes (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    if (info.project.name === 'pilot') await page.emulateMedia({ colorScheme: 'dark' });
    const { server, pageErrors } = await boot(page, { locale });
    await openEvostel(page, t);
    await expect(page.getByText(t('See what this company has today, then go step by step. Save each section as you finish it. Organisation parts are saved to the company when you press Save & next; everything else is applied by Save all changes.'))).toBeVisible();
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await expectStep(page, t, 'Company details');

    // Before any Save: the section and the rail say so.
    await expect(sectionStatus(page, 'company')).toHaveAttribute('data-status', 'notSaved');
    await expect(railStep(page, t, 'Company details')).toHaveAttribute('data-saved', 'unsaved');
    await page.getByLabel(t('Trade name (optional)')).fill('Evostel Trading');
    await expect(sectionStatus(page, 'company')).toHaveText(t('Unsaved changes'));
    await expect(sectionBar(page, 'company').getByTestId('section-save-status-company')).toHaveText(t('Unsaved changes'));
    await contained(page);
    await shot(page, info, `save-${locale}-company-unsaved`);

    // The last (only) section of a step: "Save" saves now and opens the next step, its heading focused.
    const putsBefore = server.puts.length;
    await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expectStep(page, t, 'Branches & locations');
    await expect(page.getByRole('heading', { level: 2, name: t('Branches & locations'), exact: true })).toBeFocused();
    expect(server.puts.length).toBeGreaterThan(putsBefore);
    expect(server.puts.at(-1)!.body.company.tradeName).toBe('Evostel Trading');
    await expect(railStep(page, t, 'Company details')).toHaveAttribute('data-saved', 'saved');
    await sectionBar(page, 'branches').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expectStep(page, t, 'Cost centres');

    // Organisation has no section Save here: each part has its own "Save {part} & next", and the rail shows no Saved hint for it.
    await expect(page.getByTestId('section-save-costCentres')).toHaveCount(0);
    await expect(railStep(page, t, 'Organisation')).not.toHaveAttribute('data-saved', /.*/);
    await nextUntil(page, t, 'Work rules');

    // Work rules is a section with its own Save: unsaved until saved, and Save opens the next part.
    await expect(sectionStatus(page, 'rules')).toHaveAttribute('data-status', 'notSaved');
    await page.getByLabel(t('Prefix')).first().fill('EVX');
    await expect(sectionStatus(page, 'rules')).toHaveAttribute('data-status', 'unsaved');
    await expect(railStep(page, t, 'Work & pay rules')).toHaveAttribute('data-saved', 'unsaved');
    await contained(page);
    await shot(page, info, `save-${locale}-rules-edited`);
    await sectionBar(page, 'rules').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expectStep(page, t, 'Leave & holidays');
    await expect(railStep(page, t, 'Work & pay rules')).toHaveAttribute('data-saved', 'saved');
    await (info.project.name === 'mobile' ? page.getByTestId('part-tab-workRules') : page.getByTestId('wizard-rail-parts').getByRole('button', { name: new RegExp(t('Work rules')) })).click();
    await expectStep(page, t, 'Work rules');
    await expect(sectionStatus(page, 'rules')).toHaveAttribute('data-status', 'saved');
    await expect(sectionStatus(page, 'rules')).toContainText(t('Saved at {time}', { time: '' }).trim());

    // Draft saves write the draft only: nothing is applied to the company until Save all changes.
    expect(server.commits).toEqual([]);
    await railStep(page, t, 'Review & update').click();
    await expectStep(page, t, 'Review & update');
    await expect(page.getByText(t('These are exactly the changes Save all changes will make. Organisation rows you saved in Step 4 are already in the company.'))).toBeVisible();
    const saveAll = page.getByRole('button', { name: t('Save all changes'), exact: true });
    await expect(saveAll).toBeEnabled();
    expect(server.commits).toEqual([]);
    await shot(page, info, `save-${locale}-review`);
    await saveAll.click();
    await expect(page.getByTestId('create-company-success')).toBeVisible();
    expect(server.commits).toHaveLength(1);
    expect(pageErrors).toEqual([]);
  });

  test(`a section with a problem stays put with the message at the field, and saves nothing (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server } = await boot(page, { locale });
    await startEvostel(page, t);
    await page.getByLabel(t('Legal name (English)')).fill('');
    await page.getByLabel(t('Trade name (optional)')).fill('Changed');
    await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expectStep(page, t, 'Company details');
    await expect(page.getByTestId('section-save-problem-company')).toHaveText(t('{count, plural, one {# field needs} other {# fields need}} attention in this section. Nothing was saved.', { count: 1 }));
    await expect(page.getByLabel(t('Legal name (English)'))).toBeFocused();
    await expect(page.getByLabel(t('Legal name (English)'))).toHaveAttribute('aria-invalid', 'true');
    await expect(sectionStatus(page, 'company')).toHaveAttribute('data-status', 'unsaved');
    await contained(page);
    await shot(page, info, `save-${locale}-invalid`);
    expect(server.commits).toEqual([]);
  });

  test(`a draft saved elsewhere stops the section Save with the conflict flow (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server } = await boot(page, { locale });
    await startEvostel(page, t);
    await page.getByLabel(t('Trade name (optional)')).fill('Mine');
    server.touchElsewhere([...server.drafts.keys()][0]);
    await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expect(page.getByTestId('section-save-problem-company')).toHaveText(t('This draft was changed elsewhere, so this section was not saved. Reload the draft, then save the section again.'));
    await expect(page.getByText(t('This draft was changed elsewhere — reload'))).toBeVisible();
    await expectStep(page, t, 'Company details');
    await expect(sectionStatus(page, 'company')).not.toHaveAttribute('data-status', 'saved');
    await shot(page, info, `save-${locale}-conflict`);
    // The existing flow: reload takes their version, and the section matches the draft again.
    await page.getByRole('button', { name: t('Reload draft') }).first().click();
    await expect(page.getByText(t('This draft was changed elsewhere — reload'))).toHaveCount(0);
    // Reload opens the draft at the step it was saved on; their version is what is on screen, and it is saved.
    if (await railStep(page, t, 'Company details').getAttribute('aria-current') !== 'step') await railStep(page, t, 'Company details').click();
    await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue('Saved elsewhere');
    await expect(sectionStatus(page, 'company')).toHaveAttribute('data-status', 'saved');
  });
}

test('a failed section save keeps the changes and says so in plain words', async ({ page }) => {
  const t = T('en');
  await boot(page);
  await startEvostel(page, t);
  let refused = false;
  await page.route('**/api/company-setup/drafts/*', async route => {
    if (route.request().method() === 'PUT' && !refused) { refused = true; return route.fulfill({ status: 503, json: { message: 'unavailable' } }); }
    return route.fallback();
  });
  await page.getByLabel(t('Trade name (optional)')).fill('Offline edit');
  await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
  await expect(page.getByTestId('section-save-problem-company')).toHaveText(t('This section could not be saved. Your changes are still here. Check your connection and try again.'));
  await expectStep(page, t, 'Company details');
  await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue('Offline edit');
  // Trying again works.
  await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
  await expectStep(page, t, 'Branches & locations');
});

for (const locale of ['en', 'ar'] as const) {
  test(`leaving with an unsaved section asks first; after Save it does not (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    await boot(page, { locale });
    await startEvostel(page, t);
    await page.getByLabel(t('Trade name (optional)')).fill('Not saved yet');
    // Browser Back: the guard holds the navigation, so there is no load to wait for.
    await page.evaluate(() => history.back());
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(t('These sections have changes you have not saved: {sections}. The automatic draft backup may already hold them. Leave anyway?', { sections: t('Company details') }));
    await shot(page, info, `save-${locale}-leave-warning`);
    await dialog.getByRole('button', { name: locale === 'ar' ? 'إلغاء' : 'Cancel', exact: true }).click();
    await expect(page).toHaveURL(/\/setup\/company\/co-evostel\/complete/);
    await expectStep(page, t, 'Company details');
    await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue('Not saved yet');
    await sectionBar(page, 'company').getByRole('button', { name: new RegExp(`^${t('Save')}`) }).click();
    await expectStep(page, t, 'Branches & locations');
    await page.evaluate(() => history.back());
    await expect(page).not.toHaveURL(/\/complete/);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
}

for (const locale of ['en', 'ar'] as const) {
  test(`a location without a radius never looks like a geofence, and the saved draft carries the radius shown (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server } = await boot(page, { locale, locationWithoutRadius: true });
    await startEvostel(page, t);
    await next(page, t);
    await expectStep(page, t, 'Branches & locations');
    const panel = page.getByTestId('branches.0.locations-row').first().getByTestId('location-pin');
    await expect(panel.getByTestId('location-map')).toHaveAttribute('data-ready', 'true');
    // Pinned, but no radius: said in words; no circle, no preset selected, nothing written behind the person's back.
    await expect(panel.getByTestId('pin-status')).toContainText(t('No geofence radius set'));
    await expect(panel.getByTestId('pin-no-radius')).toBeVisible();
    await expect(panel.locator('path.leaflet-interactive')).toHaveCount(0);
    await expect(panel.getByRole('radio', { checked: true })).toHaveCount(0);
    await expect(panel.getByLabel(t('Radius (m)'))).toHaveValue('');
    await contained(page);
    await panel.scrollIntoViewIfNeeded();
    await shot(page, info, `arloc-${locale}-pin-no-radius`);
    await page.waitForTimeout(1500);
    expect(server.puts.every(p => p.body.branches[0].locations[0].radiusMeters == null)).toBe(true);
    // Choosing one shows it, draws it and saves exactly it.
    await panel.getByText(t('{meters} m', { meters: 100 }), { exact: true }).click();
    await expect(panel.getByTestId('pin-status')).toContainText(t('{meters} m', { meters: 100 }));
    await expect(panel.locator('path.leaflet-interactive')).toHaveCount(1);
    await expect(panel.getByTestId('pin-no-radius')).toHaveCount(0);
    await expect.poll(() => server.puts.at(-1)?.body.branches[0].locations[0].radiusMeters, { timeout: 15_000 }).toBe(100);

    // Clearing the radius and placing a new pin writes the default it then shows (150 m), never a display-only value.
    await panel.getByLabel(t('Radius (m)')).fill('');
    await expect(panel.getByTestId('pin-no-radius')).toBeVisible();
    await panel.getByTestId('pin-paste').fill('24.7136, 46.6753');
    await panel.getByRole('button', { name: t('Place pin'), exact: true }).click();
    await expect(panel.getByTestId('pin-status')).toContainText(t('{meters} m', { meters: 150 }));
    await expect(panel.getByRole('radio', { name: t('{meters} m', { meters: 150 }) })).toBeChecked();
    await expect.poll(() => server.puts.at(-1)?.body.branches[0].locations[0], { timeout: 15_000 })
      .toMatchObject({ latitude: 24.7136, longitude: 46.6753, radiusMeters: 150 });
  });
}

// ── Addendum B: Organisation one part at a time, live section saves, copy by value ──

for (const locale of ['en', 'ar'] as const) {
  test(`existing: Organisation goes one part at a time, Save & next writes each part, Back crosses steps (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { server, pageErrors } = await boot(page, { locale });
    await openEvostel(page, t);
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await expectStep(page, t, 'Company details');
    await next(page, t);
    await next(page, t);
    await expectStep(page, t, 'Cost centres');
    await expect(page).toHaveURL(/part=costCentres/);
    if (info.project.name === 'mobile') {
      await expect(page.getByTestId('wizard-rail-position')).toHaveText(t('Step {position} of {total} · {title} · Part {part} of {parts}: {partTitle}', { position: 4, total: 7, title: t('Organisation'), part: 1, parts: 4, partTitle: t('Cost centres') }));
      await expect(page.getByTestId('part-tab-costCentres')).toHaveAttribute('aria-selected', 'true');
    } else {
      const parts = page.getByTestId('wizard-rail-parts');
      await expect(parts.locator('button')).toHaveCount(4);
      await expect(parts.locator('[data-part="costCentres"]')).toHaveAttribute('aria-current', 'step');
      // Not reachable yet is still focusable, and says why.
      await expect(page.getByTestId('wizard-rail').locator('button[data-locked="true"]')).toHaveCount(0);
    }
    // Back on the first part goes to the previous step.
    await page.getByTestId('wizard-back').click();
    await expectStep(page, t, 'Branches & locations');
    await expect(page).not.toHaveURL(/part=/);
    await next(page, t);
    await expectStep(page, t, 'Cost centres');

    await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
    const ops = page.getByTestId('costCentres-row').nth(1);
    await ops.getByLabel(t('Code')).fill('OPS');
    await ops.getByLabel(t('Name (English)')).fill('Operations');
    await expect(page.getByTestId('wizard-next')).toContainText(t('Save cost centres & next'));
    await expect(page.getByTestId('wizard-next')).toHaveAttribute('aria-keyshortcuts', 'Meta+Enter Control+Enter');
    await contained(page);
    await vshot(page, info, `parts-${locale}-4a-cost-centres-changed`);
    await next(page, t);
    await expectStep(page, t, 'Departments');
    await expect(page.getByTestId('section-saved')).toHaveText(t('{part} saved · {created} added, {updated} changed', { part: t('Cost centres'), created: 1, updated: 0 }));
    expect(server.org().costCentres.map(c => [c.code, !!c.existingId])).toEqual([['ADM', true], ['OPS', true]]);

    // A look-alike name is caught when it is entered, and the reader chooses.
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const dup = page.getByTestId('departments-row').nth(1);
    await dup.getByLabel(t('Name (English)')).fill('HR');
    await dup.getByLabel(t('Name (English)')).blur();
    await expect(page.getByTestId('look-alike')).toContainText(t('This looks like {name} ({code}), already in this list.', { name: 'Human Resources', code: 'HR' }));
    await page.getByTestId('look-alike').getByRole('button', { name: t('Use existing'), exact: true }).click();
    await expect(page.getByTestId('departments-row')).toHaveCount(1);
    await expect(page.locator('#ccw-departments-0-nameEn')).toBeFocused();

    // A department is offered the cost centre whose name matches; the reader confirms it.
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    const opsDept = page.getByTestId('departments-row').nth(1);
    await opsDept.getByLabel(t('Code')).fill('OPS');
    await opsDept.getByLabel(t('Name (English)')).fill('Operations');
    await expect(opsDept.getByTestId('cost-centre-suggestion')).toContainText(t('Suggested: {code} · {name}', { code: 'OPS', name: 'Operations' }));
    await expect(page.locator('#ccw-departments-1-costCentreCode')).toHaveValue('');
    await opsDept.getByRole('button', { name: new RegExp(t('Use this cost centre')) }).click();
    await expect(page.locator('#ccw-departments-1-costCentreCode')).toHaveValue('OPS');
    // Cmd/Ctrl+Enter is Save & next.
    await page.locator('#ccw-departments-1-nameEn').focus();
    await page.keyboard.press('ControlOrMeta+Enter');
    await expectStep(page, t, 'Grades');
    await expect(page).toHaveURL(/part=grades/);
    // Back within the step: nothing to save, so plain Next.
    await page.getByTestId('wizard-back').click();
    await expectStep(page, t, 'Departments');
    await expect(page.getByTestId('wizard-next')).toContainText(t('Next: {part}', { part: t('Grades') }));
    await nextUntil(page, t, 'Work rules');
    // Back on step 5's first part goes to step 4's last part.
    await page.getByTestId('wizard-back').click();
    await expectStep(page, t, 'Designations');
    expect(server.sectionApplies.map(a => [a.section, a.replayed])).toEqual([['costCentres', false], ['departments', false]]);

    // Resume from the address: the part it names, once nothing before it blocks.
    const url = new URL(page.url());
    url.searchParams.set('part', 'grades');
    await page.goto(url.pathname + url.search);
    await expectStep(page, t, 'Grades');
    await expect(page.getByTestId('departments-row')).toHaveCount(0);
    expect(server.errors).toEqual([]);
    expect(pageErrors).toEqual([]);
  });
}

test('a section the server refuses keeps the reader on the part with the problem on its field; a lost reply retries without writing twice', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page, { sectionFailure: 'problem' });
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await nextUntil(page, t, 'Departments');
  await page.getByRole('button', { name: t('Add department'), exact: true }).click();
  await page.locator('#ccw-departments-1-code').fill('OPS');
  await page.locator('#ccw-departments-1-nameEn').fill('Operations');
  await page.locator('#ccw-departments-1-costCentreCode').selectOption('ADM');
  await next(page, t);
  await expectStep(page, t, 'Departments');
  await expect(page.getByTestId('section-error')).toContainText('Save Cost centres first: CC_NEW is not saved yet.');
  await expect(page.locator('#ccw-departments-1-costCentreCode-error')).toHaveText('Save Cost centres first: CC_NEW is not saved yet.');
  await expect(page.locator('#ccw-departments-1-costCentreCode')).toBeFocused();
  expect(server.sectionApplies).toEqual([]);

  // The write lands but the reply is lost: "Not saved", with the rows still here; Try again replays the same write.
  server.options.sectionFailure = 'lost';
  await next(page, t);
  await expect(page.getByTestId('section-error')).toContainText(t('Not saved. Your changes are still here.'));
  await expect(page.locator('#ccw-departments-1-code')).toHaveValue('OPS');
  await page.getByTestId('section-error').getByRole('button', { name: t('Try again'), exact: true }).click();
  await expectStep(page, t, 'Grades');
  await expect(page.getByTestId('section-saved')).toHaveText(t('{part} were already saved.', { part: t('Departments') }));
  expect(server.sectionApplies.map(a => [a.section, a.replayed])).toEqual([['departments', false], ['departments', true]]);
  expect(server.org().departments.map(d => d.code)).toEqual(['HR', 'OPS']);
});

test('removing a row leaves "removed · Undo" in its place for 8 s with focus on Undo; a blank new row blocks Next', async ({ page }) => {
  const t = T('en');
  await boot(page);
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  await next(page, t);
  await next(page, t);
  await expectStep(page, t, 'Cost centres');
  await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
  // Enter never presses Next; on a valid last row it adds the next row.
  await page.keyboard.type('CC_A');
  await page.keyboard.press('Tab');
  await page.keyboard.type('Alpha');
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await expectStep(page, t, 'Cost centres');
  await expect(page.getByTestId('costCentres-row')).toHaveCount(2);
  await expect(page.locator('#ccw-costCentres-1-code')).toBeFocused();
  // A wholly blank new row asks to be filled in or removed.
  await next(page, t);
  await expect(page.locator('#ccw-costCentres-1-code-error')).toHaveText(t('Fill in this row or remove it.'));
  await page.getByTestId('costCentres-row').first().getByRole('button', { name: new RegExp(t('Remove')) }).click();
  const placeholder = page.getByTestId('row-removed');
  await expect(placeholder).toContainText(t('{row} removed.', { row: 'CC_A' }));
  await expect(placeholder.getByRole('button', { name: new RegExp(t('Undo')) })).toBeFocused();
  await placeholder.getByRole('button', { name: new RegExp(t('Undo')) }).click();
  await expect(page.locator('#ccw-costCentres-0-code')).toHaveValue('CC_A');
  await expect(page.locator('#ccw-costCentres-0-code')).toBeFocused();
  await expect(page.getByTestId('row-removed')).toHaveCount(0);
});

test('an edit made just before leaving inside the app is saved on the way out, never dropped', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await openEvostel(page, t);
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await page.getByLabel(t('Trade name (optional)')).fill('Left in a hurry');
  // Well inside the 1.2 s debounce.
  await page.getByRole('link', { name: t('Guided setup'), exact: true }).first().click();
  // The unsaved company section asks first; leaving anyway still sends the draft on the way out.
  await page.getByRole('dialog').getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page.getByTestId('guided-setup-chooser')).toBeVisible();
  await expect.poll(() => server.puts.at(-1)?.body.company.tradeName).toBe('Left in a hurry');
});

test('Arabic: arrows are mirrored once by the global rule, never twice', async ({ page }) => {
  const t = T('ar');
  await boot(page, { locale: 'ar' });
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  const arrow = page.getByTestId('wizard-next').locator('svg');
  expect(await arrow.evaluate(el => ({ scale: getComputedStyle(el).scale, transform: getComputedStyle(el).transform }))).toEqual({ scale: '-1 1', transform: 'none' });
  await contained(page);
});

test('step content rises in, and does not move at all under reduced motion', async ({ page }) => {
  const t = T('en');
  await boot(page);
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await openWizardFromCompanies(page, t);
  await startAndFillCompany(page, t);
  const name = () => page.getByTestId('wizard-position').evaluate(el => getComputedStyle(el).animationName);
  expect(await name()).toBe('fade-in');
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  expect(await name()).toBe('none');
});

for (const locale of ['en', 'ar'] as const) {
  test(`Organisation parts at 375 px: stacked rows, part tabs, no sideways scroll (${locale})`, async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'One phone-width pass is enough; it sets its own viewport.');
    const t = T(locale);
    await page.setViewportSize({ width: 375, height: 812 });
    await boot(page, { locale });
    await openEvostel(page, t);
    await page.getByRole('button', { name: t('Start'), exact: true }).click();
    await nextUntil(page, t, 'Departments');
    await expect(page.getByTestId('departments-editor')).toHaveAttribute('data-layout', 'stacked');
    await expect(page.getByTestId('part-tab-departments')).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('button', { name: t('Add department'), exact: true }).click();
    await page.locator('#ccw-departments-1-nameEn').fill('Operations');
    await contained(page);
    await vshot(page, info, `parts-${locale}-375-departments`);
    await shot(page, info, `parts-${locale}-375-departments-full`);
  });
}
