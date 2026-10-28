import type { HireForm } from './hireForm';
import type { HireTemplateDefaults } from '../../api/onboardingTemplates';

/** What applying a template changes, what it left alone, and what each changed field held before (for Undo). */
export interface TemplatePlan {
  patch: Partial<HireForm>;
  prior: Partial<HireForm>;
  /** Fields the template has a value for that were NOT filled because the person already supplied one. */
  kept: Array<keyof HireForm>;
}

const EMPTY: Partial<HireForm> = {
  departmentId: '', employmentType: '', contractType: '', probationEndDate: '', workLocation: '', managerId: '',
};

/** yyyy-mm-dd + days, in calendar terms (no time zone arithmetic). */
export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  const two = (n: number) => String(n).padStart(2, '0');
  return `${dt.getUTCFullYear()}-${two(dt.getUTCMonth() + 1)}-${two(dt.getUTCDate())}`;
}

/**
 * AC69: a template only ever FILLS. A field is filled when it is empty AND the person has not touched it (typed in it, or chosen
 * a value, even if they later cleared it). Anything the person supplied is kept and reported, never overwritten.
 */
export function planTemplate(
  form: HireForm, edited: ReadonlySet<keyof HireForm>, defaults: HireTemplateDefaults, departmentHeadId?: number | null,
): TemplatePlan {
  const want: Partial<HireForm> = {};
  if (defaults.departmentId) want.departmentId = defaults.departmentId;
  if (defaults.employmentType) want.employmentType = defaults.employmentType;
  if (defaults.contractType) want.contractType = defaults.contractType;
  if (defaults.workLocation) want.workLocation = defaults.workLocation;
  if (typeof defaults.probationDays === 'number' && defaults.probationDays > 0 && form.startDate) want.probationEndDate = addDays(form.startDate, defaults.probationDays);
  if (defaults.managerRule === 'department-head' && departmentHeadId != null) want.managerId = String(departmentHeadId);

  const patch: Partial<HireForm> = {};
  const prior: Partial<HireForm> = {};
  const kept: Array<keyof HireForm> = [];
  for (const key of Object.keys(want) as Array<keyof HireForm>) {
    const empty = (form[key] ?? '') === (EMPTY[key] ?? '');
    if (edited.has(key) || !empty) { kept.push(key); continue; }
    (patch as Record<string, unknown>)[key] = want[key];
    (prior as Record<string, unknown>)[key] = form[key];
  }
  return { patch, prior, kept };
}

/** Undo restores only the fields still holding what the template put there; a value the person changed since stays. */
export function planUndo(form: HireForm, applied: Partial<HireForm>, prior: Partial<HireForm>): Partial<HireForm> {
  const restore: Partial<HireForm> = {};
  for (const key of Object.keys(applied) as Array<keyof HireForm>) {
    if (form[key] === applied[key]) (restore as Record<string, unknown>)[key] = prior[key] ?? '';
  }
  return restore;
}
