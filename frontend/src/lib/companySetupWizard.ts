import { msg, type MessageParams } from '../i18n/translations';
import { isValidEmailDomain } from '../api/organization';
import { resolveSetupPath } from './setupPathState';
import type {
  CodeName, CompanySetupCompany, CompanySetupContext, CompanySetupDraftBody, CompanySetupDraftSummary, CompanySetupPlan,
  ExistingRow, ManageableCompany, PlanAction, PlanArea, PlanItem, Problem, WithBaseline,
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

/**
 * One wizard, two modes (Addendum A): `create` makes a new company; `existing` completes setup for an active
 * company whose rows are prefilled. Only step 1 and the review read differently.
 */
export type WizardMode = 'create' | 'existing';
export const wizardMode = (body: Pick<CompanySetupDraftBody, 'existingCompanyId'> | null | undefined): WizardMode =>
  body?.existingCompanyId ? 'existing' : 'create';

const EXISTING_STEPS: Partial<Record<StepId, { title: string; description: string }>> = {
  1: { title: msg('Summary'), description: msg('What this company has today') },
  7: { title: msg('Review & update'), description: msg('Check the changes, then save') },
};

/** Rail/heading copy for a step in this mode (dictionary keys; the view translates them). */
export function stepCopy(step: StepId, mode: WizardMode): { title: string; description: string } {
  return (mode === 'existing' ? EXISTING_STEPS[step] : undefined) ?? WIZARD_STEPS[step];
}

/** The chooser on Setup → Guided setup (Addendum A). */
export const GUIDED_SETUP_HREF = '/setup?tab=aiSetup&path=guided';
/** The Policy starter entry for the old studio (A7): attendance, overtime, benefits and HR rules starters. */
export const POLICY_STARTER_HREF = '/setup?tab=aiSetup&path=policyStarter';

/**
 * The company a Setup arrival names as a Guided setup deep link, or null. Only the address a Setup visit arrives with
 * can be one (a link from elsewhere in the app, a notification, an old studio bookmark such as
 * `/setup?tab=aiSetup&mode=starter&companyId=…`); Setup reads it once, when the page mounts. Inside Setup, `companyId`
 * is the company scope every move keeps, so the navigation's own Guided setup entry is never a deep link.
 *
 * `documentNavigation` is how the browser loaded this document when Setup is its first page: a reload or a history
 * restore brings back an address Setup wrote itself, not a link someone followed, so it opens the chooser.
 */
export function setupLandingCompanyLink(params: URLSearchParams, documentNavigation?: string | null): string | null {
  const tab = params.get('tab');
  if (tab !== null && tab !== 'aiSetup') return null;
  const companyId = params.get('companyId')?.trim();
  if (!companyId) return null;
  if (resolveSetupPath(params, { authorizedCompanyIds: new Set(), selectedCompanyId: null }).path !== 'guided') return null;
  if (documentNavigation === 'reload' || documentNavigation === 'back_forward') return null;
  return companyId;
}

/** Where the wizard for an existing company lives; `draft` resumes that draft. */
export function completeCompanyHref(companyId: string, draftId?: string | null): string {
  return `/setup/company/${encodeURIComponent(companyId)}/complete${draftId ? `?draft=${encodeURIComponent(draftId)}` : ''}`;
}

/** Resume address for either kind of draft. */
export function draftHref(summary: Pick<CompanySetupDraftSummary, 'id' | 'existingCompanyId'>): string {
  return summary.existingCompanyId ? completeCompanyHref(summary.existingCompanyId, summary.id) : `/setup/new-company?draft=${encodeURIComponent(summary.id)}`;
}

/** A4: the first payroll month is fixed once the company's cutover has left Planned. Unknown status: the server decides. */
export function firstPayrollMonthLocked(company: Pick<CompanySetupCompany, 'cutoverStatus'>): boolean {
  const status = (company.cutoverStatus ?? '').trim().toLowerCase();
  return status !== '' && status !== 'planned';
}

/** "5 of 9 done" inputs for one chooser row; complete when every item is done. */
/**
 * A company's country in the viewer's language, or the stored code as written. Intl.DisplayNames.of throws a
 * RangeError for anything that is not a region code ("SAU", "KSA", "AE "), and a stored company can hold such a
 * value: rendering it must never take the whole Setup page down.
 */
export function countryDisplayName(names: Pick<Intl.DisplayNames, 'of'> | null, code: string | null | undefined): string {
  const trimmed = (code ?? '').trim();
  if (!trimmed) return '';
  try { return names?.of(trimmed.toUpperCase()) ?? trimmed; } catch { return trimmed; }
}

export function readinessProgress(company: Pick<ManageableCompany, 'readinessDone' | 'readinessTotal'>): { done: number; total: number; percent: number; complete: boolean } {
  const total = Math.max(0, Math.trunc(company.readinessTotal || 0));
  const done = Math.min(Math.max(0, Math.trunc(company.readinessDone || 0)), total);
  return { done, total, percent: total === 0 ? 0 : Math.round((done / total) * 100), complete: total > 0 && done === total };
}

export const isExistingRow = (row: ExistingRow | null | undefined) => !!row?.existingId;

// ── A9: changes made elsewhere since the draft started ─────────────────────────

/** A field of an existing row was changed both in this draft and elsewhere since it started (Problem.current = now). */
export const CHANGED_ELSEWHERE = 'changed_since_draft_started';
/** A draft saved before A9 carries no baselines: it is reloaded from the company, never guessed. */
export const DRAFT_NEEDS_REFRESH = 'draft_needs_refresh';

const NUMBER_FIELDS = new Set(['latitude', 'longitude', 'radiusMeters', 'paddingLength', 'nextSequence']);
const FLAG_FIELDS = new Set(['isHeadOffice', 'useCountryPrefix', 'useBranchPrefix', 'useDepartmentPrefix', 'useYear', 'override']);

/** The row or block a problem's field path names, and the field in it: `branches.1.nameEn` → branches[1], `nameEn`. */
function locateField(root: unknown, field: string): { holder: Record<string, unknown> & WithBaseline; key: string } | null {
  const parts = normalizeFieldPath(field).split('.');
  const key = parts.pop();
  let holder: unknown = root;
  for (const part of parts) holder = holder && typeof holder === 'object' ? (holder as Record<string, unknown>)[part] : undefined;
  return key && holder && typeof holder === 'object' ? { holder: holder as Record<string, unknown> & WithBaseline, key } : null;
}

/** The field's value as the form holds it, from the baseline's string form. */
export function fieldValueFromCurrent(key: string, current: string | null | undefined): unknown {
  if (NUMBER_FIELDS.has(key)) return current == null || current === '' ? null : Number(current);
  if (FLAG_FIELDS.has(key)) return current === 'true';
  return current ?? '';
}

/**
 * A9, the reader's answer to `changed_since_draft_started`. `theirs` puts the current value in the field (it then
 * matches what is there, so nothing is written for it). `mine` keeps the draft's value and moves that one field's
 * baseline to the current value, so the planner reads it as this user's edit made on top of the newer value.
 * Everything else, the rest of the baseline included, is kept exactly. Returns a new body.
 */
export function resolveChangedElsewhere(body: CompanySetupDraftBody, problem: Pick<Problem, 'field' | 'current'>, choice: 'theirs' | 'mine'): CompanySetupDraftBody {
  const next = structuredClone(body);
  const found = locateField(next, problem.field);
  if (!found) return body;
  const { holder, key } = found;
  const current = problem.current ?? null;
  if (choice === 'theirs') holder[key] = fieldValueFromCurrent(key, current);
  holder.baseline = { ...(holder.baseline ?? {}), [key]: current };
  return next;
}

// The form's own edits, as pure functions, so the baseline's survival is unit-tested: each one spreads the row or block
// it changes, never rebuilds it (A9).

/** One field of one row (RowsEditor). */
export function editRow<T extends object>(rows: readonly T[], index: number, key: keyof T, value: unknown): T[] {
  return rows.map((row, i) => (i === index ? { ...row, [key]: value } : row));
}

/** Branches after an edit, with exactly one head office: ticking one unticks the others; a lone branch is the head office. */
export function withOneHeadOffice<T extends { isHeadOffice: boolean }>(before: readonly T[], branches: readonly T[]): T[] {
  const ticked = branches.findIndex((row, i) => row.isHeadOffice && !before[i]?.isHeadOffice);
  const next = ticked >= 0 ? branches.map((row, i) => ({ ...row, isHeadOffice: i === ticked })) : [...branches];
  if (next.length === 1 && !next[0].isHeadOffice) next[0] = { ...next[0], isHeadOffice: true };
  return next;
}

/** The weekend picked on step 5: an override exactly when it differs from the country's. */
export function chooseWeekend(workWeek: CompanySetupDraftBody['rules']['workWeek'], weekendDays: string, packWeekend: string): CompanySetupDraftBody['rules']['workWeek'] {
  return { ...workWeek, weekendDays, override: weekendDays !== packWeekend };
}

/** The current value of a `changed_since_draft_started` problem, as the reader sees it. */
export function currentValueLabel(problem: Pick<Problem, 'field' | 'current'>): string {
  const key = normalizeFieldPath(problem.field).split('.').pop() ?? '';
  if (FLAG_FIELDS.has(key)) return problem.current === 'true' ? msg('Yes') : msg('No');
  return problem.current ?? msg('(empty)');
}

/**
 * True when an existing row differs from what the company had when the wizard opened (the prefill), compared on the
 * given fields. New rows and rows the prefill does not know are never "changed" — they are new.
 */
export function rowChanged<T extends ExistingRow>(row: T, baseline: readonly T[] | undefined, fields: readonly (keyof T)[]): boolean {
  if (!row.existingId || !baseline) return false;
  const before = baseline.find(b => b.existingId === row.existingId);
  if (!before) return false;
  const v = (x: unknown) => (x === undefined || x === null ? '' : typeof x === 'string' ? x.trim() : x);
  return fields.some(f => v(row[f]) !== v(before[f]));
}

/** Where an existing row is removed instead (A3: the wizard never deletes). Setup tab ids. */
export const MANAGE_TAB: Record<'branches' | 'locations' | 'costCentres' | 'departments' | 'designations' | 'grades', { tab: string; label: string }> = {
  branches: { tab: 'branches', label: msg('Branches') },
  locations: { tab: 'locations', label: msg('Locations') },
  costCentres: { tab: 'costCenters', label: msg('Cost centres') },
  departments: { tab: 'departments', label: msg('Departments') },
  designations: { tab: 'designations', label: msg('Designations') },
  grades: { tab: 'grades', label: msg('Grades') },
};

/** Labels for `PlanItem.changedFields`, so an update names its fields in the reader's language. */
export const CHANGED_FIELD_LABEL: Record<string, string> = {
  legalNameEn: msg('Legal name (English)'), legalNameAr: msg('Legal name (Arabic)'), tradeName: msg('Trade name (optional)'),
  registrationNumber: msg('Commercial registration (CR) number'), molEstablishmentNumber: msg('MOL establishment number'),
  gosiEmployerId: msg('GOSI employer number (optional)'), unifiedNationalNumber: msg('Unified National Number (optional)'),
  taxNumber: msg('VAT number (optional)'), qiwaEstablishmentId: msg('Qiwa establishment ID (optional)'),
  emailDomain: msg('Work email domain (optional)'), workEmailPattern: msg('Work email pattern (optional)'),
  erpCompanyCode: msg('ERP company code (optional)'), firstPayrollMonth: msg('First payroll month'),
  nameEn: msg('Name (English)'), nameAr: msg('Name (Arabic)'), titleEn: msg('Title (English)'), titleAr: msg('Title (Arabic)'),
  city: msg('City'), timeZoneId: msg('Time zone'), isHeadOffice: msg('Head office'),
  latitude: msg('Latitude'), longitude: msg('Longitude'), radiusMeters: msg('Radius (m)'),
  weekendDays: msg('Weekend (days off)'), prefix: msg('Prefix'), paddingLength: msg('Digits in the number'), nextSequence: msg('Next number'),
  useCountryPrefix: msg('Add the country code'), useBranchPrefix: msg('Add the branch code'), useDepartmentPrefix: msg('Add the department code'), useYear: msg('Add the joining year'),
};

/**
 * What to do next for each server code of existing mode (and the shared ones it raises), in the reader's language.
 * The server's own text stays the fallback for any code not listed here.
 */
export const SETUP_CODE_COPY: Record<string, string> = {
  country_locked: msg('The country of an existing company cannot change: payroll, GOSI and bank files depend on it. To register in another country, create a new company.'),
  currency_locked: msg('The salary currency of an existing company cannot change: payroll and bank files depend on it. To pay in another currency, create a new company.'),
  draft_target_locked: msg('This draft belongs to one company and cannot be moved to another. Discard it and start again from Guided setup.'),
  existing_code_locked: msg('The code of a row the company already has cannot change, because other records refer to it. Put the code back, or add a new row with the new code.'),
  first_payroll_month_locked: msg('Payroll has already started for this company, so the first payroll month cannot change. Put back the month that was saved.'),
  company_not_manageable: msg('You cannot change the setup of this company. Choose one of your companies in Guided setup, or ask your group super admin.'),
  company_admin_required: msg('Only an administrator of this company can complete its setup. Ask your company or group super admin.'),
  company_not_active: msg('This company is not active yet, so its setup cannot be completed here. Activate it in Setup → Companies first.'),
  existing_company_draft_open: msg('Setup for this company was already started, so that draft is opened instead of a second one.'),
  existing_row_unknown: msg('A row in this draft is no longer one of the company’s records; it may have been removed in Setup. Discard this draft and start again from Guided setup.'),
  branch_code_taken: msg('This branch code is already used by this company, perhaps by a removed branch. Choose a different code.'),
  sequence_cannot_go_back: msg('The next employee number cannot go back, because a lower one could repeat a number already given. Enter the saved number or a higher one.'),
  work_week_override_kept: msg('This company keeps its own weekend. If it should follow the country weekend, remove it in Setup → GCC settings.'),
  organisation_defaults_need_group_scope: msg('Some country defaults that every company shares are missing, and only a group administrator can add them. Ask your group super admin; nothing else waits on it here.'),
  shared_row_not_edited: msg('This is a shared row: its name is not changed here. A group administrator renames it in its Setup tab.'),
  // A9. changed_since_draft_started has none on purpose: the server's own words carry the value that is there now.
  draft_needs_refresh: msg('This draft was started before KynexOne kept a record of what the company had at the time, so it cannot tell your edits from changes made elsewhere. Reload the company’s current setup, then make your changes again.'),
};

/** The copy for a server code, or undefined when the server's own text should be shown. */
export function setupCodeCopy(code: string | null | undefined): string | undefined {
  return code ? SETUP_CODE_COPY[code] : undefined;
}

/** The planner's change words (CompanySetupPlanner "Changes: …"), so an update reads in the reader's language. */
const REASON_FIELD: Record<string, string> = {
  'legal name (English)': msg('Legal name (English)'), 'legal name (Arabic)': msg('Legal name (Arabic)'), 'trade name': msg('Trade name (optional)'),
  'CR number': msg('Commercial registration (CR) number'), 'MOL establishment number': msg('MOL establishment number'),
  'GOSI number': msg('GOSI employer number (optional)'), 'Unified National Number': msg('Unified National Number (optional)'),
  'VAT number': msg('VAT number (optional)'), 'Qiwa establishment id': msg('Qiwa establishment ID (optional)'),
  'email domain': msg('Work email domain (optional)'), 'work-email pattern': msg('Work email pattern (optional)'), 'ERP company code': msg('ERP company code (optional)'),
  'name (English)': msg('Name (English)'), 'name (Arabic)': msg('Name (Arabic)'), name: msg('Name'), city: msg('City'), 'time zone': msg('Time zone'),
  'head office': msg('Head office'), coordinates: msg('Coordinates'), 'geofence radius': msg('Radius (m)'),
  prefix: msg('Prefix'), padding: msg('Digits in the number'), 'next number': msg('Next number'), format: msg('Number format'),
};

/**
 * "Changes: name (English), city." → the dictionary keys of those fields; null when the reason is not that shape or
 * names a word not listed (the reason is then shown as the server wrote it).
 */
export function reasonFields(reason: string | null | undefined): string[] | null {
  const m = /^Changes:\s*(.+?)\.?$/.exec((reason ?? '').trim());
  if (!m) return null;
  const words = m[1].split(',').map(w => w.trim()).filter(Boolean);
  const keys = words.map(w => REASON_FIELD[w]);
  return words.length > 0 && keys.every(Boolean) ? keys : null;
}

/**
 * What the review shows open: what will change. In create mode every item is shown (reused catalog rows are part of
 * what Create does). In existing mode, rows that stay as they are — unchanged, and shared rows reused as they are —
 * are collapsed so the reader sees only the changes.
 */
export function isPlanChange(action: PlanAction, mode: WizardMode): boolean {
  if (mode === 'create') return action !== 'unchanged';
  return action === 'create' || action === 'update' || action === 'conflict';
}

export interface PlanCounts { create: number; update: number; unchanged: number; reuse: number; conflict: number }
export function planCounts(plan: CompanySetupPlan | null): PlanCounts {
  const out: PlanCounts = { create: 0, update: 0, unchanged: 0, reuse: 0, conflict: 0 };
  for (const item of plan?.items ?? []) if (item.action in out) out[item.action] += 1;
  return out;
}

/** Existing mode with nothing to add or change: the commit writes only its audit row. */
export function planIsNoChange(plan: CompanySetupPlan | null): boolean {
  if (!plan) return false;
  const c = planCounts(plan);
  return c.create === 0 && c.update === 0 && c.conflict === 0;
}

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

/**
 * The users step 6 may offer as "an existing user" company super admin: active, and already holding the Admin
 * role (a company admin elsewhere, or a group admin who manages every company already). Roles apply across every
 * company a user can reach, so giving Admin to anyone else would also make them Admin of their other companies;
 * the planner refuses that as `existing_user_not_admin`, and stays the backstop for anything this list misses.
 */
export function eligibleCompanySuperAdmins<U extends { isActive: boolean; status?: string | null; roles?: readonly string[] | null }>(users: readonly U[]): U[] {
  return users.filter(u => u.isActive && (u.status ?? 'Active') === 'Active'
    && (u.roles ?? []).some(r => r.trim().toLowerCase() === 'admin'));
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
  /** Existing mode: what the company has today (the prefill). Values kept from it are judged as saved, not as typed. */
  baseline?: CompanySetupDraftBody | null;
}

/**
 * Existing mode: registration values saved before today's rules and left as they are do not block (the server warns
 * instead of refusing); a value the reader changes is checked as on create.
 */
const LEGACY_FIELDS = ['company.legalNameAr', 'company.registrationNumber', 'company.molEstablishmentNumber', 'company.unifiedNationalNumber', 'company.emailDomain'] as const;
function keptFromBaseline(field: string, { body, baseline }: ValidationInput): boolean {
  if (!body.existingCompanyId || !baseline || !(LEGACY_FIELDS as readonly string[]).includes(field)) return false;
  const key = field.slice('company.'.length) as keyof CompanySetupCompany;
  return (String(body.company[key] ?? '').trim()) === (String(baseline.company[key] ?? '').trim());
}

/** Field-level blockers for one step. Empty means Next is allowed. */
export function validateStep(step: StepId, input: ValidationInput): FieldError[] {
  const errors = validateStepRaw(step, input).filter(e => !keptFromBaseline(e.field, input));
  // Existing mode: the employee-number counter never goes back (server: sequence_cannot_go_back).
  const floor = input.body.existingCompanyId ? input.baseline?.rules.employeeNumbering.nextSequence : undefined;
  if (step === 5 && floor && input.body.rules.employeeNumbering.nextSequence < floor && !errors.some(e => e.field === 'rules.employeeNumbering.nextSequence'))
    errors.push({ field: 'rules.employeeNumbering.nextSequence', message: msg('The next number is already {number}. A lower one could repeat a number already given; enter {number} or higher.'), params: { number: floor } });
  return errors;
}

/** Existing mode: legacy registration values kept as they are, shown as advice on step 2 instead of blocking. */
export function legacyWarnings(input: ValidationInput): FieldError[] {
  return validateStepRaw(2, input).filter(e => keptFromBaseline(e.field, input));
}

function validateStepRaw(step: StepId, { body, context, adminStepVisible = true }: ValidationInput): FieldError[] {
  const errors: FieldError[] = [];
  const c = body.company;
  const saudi = isSaudi(c.countryCode);
  switch (step) {
    case 1:
      if (!body.existingCompanyId && body.startFrom === 'copy' && blank(body.copyFromCompanyId)) errors.push({ field: 'copyFromCompanyId', message: msg('Choose the company to copy from.') });
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
          // The planner's own limits (CompanySetupPlanner), checked here so the message sits at the field.
          const at = `branches.${i}.locations.${j}`;
          const lat = l.latitude ?? null;
          const lng = l.longitude ?? null;
          if ((lat === null) !== (lng === null)) errors.push({ field: `${at}.${lat === null ? 'latitude' : 'longitude'}`, message: msg('Enter both latitude and longitude, or neither.') });
          if (lat !== null && !(lat >= -90 && lat <= 90)) errors.push({ field: `${at}.latitude`, message: msg('Latitude must be between -90 and 90.') });
          if (lng !== null && !(lng >= -180 && lng <= 180)) errors.push({ field: `${at}.longitude`, message: msg('Longitude must be between -180 and 180.') });
          if (l.radiusMeters != null && !(l.radiusMeters >= 1 && l.radiusMeters <= 100_000)) errors.push({ field: `${at}.radiusMeters`, message: msg('The geofence radius must be between 1 and 100,000 metres.') });
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
export function stepWarnings(step: StepId, input: ValidationInput): FieldError[] {
  const { body, context } = input;
  const warnings: FieldError[] = step === 2 ? legacyWarnings(input) : [];
  if (step === 3 && body.branches.length > 0 && body.branches.every(b => b.locations.length === 0))
    warnings.push({ field: 'locations', message: msg('No locations yet. Until you add one, employees can punch in at any geofenced site in the organisation. Add a location to each branch, or add them later in Setup → Locations.') });
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

export type TreeTag = 'new' | 'reused' | 'existing';
export interface TreeNode { id: string; label: string; labelAr?: string; code?: string; tag?: TreeTag; children: TreeNode[]; kind: 'company' | 'group' | 'branch' | 'location' | 'costCentre' | 'catalog' | 'department' | 'designation' | 'grade' }

const named = (code: string, en: string | undefined, ar: string | undefined) => ({ label: (en ?? '').trim() || code, labelAr: (ar ?? '').trim() || undefined });

/**
 * Company → Branches → Locations; Cost centres; Shared group catalog (Departments, Designations, Grades).
 * Group labels are dictionary keys; the view translates them.
 */
export function buildTree(body: CompanySetupDraftBody, context?: CompanySetupContext | null): TreeNode {
  const catalog = context?.sharedCatalog;
  const tagFor = (list: readonly CodeName[] | undefined, code: string): TreeTag => (inCatalog(list, code) ? 'reused' : 'new');
  // Existing mode: a row the company already has is "existing"; a shared catalog row stays "reused".
  const ownTag = (r: unknown): TreeTag => (isExistingRow(r as ExistingRow) ? 'existing' : 'new');
  const rows = <T,>(items: readonly T[], kind: TreeNode['kind'], code: (r: T) => string, en: (r: T) => string | undefined, ar: (r: T) => string | undefined, list?: readonly CodeName[]): TreeNode[] =>
    items.filter(r => code(r).trim() || (en(r) ?? '').trim()).map((r, i) => {
      const reused = list ? inCatalog(list, code(r)) : undefined;
      return {
        id: `${kind}-${i}-${code(r)}`, kind, code: code(r).trim(),
        ...(reused ? named(reused.code, reused.nameEn, reused.nameAr ?? undefined) : named(code(r), en(r), ar(r))),
        tag: list ? tagFor(list, code(r)) : ownTag(r),
        children: [],
      };
    });
  const company: TreeNode = {
    id: 'company', kind: 'company', ...named('', body.company.legalNameEn || '', body.company.legalNameAr), tag: body.existingCompanyId ? 'existing' : 'new',
    children: [
      { id: 'branches', kind: 'group', label: msg('Branches'), children: body.branches.filter(b => b.code.trim() || b.nameEn.trim()).map((b, i) => ({
        id: `branch-${i}-${b.code}`, kind: 'branch' as const, code: b.code.trim(), ...named(b.code, b.nameEn, b.nameAr), tag: ownTag(b),
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
