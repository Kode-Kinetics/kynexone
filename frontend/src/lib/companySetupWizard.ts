import { msg, type MessageParams } from '../i18n/translations';
import { isValidEmailDomain } from '../api/organization';
import type {
  CodeName, CompanySetupContext, CompanySetupDraftBody, CompanySetupPlan, PlanArea, PlanItem, Problem,
} from '../api/companySetup';

/**
 * The create-company wizard's rules, as pure functions so they are unit-tested without a browser
 * (unit/companySetupWizard.spec.ts). The server re-checks everything on dry-run and commit; these
 * only stop Next on a `*` field and say, on that field, what to enter.
 */

export type StepId = 1 | 2 | 3 | 4 | 5 | 6 | 7;
export const STEP_IDS: StepId[] = [1, 2, 3, 4, 5, 6, 7];

export const WIZARD_STEPS: Record<StepId, { title: string; description: string }> = {
  1: { title: msg('Start'), description: msg('Plan, drafts and starting point') },
  2: { title: msg('Company details'), description: msg('Legal and registration details') },
  3: { title: msg('Branches & locations'), description: msg('Where people work') },
  4: { title: msg('Organisation'), description: msg('Departments, roles and grades') },
  5: { title: msg('Work & pay rules'), description: msg('Week, holidays, leave, numbering') },
  6: { title: msg('Company super admin'), description: msg('Who runs this company') },
  7: { title: msg('Review & create'), description: msg('Check the plan, then create') },
};

/** Which wizard step each planned area belongs to, so the review groups the plan the way it was entered. */
export const AREA_STEP: Record<PlanArea, StepId> = {
  company: 2, cutover: 2,
  branch: 3, location: 3,
  costCentre: 4, department: 4, designation: 4, grade: 4,
  employeeIdRule: 5, holidayCalendar: 5, leavePolicy: 5, gccSetting: 5, workWeek: 5,
  companyAdmin: 6,
};

export const AREA_LABEL: Record<PlanArea, string> = {
  company: msg('Company'), cutover: msg('First payroll month'),
  branch: msg('Branch'), location: msg('Location'),
  costCentre: msg('Cost centre'), department: msg('Department'), designation: msg('Designation'), grade: msg('Grade'),
  employeeIdRule: msg('Employee numbering'), holidayCalendar: msg('Holiday calendar'), leavePolicy: msg('Leave policy'),
  gccSetting: msg('End-of-service settings'), workWeek: msg('Working week'), companyAdmin: msg('Company super admin'),
};

export interface FieldError { field: string; message: string; params?: MessageParams }

const SAUDI = new Set(['SA', 'SAU', 'KSA']);
export const isSaudi = (country?: string | null) => SAUDI.has((country ?? '').trim().toUpperCase());

/** Pack currency per country. SA is locked to SAR (contract step 2). */
export const COUNTRY_CURRENCY: Record<string, string> = {
  SA: 'SAR', AE: 'AED', QA: 'QAR', KW: 'KWD', BH: 'BHD', OM: 'OMR', EG: 'EGP', IN: 'INR', PK: 'PKR', GB: 'GBP', US: 'USD',
};
export const CURRENCIES = ['SAR', 'AED', 'QAR', 'KWD', 'BHD', 'OMR', 'EGP', 'INR', 'PKR', 'USD', 'EUR', 'GBP'];
export const TIME_ZONES = ['Asia/Riyadh', 'Asia/Dubai', 'Asia/Qatar', 'Asia/Kuwait', 'Asia/Bahrain', 'Asia/Muscat', 'Africa/Cairo', 'Asia/Kolkata', 'Asia/Karachi', 'Europe/London', 'America/New_York', 'UTC'];
export const WEEKEND_OPTIONS: { value: string; label: string }[] = [
  { value: 'Fri,Sat', label: msg('Friday and Saturday') },
  { value: 'Sat,Sun', label: msg('Saturday and Sunday') },
  { value: 'Fri', label: msg('Friday only') },
  { value: 'Sun', label: msg('Sunday only') },
];

/** The 1st of next month, as "YYYY-MM" — the contract's default first payroll month. */
export function defaultFirstPayrollMonth(today: Date = new Date()): string {
  const year = today.getFullYear() + (today.getMonth() === 11 ? 1 : 0);
  const month = (today.getMonth() + 1) % 12 + 1;
  return `${year}-${String(month).padStart(2, '0')}`;
}

