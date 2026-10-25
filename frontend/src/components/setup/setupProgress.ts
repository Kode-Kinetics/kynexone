import { msg } from '../../i18n/translations';
import type { ManageableCompany, ReadinessItem } from '../../api/companySetup';
import type { SetupAreaId, SetupSettingId } from '../../lib/setupAreas';

/**
 * Setup progress, from persisted data only (unit/setupProgress.spec.ts). Two sources, never invented numbers:
 *
 * 1. Guided setup's checklist: GET /api/company-setup/context gives each manageable company's readinessDone/Total
 *    (the "N of M done" on Guided setup's chooser) and GET /api/company-setup/readiness/{id} lists those same items.
 *    The server computes both from one function (CompanySetupReadiness.ComputeAsync).
 * 2. Record checks: a settings section is done when its list endpoint returns at least one active record.
 *
 * An area's status counts only items one of these can answer. When a source fails or a section has no check, the item
 * is left out, never assumed; an area with nothing left shows no status at all.
 */

/** Readiness items that belong to an All setup areas destination. The rest (company super admin, email delivery,
 *  workspace settings, record numbering) count in Guided setup's total but sit in no area. */
export const READINESS_DESTINATION: Readonly<Record<string, { areaId: SetupAreaId; destinationId: string }>> = {
  establishment: { areaId: 'organization', destinationId: 'establishment' },
  masterData: { areaId: 'organization', destinationId: 'masterData' },
  locations: { areaId: 'time', destinationId: 'locations' },
  glMapping: { areaId: 'payroll', destinationId: 'glMapping' },
  fiscalYears: { areaId: 'payroll', destinationId: 'fiscalYears' },
  bankWps: { areaId: 'payroll', destinationId: 'payroll' },
  notificationTemplates: { areaId: 'services', destinationId: 'notificationTemplates' },
  gccSettings: { areaId: 'compliance', destinationId: 'gccSettings' },
  statutoryRules: { areaId: 'compliance', destinationId: 'statutoryRules' },
  // The Nitaqat establishment profile is set on the Saudization tab of the Saudi compliance dashboard, not in Setup's
  // Saudi compliance section (Qiwa, GOSI, WPS), so this item leads to the dashboard.
  nitaqat: { areaId: 'compliance', destinationId: 'saudiComplianceDashboard' },
};

/** The settings whose list endpoint answers "is there at least one active record". */
export const RECORD_CHECKED_SECTIONS = ['companies', 'branches', 'departments', 'designations', 'grades', 'costCenters', 'locations', 'masterData', 'fiscalYears', 'notificationTemplates', 'gccSettings'] as const satisfies readonly SetupSettingId[];
export type RecordCheckedSection = typeof RECORD_CHECKED_SECTIONS[number];
/** Active-record counts per section; null when the list could not be read (so the item is not measurable). */
export type RecordCounts = Partial<Record<RecordCheckedSection, number | null>>;

/** The first-step wording for an item that is not done yet, by destination. */
export const NEXT_ACTION_LABEL: Readonly<Record<string, string>> = {
  companies: msg('Add your first company'),
  branches: msg('Add your first branch'),
  departments: msg('Add your first department'),
  designations: msg('Add your first designation'),
  grades: msg('Add your first grade'),
  costCenters: msg('Add your first cost centre'),
  establishment: msg('Set up headcount & budget'),
  masterData: msg('Create your first drop-down list'),
  locations: msg('Add your first location'),
  glMapping: msg('Set up payroll accounting'),
  fiscalYears: msg('Add your first fiscal year'),
  payroll: msg('Add bank and WPS details'),
  notificationTemplates: msg('Add your first notification'),
  gccSettings: msg('Set up country rules'),
  statutoryRules: msg('Set up statutory rules'),
  saudiComplianceDashboard: msg('Set up the Nitaqat profile'),
};

export interface AreaProgressItem {
  destinationId: string;
  done: boolean;
  source: 'guided' | 'records';
}

export interface AreaStatus {
  done: number;
  total: number;
  state: 'notStarted' | 'partial' | 'done';
}

/**
 * The measurable items of one area, in the area's destination order. `destinationIds` are the destinations this user
 * can open there (unavailable ones are never counted). A readiness item for the destination wins over a record check,
 * so an area never contradicts Guided setup. `readiness` is null when no company is selected or it could not be read.
 */
export function areaProgressItems(areaId: SetupAreaId, destinationIds: readonly string[], readiness: readonly ReadinessItem[] | null, records: RecordCounts): AreaProgressItem[] {
  const items: AreaProgressItem[] = [];
  for (const destinationId of destinationIds) {
    // Workspace-wide readiness items are listed only while unconfigured; once configured, the record check answers.
    const guided = readiness?.find(item => {
      const target = READINESS_DESTINATION[item.key];
      return target?.areaId === areaId && target.destinationId === destinationId;
    });
    if (guided) { items.push({ destinationId, done: guided.status === 'done', source: 'guided' }); continue; }
    if ((RECORD_CHECKED_SECTIONS as readonly string[]).includes(destinationId)) {
      const count = records[destinationId as RecordCheckedSection];
      if (typeof count === 'number') items.push({ destinationId, done: count > 0, source: 'records' });
    }
  }
  return items;
}

/** "Not started", "{x} of {y}" or "Done"; null for an area with no measurable item (it shows no status). */
export function areaStatus(items: readonly AreaProgressItem[]): AreaStatus | null {
  if (items.length === 0) return null;
  const done = items.filter(item => item.done).length;
  return { done, total: items.length, state: done === 0 ? 'notStarted' : done === items.length ? 'done' : 'partial' };
}

/** The area's next action: its first measurable item that is not done, or null when every one is. */
export function firstIncomplete(items: readonly AreaProgressItem[]): AreaProgressItem | null {
  return items.find(item => !item.done) ?? null;
}

/** Active records in a list response; null when the response is not a list (the check could not be made). */
export function activeCount(rows: unknown, isActive: (row: Record<string, unknown>) => boolean = row => row.isActive !== false): number | null {
  return Array.isArray(rows) ? rows.filter(row => row && typeof row === 'object' && isActive(row as Record<string, unknown>)).length : null;
}

/**
 * The company whose Guided setup progress Setup shows: the selected company when the caller can set it up, otherwise
 * the only company they can, otherwise none (a group scope with several companies has no single number to show).
 */
export function progressCompany(companies: readonly ManageableCompany[] | null | undefined, selectedCompanyId: string | null): ManageableCompany | null {
  if (!companies || companies.length === 0) return null;
  if (selectedCompanyId) return companies.find(company => company.id === selectedCompanyId) ?? null;
  return companies.length === 1 ? companies[0] : null;
}

/** Guided setup's checklist items that are not done, in the server's order (what the progress line drills down to). */
export function missingReadinessItems(items: readonly ReadinessItem[] | null | undefined): ReadinessItem[] {
  return (items ?? []).filter(item => item.status !== 'done');
}
