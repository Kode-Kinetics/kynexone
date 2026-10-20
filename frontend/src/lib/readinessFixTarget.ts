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
}

export function readinessFixTarget(item: Pick<ReadinessItem, 'category' | 'fix'>): ReadinessFixTarget {
  const category = (item.category ?? '').trim().toLowerCase();
  if (item.fix?.kind === 'document' || category === 'document') return { section: 'compliance-documents', edit: false, panel: 'documents' };
  const field = item.fix?.kind === 'field' || item.fix === undefined;
  switch (category) {
    case 'identity':
      return { section: 'compliance-documents', edit: field };
    case 'payroll':
      return { section: 'pay-benefits', edit: field, ...(item.fix?.kind === 'field' && item.fix.target === 'salary' ? { panel: 'salary' } : {}) };
    case 'personal':
      return { section: 'personal', edit: field };
    case 'org':
    case 'contract':
      return { section: 'employment', edit: field };
    case 'compliance':
      return { section: 'compliance-documents', edit: false };
    default:
      return { section: 'employment', edit: false };
  }
}

export function readinessFixHref(employeeId: number, item: Pick<ReadinessItem, 'category' | 'fix'>, returnTo?: string): string {
  const target = readinessFixTarget(item);
  return employeeWorkspaceHref(employeeId, { section: target.section, panel: target.panel, edit: target.edit, returnTo });
}