/** A body that satisfies the type before the server's defaults arrive. Never sent as-is. */
export function emptyBody(countryCode = ''): CompanySetupDraftBody {
  return {
    startFrom: 'defaults',
    company: { legalNameEn: '', legalNameAr: '', countryCode, currency: COUNTRY_CURRENCY[countryCode] ?? '', registrationNumber: '', firstPayrollMonth: defaultFirstPayrollMonth() },
    branches: [], costCentres: [], departments: [], designations: [], grades: [],
    rules: {
      workWeek: { weekendDays: 'Fri,Sat', override: false },
      holidayCalendar: { usePack: true },
      leavePolicies: { usePack: true },
      employeeNumbering: { prefix: 'EMP', useCountryPrefix: false, useBranchPrefix: false, useDepartmentPrefix: false, useYear: false, paddingLength: 4, nextSequence: 1 },
    },
    companyAdmin: { mode: 'groupOnly' },
  };
}

const blank = (value?: string | null) => !(value ?? '').trim();
const norm = (code?: string | null) => (code ?? '').trim().toUpperCase();
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const MOL = /^[0-9]+(-[0-9]+)?$/;

/** Mirrors backend KsaWageFileRules.ValidateEstablishmentId. */
export function molEstablishmentError(value?: string | null): string | null {
  const v = (value ?? '').trim();
  if (!v) return msg('Enter the MOL establishment number shown in Qiwa.');
  const digits = v.replace(/[^0-9]/g, '');
  if (v.length < 2 || v.length > 15 || !MOL.test(v) || digits.length < 2)
    return msg('The MOL establishment number is the labour-office number and sequence number, digits only, optionally joined by one hyphen (for example 7-1234567).');
  if (/^0+$/.test(digits)) return msg('The MOL establishment number is all zeros, which is a placeholder. Enter the number shown in Qiwa.');
  return null;
}

/** True when a code is already in the tenant's shared catalog: it is reused, never created or edited. */
export function inCatalog(catalog: readonly CodeName[] | undefined, code?: string | null): CodeName | undefined {
  const c = norm(code);
  return c ? catalog?.find(item => norm(item.code) === c) : undefined;
}

function duplicateCodes<T>(rows: readonly T[], code: (row: T) => string | undefined, path: string, errors: FieldError[]) {
  const seen = new Map<string, number>();
  rows.forEach((row, index) => {
    const c = norm(code(row));
    if (!c) return;
    if (seen.has(c)) errors.push({ field: `${path}.${index}.code`, message: msg('This code is already used in this list. Give each row its own code.') });
    else seen.set(c, index);
  });
}

export interface ValidationInput {
  body: CompanySetupDraftBody;
  context?: CompanySetupContext | null;
  /** Step 6 only applies when the viewer can grant the company super admin. */
  adminStepVisible?: boolean;
}

