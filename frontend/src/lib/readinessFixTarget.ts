import type { ReadinessItem } from '../api/employees';
import { employeeWorkspaceHref, type EmployeeSection } from './peopleRoutes';

/**
 * Where an open readiness requirement is fixed.
 *
 * The categories come from the server's field registry (EmployeeFieldRegistry): personal, org, contract,
 * identity, payroll — plus `document` for a required file and `compliance` for "no readiness policy applies".
 * The overview used to know only the gate value `pay` (never a category) and documents, so a missing Government
 * ID, Iqama or IBAN sent the user to the read-only Employment section. A field fix opens the EDIT flow on the
 * step that holds the field; a document opens the upload panel; anything else opens the matching section.
 */
export interface ReadinessFixTarget {
  section: EmployeeSection;
  edit: boolean;
  panel?: string;
  /** What the user needs to act on the target (all of them). Identity and payroll values are sensitive. */
  permissions: string[];
}

export function readinessFixTarget(item: Pick<ReadinessItem, 'category' | 'fix'>): ReadinessFixTarget {
  const category = (item.category ?? '').trim().toLowerCase();
  if (item.fix?.kind === 'document' || category === 'document') return { section: 'compliance-documents', edit: false, panel: 'documents', permissions: ['employees.documents'] };
  const field = item.fix?.kind === 'field' || item.fix === undefined;
  const editing = (sensitive: boolean) => field ? ['employees.write', ...(sensitive ? ['employees.sensitive'] : [])] : [];
  switch (category) {
    case 'identity':
      return { section: 'compliance-documents', edit: field, permissions: editing(true) };
    case 'payroll':
      return { section: 'pay-benefits', edit: field, permissions: editing(true), ...(item.fix?.kind === 'field' && item.fix.target === 'salary' ? { panel: 'salary' } : {}) };
    case 'personal':
      return { section: 'personal', edit: field, permissions: editing(false) };
    case 'org':
    case 'contract':
      return { section: 'employment', edit: field, permissions: editing(false) };
    case 'compliance':
      return { section: 'compliance-documents', edit: false, permissions: [] };
    default:
      return { section: 'employment', edit: false, permissions: [] };
  }
}

/** True when the user holds every permission the fix needs, so the action is not a dead end. */
export function canActOnReadinessFix(target: ReadinessFixTarget, hasPermission: (permission: string) => boolean): boolean {
  return target.permissions.every(hasPermission);
}

export function readinessFixHref(employeeId: number, item: Pick<ReadinessItem, 'category' | 'fix'>, returnTo?: string): string {
  const target = readinessFixTarget(item);
  return employeeWorkspaceHref(employeeId, { section: target.section, panel: target.panel, edit: target.edit, returnTo });
}
