import { msg, type MessageParams } from '../i18n/translations';
import { isValidEmailDomain } from '../api/organization';
import { resolveSetupPath } from './setupPathState';
import type {
  CompanySetupCompany, CompanySetupContext, CompanySetupDraftBody, CompanySetupDraftSummary, CompanySetupGrade, CompanySetupPlan,
  ExistingRow, ManageableCompany, OrgSection, PlanAction, PlanArea, PlanItem, Problem, WithBaseline,
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
  4: { title: msg('Organisation'), description: msg('Cost centres, departments, grades, designations') },
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
  workspace_defaults_need_group_scope: msg('Some country defaults that every company shares are missing, and only a group administrator can add them. Ask your group super admin; nothing else waits on it here.'),
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
 * What the review shows open: what will change (added, updated, or in conflict). Rows that stay as they are — unchanged,
 * or existing settings reused as they are — are folded away in both modes.
 */
export function isPlanChange(action: PlanAction, _mode: WizardMode): boolean {
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
/** Arabic-Indic (٠–٩) and Persian (۰–۹) digits as Latin digits, so a number typed on an Arabic keyboard is checked as typed. */
export function latinDigits(value?: string | null): string {
  return (value ?? '').replace(/[\u0660-\u0669]/g, d => String(d.charCodeAt(0) - 0x0660)).replace(/[\u06F0-\u06F9]/g, d => String(d.charCodeAt(0) - 0x06F0));
}
/** Public mailbox services: an administrator signs in with their company email, never one of these. */
export const PUBLIC_EMAIL_DOMAINS = new Set([
  'gmail.com', 'googlemail.com', 'outlook.com', 'outlook.sa', 'hotmail.com', 'hotmail.co.uk', 'live.com', 'msn.com', 'yahoo.com', 'yahoo.co.uk',
  'ymail.com', 'icloud.com', 'me.com', 'mac.com', 'aol.com', 'proton.me', 'protonmail.com', 'gmx.com', 'mail.com', 'zoho.com', 'yandex.com',
]);
const ARABIC_LETTER = /[\u0621-\u064A\u0671-\u06D3]/;
const norm = (code?: string | null) => (code ?? '').trim().toUpperCase();
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const MOL = /^[0-9]+(-[0-9]+)?$/;

/** Mirrors backend KsaWageFileRules.ValidateEstablishmentId. */
export function molEstablishmentError(value?: string | null): string | null {
  const v = latinDigits(value).trim();
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
const LEGACY_FIELDS = ['company.legalNameAr', 'company.registrationNumber', 'company.molEstablishmentNumber', 'company.unifiedNationalNumber', 'company.emailDomain', 'company.taxNumber'] as const;
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
      else if (saudi && !/^[0-9]{10}$/.test(latinDigits(c.registrationNumber).trim())) errors.push({ field: 'company.registrationNumber', message: msg('A Saudi CR number is 10 digits. Check the number on the CR certificate.') });
      if (saudi) {
        const mol = molEstablishmentError(c.molEstablishmentNumber);
        if (mol) errors.push({ field: 'company.molEstablishmentNumber', message: mol });
      }
      if (!blank(c.unifiedNationalNumber) && !/^7[0-9]{9}$/.test(latinDigits(c.unifiedNationalNumber).trim()))
        errors.push({ field: 'company.unifiedNationalNumber', message: msg('The Unified National Number is 10 digits starting with 7. Leave it blank if you do not have it yet.') });
      if (saudi && !blank(c.taxNumber) && !/^3\d{13}3$/.test(latinDigits(c.taxNumber).trim()))
        errors.push({ field: 'company.taxNumber', message: msg('A Saudi VAT number is 15 digits that start and end with 3. Leave it blank if the company is not registered for VAT.') });
      if (!isValidEmailDomain(c.emailDomain ?? '')) errors.push({ field: 'company.emailDomain', message: msg('Use a bare domain such as acme.sa, without "@" or "https://". Leave it blank if the company has none.') });
      if (!/^[0-9]{4}-(0[1-9]|1[0-2])$/.test(c.firstPayrollMonth ?? '')) errors.push({ field: 'company.firstPayrollMonth', message: msg('Choose a month, for example 2026-11.') });
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
      // Addendum B: every row is this company's own; there is no shared catalog to reuse from.
      const rowBasics = (rows: ({ code: string; name?: string; nameAr?: string } & ExistingRow)[], path: string, nameField: string, extraFilled: (i: number) => boolean = () => false) => {
        rows.forEach((row, i) => {
          if (!row.existingId && blank(row.code) && blank(row.name) && blank(row.nameAr) && !extraFilled(i)) {
            errors.push({ field: `${path}.${i}.code`, message: msg('Fill in this row or remove it.') });
            return;
          }
          if (blank(row.code)) errors.push({ field: `${path}.${i}.code`, message: msg('Enter a code.') });
          if (blank(row.name)) errors.push({ field: `${path}.${i}.${nameField}`, message: msg('Enter the name in English.') });
        });
        duplicateCodes(rows, r => r.code, path, errors);
      };
      rowBasics(body.costCentres.map(r => ({ code: r.code, name: r.nameEn, nameAr: r.nameAr, existingId: r.existingId })), 'costCentres', 'nameEn');
      rowBasics(body.departments.map(r => ({ code: r.code, name: r.nameEn, nameAr: r.nameAr, existingId: r.existingId })), 'departments', 'nameEn',
        i => !blank(body.departments[i].parentCode) || !blank(body.departments[i].branchCode) || !blank(body.departments[i].costCentreCode));
      rowBasics(body.grades.map(r => ({ code: r.code, name: r.nameEn, nameAr: r.nameAr, existingId: r.existingId })), 'grades', 'nameEn',
        i => (body.grades[i].minSalary || 0) > 0 || (body.grades[i].maxSalary || 0) > 0);
      rowBasics(body.designations.map(r => ({ code: r.code, name: r.titleEn, nameAr: r.titleAr, existingId: r.existingId })), 'designations', 'titleEn',
        i => !blank(body.designations[i].departmentCode) || !blank(body.designations[i].gradeCode));
      const centres = new Set(body.costCentres.map(cc => norm(cc.code)).filter(Boolean));
      const departments = new Set(body.departments.map(d => norm(d.code)).filter(Boolean));
      const branches = new Set(body.branches.map(b => norm(b.code)).filter(Boolean));
      const grades = new Set(body.grades.map(g => norm(g.code)).filter(Boolean));
      body.departments.forEach((d, i) => {
        if (d.costCentreCode && !centres.has(norm(d.costCentreCode)))
          errors.push({ field: `departments.${i}.costCentreCode`, message: msg('Choose a cost centre from the list on this step.') });
        if (d.parentCode && !departments.has(norm(d.parentCode)))
          errors.push({ field: `departments.${i}.parentCode`, message: msg('Choose a parent department from this list.') });
        else if (d.parentCode && norm(d.parentCode) === norm(d.code))
          errors.push({ field: `departments.${i}.parentCode`, message: msg('A department cannot be its own parent.') });
        if (d.branchCode && !branches.has(norm(d.branchCode)))
          errors.push({ field: `departments.${i}.branchCode`, message: msg('Choose a branch from step 3.') });
      });
      body.grades.forEach((g, i) => {
        if (!g.existingId && blank(g.code) && blank(g.nameEn) && blank(g.nameAr) && !((g.minSalary || 0) > 0 || (g.maxSalary || 0) > 0)) return;
        if (!(g.level >= 1)) errors.push({ field: `grades.${i}.level`, message: msg('Enter a level of 1 or more.') });
        if (!(g.minSalary >= 0) || !(g.minSalary <= g.midSalary && g.midSalary <= g.maxSalary))
          errors.push({ field: `grades.${i}.maxSalary`, message: msg('Salaries must rise from minimum to midpoint to maximum.') });
      });
      body.designations.forEach((d, i) => {
        if (d.departmentCode && !departments.has(norm(d.departmentCode)))
          errors.push({ field: `designations.${i}.departmentCode`, message: msg('Choose a department from this company’s list.') });
        if (d.gradeCode && !grades.has(norm(d.gradeCode)))
          errors.push({ field: `designations.${i}.gradeCode`, message: msg('Choose a grade from this company’s list.') });
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
        // With no company domain to check against, a public mailbox is still never an administrator's sign-in.
        else if (PUBLIC_EMAIL_DOMAINS.has(email.toLowerCase().split('@')[1] ?? ''))
          errors.push({ field: 'companyAdmin.email', message: msg('{domain} is a public email service. Use the person’s company email; KynexOne never creates mailboxes.'), params: { domain: email.toLowerCase().split('@')[1] } });
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
  const { body } = input;
  const warnings: FieldError[] = step === 2 ? legacyWarnings(input) : [];
  if (step === 2 && !blank(body.company.legalNameAr) && !ARABIC_LETTER.test(body.company.legalNameAr ?? ''))
    warnings.push({ field: 'company.legalNameAr', message: msg('The Arabic legal name has no Arabic letters. Enter the name as it is written on the commercial registration.') });
  if (step === 3 && body.branches.length > 0 && body.branches.every(b => b.locations.length === 0))
    warnings.push({ field: 'locations', message: msg('No locations yet. Until you add one, employees can punch in at any geofenced site in the workspace. Add a location to each branch, or add them later in Setup → Locations.') });
  return warnings;
}

/**
 * Rail state. `blocked` = something must be fixed (a blocker or a server problem); `review` = only advice worth a look;
 * `inProgress` = opened but not finished (a step with parts not all seen, or the review not checked yet).
 */
export type StepState = 'notStarted' | 'inProgress' | 'blocked' | 'review' | 'done';

/** Rail state of a step without parts. */
export function stepState(step: StepId, input: ValidationInput & { visited: ReadonlySet<StepId>; serverProblems?: readonly Problem[] }): StepState {
  if (!input.visited.has(step)) return 'notStarted';
  if (validateStep(step, input).length > 0) return 'blocked';
  if ((input.serverProblems ?? []).some(p => p.step === step)) return 'blocked';
  if (stepWarnings(step, input).length > 0) return 'review';
  return 'done';
}

/** "2 fields to fix" / "1 thing to check" for a step's rail sub-label. */
export function stepIssueCounts(step: StepId, input: ValidationInput & { serverProblems?: readonly Problem[] }): { fix: number; check: number } {
  const fields = new Set(validateStep(step, input).map(e => e.field));
  for (const p of input.serverProblems ?? []) if (p.step === step) fields.add(normalizeFieldPath(p.field));
  const check = stepWarnings(step, input).length + (step === 4 ? gradeWarnings(input.body.grades).length : 0);
  return { fix: fields.size, check };
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

export type TreeTag = 'new' | 'existing';
export interface TreeNode {
  id: string; label: string; labelAr?: string; code?: string; tag?: TreeTag; children: TreeNode[];
  kind: 'company' | 'group' | 'branch' | 'location' | 'costCentre' | 'department' | 'designation' | 'grade';
  /** On an Organisation group: the wizard part that edits it (Addendum B4). */
  part?: PartId;
}

const named = (code: string, en: string | undefined, ar: string | undefined) => ({ label: (en ?? '').trim() || code, labelAr: (ar ?? '').trim() || undefined });

/**
 * Company → Branches → Locations; Cost centres; Departments; Grades; Designations — all this company's own (Addendum B).
 * Group labels are dictionary keys; the view translates them.
 */
export function buildTree(body: CompanySetupDraftBody, _context?: CompanySetupContext | null): TreeNode {
  const ownTag = (r: unknown): TreeTag => (isExistingRow(r as ExistingRow) ? 'existing' : 'new');
  const rows = <T,>(items: readonly T[], kind: TreeNode['kind'], code: (r: T) => string, en: (r: T) => string | undefined, ar: (r: T) => string | undefined): TreeNode[] =>
    items.filter(r => code(r).trim() || (en(r) ?? '').trim()).map((r, i) => ({
      id: `${kind}-${i}-${code(r)}`, kind, code: code(r).trim(), ...named(code(r), en(r), ar(r)), tag: ownTag(r), children: [],
    }));
  const company: TreeNode = {
    id: 'company', kind: 'company', ...named('', body.company.legalNameEn || '', body.company.legalNameAr), tag: body.existingCompanyId ? 'existing' : 'new',
    children: [
      { id: 'branches', kind: 'group', label: msg('Branches'), children: body.branches.filter(b => b.code.trim() || b.nameEn.trim()).map((b, i) => ({
        id: `branch-${i}-${b.code}`, kind: 'branch' as const, code: b.code.trim(), ...named(b.code, b.nameEn, b.nameAr), tag: ownTag(b),
        children: rows(b.locations, 'location', l => l.code, l => l.nameEn, l => l.nameAr),
      })) },
      { id: 'costCentres', part: 'costCentres', kind: 'group', label: msg('Cost centres'), children: rows(body.costCentres, 'costCentre', r => r.code, r => r.nameEn, r => r.nameAr) },
      { id: 'departments', part: 'departments', kind: 'group', label: msg('Departments'), children: rows(body.departments, 'department', r => r.code, r => r.nameEn, r => r.nameAr) },
      { id: 'grades', part: 'grades', kind: 'group', label: msg('Grades'), children: rows(sortedGradeIndexes(body.grades).map(i => body.grades[i]), 'grade', r => r.code, r => r.nameEn, r => r.nameAr) },
      { id: 'designations', part: 'designations', kind: 'group', label: msg('Designations'), children: rows(body.designations, 'designation', r => r.code, r => r.titleEn, r => r.titleAr) },
    ],
  };
  return { id: 'root', kind: 'group', label: '', children: [company] };
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

// ── Wizard parts (Addendum B4) ───────────────────────────────────────────────

/** A part of a step: steps 4 and 5 go one section at a time. The part is UI state plus `&part=` in the URL. */
export type PartId = 'costCentres' | 'departments' | 'grades' | 'designations' | 'workRules' | 'leave' | 'endOfService' | 'moreSettings';

/** Parts in order. Step 4 follows dependency order: each section references only earlier ones. */
export const STEP_PARTS: Partial<Record<StepId, readonly PartId[]>> = {
  4: ['costCentres', 'departments', 'grades', 'designations'],
  5: ['workRules', 'leave', 'endOfService', 'moreSettings'],
};

export const PART_IDS: readonly PartId[] = [...STEP_PARTS[4]!, ...STEP_PARTS[5]!];
export const isPartId = (value: string | null | undefined): value is PartId => !!value && (PART_IDS as readonly string[]).includes(value);

/** Dictionary keys: the part's name and its one-line purpose. */
export const PART_COPY: Record<PartId, { title: string; purpose: string }> = {
  costCentres: { title: msg('Cost centres'), purpose: msg('Where this company’s costs are reported. Departments can be charged to one of them.') },
  departments: { title: msg('Departments'), purpose: msg('The teams people belong to. Link each one to a cost centre so its costs land in the right place.') },
  grades: { title: msg('Grades'), purpose: msg('Pay levels with a salary band. Designations and benefits use them.') },
  designations: { title: msg('Designations'), purpose: msg('Job titles, each in a department and at a grade.') },
  workRules: { title: msg('Work rules'), purpose: msg('The working week and how employee numbers are made.') },
  leave: { title: msg('Leave & holidays'), purpose: msg('Leave types, leave policies and the holiday calendar.') },
  endOfService: { title: msg('End of service'), purpose: msg('Gratuity rules for this company’s country.') },
  moreSettings: { title: msg('More settings'), purpose: msg('Everything else this company can set up, one link each.') },
};

export const partsOf = (step: StepId): readonly PartId[] => STEP_PARTS[step] ?? [];
export const stepOfPart = (part: PartId): StepId => (STEP_PARTS[4]!.includes(part) ? 4 : 5);
/** Step 4 parts are the Organisation sections the server saves one at a time (B2). */
export const isOrgPart = (part: PartId | null | undefined): part is OrgSection => !!part && STEP_PARTS[4]!.includes(part);

/** One place in the journey: a step, and its part when the step has parts. */
export interface WizardPosition { step: StepId; part?: PartId }
export const positionKey = (p: WizardPosition) => (p.part ? `${p.step}:${p.part}` : String(p.step));
export const samePosition = (a: WizardPosition, b: WizardPosition) => positionKey(a) === positionKey(b);

/** Every position in order: steps, with steps 4 and 5 expanded into their parts. */
export function wizardPositions(steps: readonly StepId[]): WizardPosition[] {
  return steps.flatMap(step => (partsOf(step).length ? partsOf(step).map(part => ({ step, part })) : [{ step }]));
}

/** The part a field path belongs to: `costCentres.0.code` → costCentres, `rules.workWeek.weekendDays` → workRules. */
export function partOfField(field: string | null | undefined): PartId | undefined {
  const f = normalizeFieldPath(field ?? '');
  const head = f.split('.')[0];
  if (head === 'costCentres' || head === 'departments' || head === 'grades' || head === 'designations') return head;
  if (/^rules\.(workWeek|employeeNumbering)/.test(f) || head === 'workWeek' || head === 'employeeNumbering' || head === 'employeeIdRule') return 'workRules';
  if (/^rules\.(leavePolicies|holidayCalendar)/.test(f) || head === 'leavePolicy' || head === 'holidayCalendar') return 'leave';
  if (head === 'gccSetting' || head === 'endOfService') return 'endOfService';
  return undefined;
}

/** Where a field lives: its step, and its part when that step has parts. */
export function positionOfField(field: string | null | undefined, fallbackStep: StepId = 1): WizardPosition {
  const f = normalizeFieldPath(field ?? '');
  const part = partOfField(f);
  if (part) return { step: stepOfPart(part), part };
  if (f.startsWith('company.') || f === 'company') return { step: 2 };
  if (f.startsWith('branches') || f === 'locations') return { step: 3 };
  if (f.startsWith('rules.')) return { step: 5, part: 'workRules' };
  if (f.startsWith('companyAdmin')) return { step: 6 };
  return { step: fallbackStep };
}

/** Plan areas of step 4 and 5 by part, so a conflict on review opens the right part. */
export const AREA_PART: Partial<Record<PlanArea, PartId>> = {
  costCentre: 'costCentres', department: 'departments', grade: 'grades', designation: 'designations',
  workWeek: 'workRules', employeeIdRule: 'workRules', leavePolicy: 'leave', holidayCalendar: 'leave', gccSetting: 'endOfService',
};

/** Blockers of one position: a part's share of its step's blockers, or the whole step. */
export function validatePosition(position: WizardPosition, input: ValidationInput): FieldError[] {
  const errors = validateStep(position.step, input);
  if (!position.part) return errors;
  return errors.filter(e => partOfField(e.field) === position.part);
}

/** The first position (in order) that still blocks, or null. The rail and Next cannot pass it. */
export function firstBlockingPosition(positions: readonly WizardPosition[], input: ValidationInput): WizardPosition | null {
  const byStep = new Map<StepId, FieldError[]>();
  for (const p of positions) {
    if (!byStep.has(p.step)) byStep.set(p.step, validateStep(p.step, input));
    const errors = byStep.get(p.step)!;
    if (p.part ? errors.some(e => partOfField(e.field) === p.part) : errors.length > 0) return p;
  }
  return null;
}

/** Resume: the first part of the step with a blocker, else the part the URL names, else the step's first part. */
export function resumePart(step: StepId, urlPart: string | null | undefined, input: ValidationInput): PartId | undefined {
  const parts = partsOf(step);
  if (parts.length === 0) return undefined;
  const blocked = parts.find(part => validatePosition({ step, part }, input).length > 0);
  if (blocked) return blocked;
  if (isPartId(urlPart) && parts.includes(urlPart)) return urlPart;
  return parts[0];
}

export type PartState = 'notStarted' | 'done' | 'blocked' | 'review' | 'skipped';

/** Rail state of a part: Skipped (neutral) when cost centres were left empty on purpose; review for grade-band advice. */
export function partState(part: PartId, input: ValidationInput & { visited: ReadonlySet<string>; serverProblems?: readonly Problem[] }): PartState {
  const step = stepOfPart(part);
  if (!input.visited.has(positionKey({ step, part }))) return 'notStarted';
  if (validatePosition({ step, part }, input).length > 0) return 'blocked';
  if ((input.serverProblems ?? []).some(p => partOfField(p.field) === part)) return 'blocked';
  if (part === 'costCentres' && input.body.costCentres.length === 0) return 'skipped';
  if (part === 'grades' && gradeWarnings(input.body.grades).length > 0) return 'review';
  return 'done';
}

/** A step with parts, from its parts: blocked or review if any part is; done once every part is done or skipped. */
export function stepStateFromParts(states: readonly PartState[]): StepState {
  if (states.every(s => s === 'notStarted')) return 'notStarted';
  if (states.includes('blocked')) return 'blocked';
  if (states.includes('notStarted')) return 'inProgress';
  if (states.includes('review')) return 'review';
  return 'done';
}

/** Address of a position on the wizard's own page: `draft=` stays, `part=` follows the part. */
export function withPart(href: string, part: PartId | undefined): string {
  const [path, query = ''] = href.split('?');
  const params = new URLSearchParams(query);
  if (part) params.set('part', part); else params.delete('part');
  const q = params.toString();
  return q ? `${path}?${q}` : path;
}

// ── Organisation sections: what changed, and what the app can suggest (deterministic, never silent) ──

type OrgRow = { code: string; nameEn?: string; titleEn?: string; nameAr?: string; titleAr?: string } & ExistingRow;
const ORG_FIELDS: Record<OrgSection, readonly string[]> = {
  costCentres: ['code', 'nameEn', 'nameAr'],
  departments: ['code', 'nameEn', 'nameAr', 'parentCode', 'branchCode', 'costCentreCode'],
  grades: ['code', 'nameEn', 'nameAr', 'level', 'minSalary', 'midSalary', 'maxSalary'],
  designations: ['code', 'titleEn', 'titleAr', 'departmentCode', 'gradeCode'],
};
export const orgFields = (section: OrgSection) => ORG_FIELDS[section];

/** Rows the section would add and change compared with what the company has (existing mode). */
export function sectionChanges(body: CompanySetupDraftBody, baseline: CompanySetupDraftBody | null | undefined, section: OrgSection): { added: number; changed: number } {
  const rows = body[section] as unknown as OrgRow[];
  const before = (baseline?.[section] ?? []) as unknown as OrgRow[];
  const fields = ORG_FIELDS[section] as (keyof OrgRow)[];
  let added = 0, changed = 0;
  for (const row of rows) {
    if (!row.existingId) added += 1;
    else if (rowChanged(row, before, fields)) changed += 1;
  }
  return { added, changed };
}

const STOP_WORDS = new Set(['and', 'of', 'the', 'for', 'to', 'in', '&']);
const NOISE_WORDS = new Set(['department', 'dept', 'cost', 'center', 'centre', 'division', 'unit', 'team', 'office']);
const words = (name: string) => name.toLowerCase().replace(/[^\p{L}\p{N}&]+/gu, ' ').split(' ').filter(Boolean);

/** A name reduced for comparison: lower case, punctuation and filler words dropped ("HR Department" → "hr"). */
export function normaliseName(name: string | null | undefined): string {
  return words(name ?? '').filter(w => !STOP_WORDS.has(w) && !NOISE_WORDS.has(w)).join(' ');
}

const initials = (name: string) => words(name).filter(w => !STOP_WORDS.has(w) && !NOISE_WORDS.has(w)).map(w => (/^\d+$/.test(w) ? w : w[0])).join('');

/** True when two names mean the same row: equal once normalised, or one is the other's initials (HR ↔ Human Resources). */
export function namesLookAlike(a: string | null | undefined, b: string | null | undefined): boolean {
  const na = normaliseName(a), nb = normaliseName(b);
  if (!na || !nb) return false;
  if (na === nb) return true;
  const compact = (s: string) => s.replace(/\s+/g, '');
  const ia = initials(a ?? ''), ib = initials(b ?? '');
  return (ia.length >= 2 && compact(nb) === ia && na.includes(' ')) || (ib.length >= 2 && compact(na) === ib && nb.includes(' '));
}

/** The other row of this list whose name or code looks like this row's name, or -1. */
export function lookAlikeIndex<T extends OrgRow>(rows: readonly T[], index: number, nameOf: (row: T) => string | undefined): number {
  const name = nameOf(rows[index]);
  if (!normaliseName(name)) return -1;
  return rows.findIndex((row, i) => i !== index && (namesLookAlike(name, nameOf(row)) || (!!row.code && normaliseName(row.code) === normaliseName(name))));
}

/**
 * A code from the English name, unique among `taken`: initials for several words ("Human Resources" → "HR",
 * "Grade 3" → "G3"), the first three letters for one ("Finance" → "FIN"). Empty when the name gives nothing.
 */
export function suggestCode(name: string | null | undefined, taken: Iterable<string>, prefix = ''): string {
  const meaningful = words(name ?? '').filter(w => !STOP_WORDS.has(w) && !NOISE_WORDS.has(w) && /^[a-z0-9]+$/.test(w));
  if (meaningful.length === 0) return '';
  const core = (meaningful.length === 1 ? meaningful[0].slice(0, 3) : meaningful.map(w => (/^\d+$/.test(w) ? w : w[0])).join('')).toUpperCase().slice(0, 8);
  const used = new Set([...taken].map(c => c.trim().toUpperCase()));
  const base = `${prefix}${core}`;
  if (!used.has(base)) return base;
  for (let n = 2; n < 100; n += 1) if (!used.has(`${base}${n}`)) return `${base}${n}`;
  return '';
}

/** The cost centre whose name starts with the department's name ("Finance" → "Finance Cost Centre"), for the user to confirm. */
export function suggestCostCentre(departmentName: string | null | undefined, costCentres: readonly { code: string; nameEn: string }[]): string | undefined {
  const dept = normaliseName(departmentName);
  if (!dept) return undefined;
  const match = costCentres.find(cc => cc.code.trim() && (normaliseName(cc.nameEn) === dept || normaliseName(cc.nameEn).startsWith(`${dept} `)));
  return match?.code.trim();
}

/** "Arabic names: 3 of 9" — filled Arabic names among rows that have a code or name. */
export function arabicProgress<T>(rows: readonly T[], arabic: (row: T) => string | undefined): { filled: number; total: number } {
  return { filled: rows.filter(r => !blank(arabic(r))).length, total: rows.length };
}

/** Grades in level order (then code), as indexes into the draft's list, which itself keeps its order. */
export function sortedGradeIndexes(grades: readonly Pick<CompanySetupGrade, 'level' | 'code'>[]): number[] {
  return grades.map((_, i) => i).sort((a, b) => (grades[a].level || 0) - (grades[b].level || 0) || grades[a].code.localeCompare(grades[b].code) || a - b);
}

/** Non-blocking advice on grade bands, per field: shown in amber, never stops Save & next. */
export function gradeWarnings(grades: readonly CompanySetupGrade[]): FieldError[] {
  const warnings: FieldError[] = [];
  const order = sortedGradeIndexes(grades).filter(i => grades[i].code.trim() || grades[i].nameEn.trim());
  const byLevel = new Map<number, number>();
  for (const i of order) {
    const g = grades[i];
    const prior = byLevel.get(g.level);
    if (g.level >= 1 && prior !== undefined)
      warnings.push({ field: `grades.${i}.level`, message: msg('Level {level} is also used by {code}. Give each grade its own level, unless they really are the same level.'), params: { level: g.level, code: grades[prior].code || '—' } });
    else byLevel.set(g.level, i);
    if ((g.minSalary || 0) === 0 && (g.midSalary || 0) === 0 && (g.maxSalary || 0) === 0)
      warnings.push({ field: `grades.${i}.minSalary`, message: msg('No salary band yet. Enter the minimum and maximum, or leave it for finance to fill in.') });
  }
  for (let k = 1; k < order.length; k += 1) {
    const lower = grades[order[k - 1]], higher = grades[order[k]];
    if (higher.level > lower.level && higher.minSalary > 0 && higher.minSalary < lower.minSalary)
      warnings.push({ field: `grades.${order[k]}.minSalary`, message: msg('The minimum is below {code}’s minimum, though this grade is higher. Check the band.'), params: { code: lower.code || '—' } });
  }
  return warnings;
}

/** "Use the middle": the midpoint of a band whose midpoint is empty, rounded to a whole amount. */
export function suggestedMidpoint(grade: Pick<CompanySetupGrade, 'minSalary' | 'midSalary' | 'maxSalary'>): number | null {
  if ((grade.midSalary || 0) > 0 || !(grade.minSalary > 0) || !(grade.maxSalary > grade.minSalary)) return null;
  return Math.round((grade.minSalary + grade.maxSalary) / 2);
}

/**
 * Copy from a sister company (B3): append the copied rows whose code is not already in the list. Nothing already
 * here is changed. Returns the new list and how many were skipped because their code is taken.
 */
export function appendCopiedRows<T extends { code: string }>(current: readonly T[], copied: readonly T[]): { rows: T[]; added: number; skipped: number } {
  const taken = new Set(current.map(r => norm(r.code)).filter(Boolean));
  const fresh: T[] = [];
  let skipped = 0;
  for (const row of copied) {
    const code = norm(row.code);
    if (!code || taken.has(code)) { skipped += 1; continue; }
    taken.add(code);
    // Copied by value: a new row of this company, never linked to the source (no id, no baseline).
    const { existingId: _id, baseline: _baseline, ...rest } = row as T & ExistingRow & WithBaseline;
    fresh.push(rest as unknown as T);
  }
  return { rows: [...current, ...fresh], added: fresh.length, skipped };
}