/** Field-level blockers for one step. Empty means Next is allowed. */
export function validateStep(step: StepId, { body, context, adminStepVisible = true }: ValidationInput): FieldError[] {
  const errors: FieldError[] = [];
  const c = body.company;
  const saudi = isSaudi(c.countryCode);
  switch (step) {
    case 1:
      if (body.startFrom === 'copy' && blank(body.copyFromCompanyId)) errors.push({ field: 'copyFromCompanyId', message: msg('Choose the company to copy from.') });
      break;
    case 2: {
      if (blank(c.legalNameEn)) errors.push({ field: 'company.legalNameEn', message: msg('Enter the legal name in English, as it appears on the commercial registration.') });
      if (saudi && blank(c.legalNameAr)) errors.push({ field: 'company.legalNameAr', message: msg('Enter the legal name in Arabic. Saudi companies are registered with an Arabic name.') });
      if (blank(c.countryCode)) errors.push({ field: 'company.countryCode', message: msg('Choose the country the company is registered in.') });
      if (blank(c.currency)) errors.push({ field: 'company.currency', message: msg('Choose the currency salaries are paid in.') });
      else if (saudi && c.currency !== 'SAR') errors.push({ field: 'company.currency', message: msg('Saudi companies pay salaries in SAR.') });
      if (blank(c.registrationNumber)) errors.push({ field: 'company.registrationNumber', message: msg('Enter the commercial registration (CR) number.') });
      else if (saudi && !/^[0-9]{10}$/.test(c.registrationNumber.trim())) errors.push({ field: 'company.registrationNumber', message: msg('A Saudi CR number is 10 digits. Check the number on the CR certificate.') });
      if (saudi) {
        const mol = molEstablishmentError(c.molEstablishmentNumber);
        if (mol) errors.push({ field: 'company.molEstablishmentNumber', message: mol });
      }
      if (!blank(c.unifiedNationalNumber) && !/^7[0-9]{9}$/.test((c.unifiedNationalNumber ?? '').trim()))
        errors.push({ field: 'company.unifiedNationalNumber', message: msg('The Unified National Number is 10 digits starting with 7. Leave it blank if you do not have it yet.') });
      if (!isValidEmailDomain(c.emailDomain ?? '')) errors.push({ field: 'company.emailDomain', message: msg('Use a bare domain such as acme.sa, without "@" or "https://". Leave it blank if the company has none.') });
      if (!/^[0-9]{4}-(0[1-9]|1[0-2])$/.test(c.firstPayrollMonth ?? '')) errors.push({ field: 'company.firstPayrollMonth', message: msg('Choose the first month you will run payroll in KynexOne.') });
      break;
    }
    case 3: {
      if (body.branches.length === 0) errors.push({ field: 'branches', message: msg('Add at least one branch. Every company needs a head office.') });
      const heads = body.branches.filter(b => b.isHeadOffice).length;
      if (body.branches.length > 0 && heads !== 1) errors.push({ field: 'branches', message: msg('Mark exactly one branch as the head office.') });
      body.branches.forEach((b, i) => {
        if (blank(b.code)) errors.push({ field: `branches.${i}.code`, message: msg('Enter a branch code.') });
        if (blank(b.nameEn)) errors.push({ field: `branches.${i}.nameEn`, message: msg('Enter the branch name in English.') });
        if (blank(b.city)) errors.push({ field: `branches.${i}.city`, message: msg('Enter the city.') });
        b.locations.forEach((l, j) => {
          if (blank(l.code)) errors.push({ field: `branches.${i}.locations.${j}.code`, message: msg('Enter a location code.') });
          if (blank(l.nameEn)) errors.push({ field: `branches.${i}.locations.${j}.nameEn`, message: msg('Enter the location name in English.') });
        });
        duplicateCodes(b.locations, l => l.code, `branches.${i}.locations`, errors);
      });
      duplicateCodes(body.branches, b => b.code, 'branches', errors);
      break;
    }
    case 4: {
      const rowBasics = (rows: { code: string; name?: string }[], path: string, nameField: string, catalog?: readonly CodeName[]) => {
        rows.forEach((row, i) => {
          if (blank(row.code)) errors.push({ field: `${path}.${i}.code`, message: msg('Enter a code.') });
          // A reused catalog row takes its names from the catalog; nothing to type.
          else if (inCatalog(catalog, row.code)) return;
          if (blank(row.name)) errors.push({ field: `${path}.${i}.${nameField}`, message: msg('Enter the name in English.') });
        });
        duplicateCodes(rows, r => r.code, path, errors);
      };
      const shared = context?.sharedCatalog;
      rowBasics(body.costCentres.map(r => ({ code: r.code, name: r.nameEn })), 'costCentres', 'nameEn');
      rowBasics(body.departments.map(r => ({ code: r.code, name: r.nameEn })), 'departments', 'nameEn', shared?.departments);
      rowBasics(body.designations.map(r => ({ code: r.code, name: r.titleEn })), 'designations', 'titleEn', shared?.designations);
      rowBasics(body.grades.map(r => ({ code: r.code, name: r.nameEn })), 'grades', 'nameEn', shared?.grades);
      const centres = new Set(body.costCentres.map(cc => norm(cc.code)));
      body.departments.forEach((d, i) => {
        if (!inCatalog(context?.sharedCatalog.departments, d.code) && d.costCentreCode && !centres.has(norm(d.costCentreCode)))
          errors.push({ field: `departments.${i}.costCentreCode`, message: msg('Choose a cost centre from the list on this step.') });
      });
      body.grades.forEach((g, i) => {
        if (inCatalog(context?.sharedCatalog.grades, g.code)) return;
        if (!(g.level >= 1)) errors.push({ field: `grades.${i}.level`, message: msg('Enter a level of 1 or more.') });
        if (!(g.minSalary >= 0) || !(g.minSalary <= g.midSalary && g.midSalary <= g.maxSalary))
          errors.push({ field: `grades.${i}.maxSalary`, message: msg('Salaries must rise from minimum to midpoint to maximum.') });
      });
      break;
    }
    case 5: {
      const n = body.rules.employeeNumbering;
      if (!(n.paddingLength >= 1 && n.paddingLength <= 10)) errors.push({ field: 'rules.employeeNumbering.paddingLength', message: msg('Use between 1 and 10 digits.') });
      if (!(n.nextSequence >= 1)) errors.push({ field: 'rules.employeeNumbering.nextSequence', message: msg('The next number must be 1 or more.') });
      break;
    }
    case 6: {
      if (!adminStepVisible) break;
      const a = body.companyAdmin;
      if (a.mode === 'invite') {
        if (blank(a.fullNameEn)) errors.push({ field: 'companyAdmin.fullNameEn', message: msg('Enter the administrator’s full name in English.') });
        const email = (a.email ?? '').trim();
        const domain = (c.emailDomain ?? '').trim().toLowerCase();
        if (!email) errors.push({ field: 'companyAdmin.email', message: msg('Enter the administrator’s official work email.') });
        else if (!EMAIL.test(email)) errors.push({ field: 'companyAdmin.email', message: msg('Enter a full email address, such as name@company.sa.') });
        else if (domain && !email.toLowerCase().endsWith(`@${domain}`))
          errors.push({ field: 'companyAdmin.email', message: msg('Use an address on {domain}. KynexOne never creates mailboxes, so the administrator signs in with their company email.'), params: { domain } });
      }
      if (a.mode === 'existing' && blank(a.existingUserId)) errors.push({ field: 'companyAdmin.existingUserId', message: msg('Choose the user who will be the company super admin.') });
      break;
    }
    case 7:
      break;
  }
  return errors;
}

