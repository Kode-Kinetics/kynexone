import type { RawDepartmentPreview } from '../api/organization';
import type { ImportPreview } from '../components/ImportExportToolbar';
import { linksToClearFrom } from './costCentreMaster';

const OUTCOMES = ['ok', 'warning', 'error', 'skipped'] as const;
const outcomeOf = (status: number | string): (typeof OUTCOMES)[number] => {
  if (typeof status === 'number') return OUTCOMES[status] ?? 'ok';
  const s = status.toLowerCase();
  return (OUTCOMES as readonly string[]).includes(s) ? (s as (typeof OUTCOMES)[number]) : 'ok';
};

/**
 * A department dry run in the import review's shape. `org` carries what is specific to this kind of file (the table of
 * rows and how many existing links it would clear); the employee-only fields are left out.
 */
export function toDepartmentPreview(raw: RawDepartmentPreview, linksToClearHeader?: string): ImportPreview {
  const rows = (raw.rows ?? []).map((r) => ({
    row: r.rowNumber,
    code: r.entityCode ?? '',
    name: r.entityName ?? '',
    outcome: outcomeOf(r.status),
    errors: r.errors ?? [],
    warnings: r.warnings ?? [],
  }));
  return {
    received: raw.received,
    wouldCreate: raw.wouldCreate,
    wouldSkip: raw.wouldSkip,
    rows: rows.map((r) => ({ row: r.row, fullName: r.name, employeeCode: r.code, status: r.outcome === 'error' ? 'Error' : 'WillCreate', errors: r.errors, warnings: r.warnings })),
    org: { rows, wouldUpdate: raw.wouldUpdate, linksToClear: linksToClearFrom(linksToClearHeader, rows) },
  };
}