/** Non-blocking advice per step: shown in amber, never stops Next. */
export function stepWarnings(step: StepId, { body, context }: ValidationInput): FieldError[] {
  const warnings: FieldError[] = [];
  if (step === 3 && body.branches.length > 0 && body.branches.every(b => b.locations.length === 0))
    warnings.push({ field: 'locations', message: msg('No locations yet. Until you add one, employees can punch in at any geofenced site in the workspace. Add a location to each branch, or add them later in Setup → Locations.') });
  if (step === 4 && context?.gradesCurrency && body.company.currency && context.gradesCurrency !== body.company.currency && body.grades.length > 0)
    warnings.push({ field: 'grades', message: msg('Grade salary bands are kept in {gradeCurrency} for the whole group, but this company pays in {currency}. Enter the bands in {gradeCurrency}, or check them with finance before paying salaries.'), params: { gradeCurrency: context.gradesCurrency, currency: body.company.currency } });
  return warnings;
}

export type StepState = 'notStarted' | 'done' | 'warning';

/** Rail state: a step is done once visited with no blockers; warning when visited with blockers or advice. */
export function stepState(step: StepId, input: ValidationInput & { visited: ReadonlySet<StepId>; serverProblems?: readonly Problem[] }): StepState {
  if (!input.visited.has(step)) return 'notStarted';
  if (validateStep(step, input).length > 0) return 'warning';
  if ((input.serverProblems ?? []).some(p => p.step === step)) return 'warning';
  if (stepWarnings(step, input).length > 0) return 'warning';
  return 'done';
}

/** The first step (in order) that still blocks, or null. The rail cannot jump past it. */
export function firstBlockingStep(steps: readonly StepId[], input: ValidationInput): StepId | null {
  for (const s of steps) if (validateStep(s, input).length > 0) return s;
  return null;
}

/** `Branches[0].Code` / `branches[0].code` → `branches.0.code`, so server problems land on the same field keys. */
export function normalizeFieldPath(field: string): string {
  return field
    .replace(/\[(\d+)\]/g, '.$1')
    .split('.')
    .filter(Boolean)
    .map(part => (/^\d+$/.test(part) ? part : part.charAt(0).toLowerCase() + part.slice(1)))
    .join('.');
}

export function problemsByField(problems: readonly Problem[]): Map<string, Problem> {
  const map = new Map<string, Problem>();
  for (const p of problems) map.set(normalizeFieldPath(p.field), p);
  return map;
}

export interface PlanGroup { step: StepId; items: PlanItem[]; conflicts: number }

/** The dry-run plan grouped by the step that produced it, in step order. */
export function groupPlan(plan: CompanySetupPlan | null): PlanGroup[] {
  if (!plan) return [];
  const groups = new Map<StepId, PlanItem[]>();
  for (const item of plan.items) {
    const step = AREA_STEP[item.area] ?? 7;
    groups.set(step, [...(groups.get(step) ?? []), item]);
  }
  return STEP_IDS.filter(s => groups.has(s)).map(step => {
    const items = groups.get(step)!;
    return { step, items, conflicts: items.filter(i => i.action === 'conflict').length };
  });
}

/** Create is allowed only on a fresh plan with no conflicts and no problems. */
export function canCommit(plan: CompanySetupPlan | null): boolean {
  return !!plan && plan.ok && plan.problems.length === 0 && !plan.items.some(i => i.action === 'conflict');
}

// ── Structure tree ───────────────────────────────────────────────────────────

export type TreeTag = 'new' | 'reused';
export interface TreeNode { id: string; label: string; labelAr?: string; code?: string; tag?: TreeTag; children: TreeNode[]; kind: 'company' | 'group' | 'branch' | 'location' | 'costCentre' | 'catalog' | 'department' | 'designation' | 'grade' }

const named = (code: string, en: string | undefined, ar: string | undefined) => ({ label: (en ?? '').trim() || code, labelAr: (ar ?? '').trim() || undefined });

/**
 * Company → Branches → Locations; Cost centres; Shared group catalog (Departments, Designations, Grades).
 * Group labels are dictionary keys; the view translates them.
 */
export function buildTree(body: CompanySetupDraftBody, context?: CompanySetupContext | null): TreeNode {
  const catalog = context?.sharedCatalog;
  const tagFor = (list: readonly CodeName[] | undefined, code: string): TreeTag => (inCatalog(list, code) ? 'reused' : 'new');
  const rows = <T,>(items: readonly T[], kind: TreeNode['kind'], code: (r: T) => string, en: (r: T) => string | undefined, ar: (r: T) => string | undefined, list?: readonly CodeName[]): TreeNode[] =>
    items.filter(r => code(r).trim() || (en(r) ?? '').trim()).map((r, i) => {
      const reused = list ? inCatalog(list, code(r)) : undefined;
      return {
        id: `${kind}-${i}-${code(r)}`, kind, code: code(r).trim(),
        ...(reused ? named(reused.code, reused.nameEn, reused.nameAr ?? undefined) : named(code(r), en(r), ar(r))),
        tag: list ? tagFor(list, code(r)) : 'new',
        children: [],
      };
    });
  const company: TreeNode = {
    id: 'company', kind: 'company', ...named('', body.company.legalNameEn || '', body.company.legalNameAr), tag: 'new',
    children: [
      { id: 'branches', kind: 'group', label: msg('Branches'), children: body.branches.filter(b => b.code.trim() || b.nameEn.trim()).map((b, i) => ({
        id: `branch-${i}-${b.code}`, kind: 'branch' as const, code: b.code.trim(), ...named(b.code, b.nameEn, b.nameAr), tag: 'new' as const,
        children: rows(b.locations, 'location', l => l.code, l => l.nameEn, l => l.nameAr),
      })) },
      { id: 'costCentres', kind: 'group', label: msg('Cost centres'), children: rows(body.costCentres, 'costCentre', r => r.code, r => r.nameEn, r => r.nameAr) },
    ],
  };
  const shared: TreeNode = {
    id: 'catalog', kind: 'catalog', label: msg('Shared group catalog'), children: [
      { id: 'departments', kind: 'group', label: msg('Departments'), children: rows(body.departments, 'department', r => r.code, r => r.nameEn, r => r.nameAr, catalog?.departments ?? []) },
      { id: 'designations', kind: 'group', label: msg('Designations'), children: rows(body.designations, 'designation', r => r.code, r => r.titleEn, r => r.titleAr, catalog?.designations ?? []) },
      { id: 'grades', kind: 'group', label: msg('Grades'), children: rows(body.grades, 'grade', r => r.code, r => r.nameEn, r => r.nameAr, catalog?.grades ?? []) },
    ],
  };
  return { id: 'root', kind: 'group', label: '', children: [company, shared] };
}

/** "2 of 5 companies" inputs: null allowed means no plan cap. */
export function planUsage(context: CompanySetupContext | null | undefined): { used: number; allowed: number | null; full: boolean } | null {
  if (!context) return null;
  const allowed = context.companiesAllowed ?? null;
  return { used: context.companiesUsed, allowed, full: allowed !== null && context.companiesUsed >= allowed };
}

/** Next employee number preview from the numbering rule, e.g. "SA-HQ-2026-0001". */
export function numberingPreview(body: CompanySetupDraftBody, year = new Date().getFullYear()): string {
  const n = body.rules.employeeNumbering;
  const parts: string[] = [];
  if (n.prefix.trim()) parts.push(n.prefix.trim());
  if (n.useCountryPrefix && body.company.countryCode) parts.push(body.company.countryCode.toUpperCase());
  const head = body.branches.find(b => b.isHeadOffice) ?? body.branches[0];
  if (n.useBranchPrefix) parts.push(head?.code.trim() || 'BR');
  if (n.useDepartmentPrefix) parts.push(body.departments[0]?.code.trim() || 'DEP');
  if (n.useYear) parts.push(String(year));
  const pad = Math.min(Math.max(Math.trunc(n.paddingLength) || 1, 1), 10);
  parts.push(String(Math.max(Math.trunc(n.nextSequence) || 1, 1)).padStart(pad, '0'));
  return parts.join('-');
}
