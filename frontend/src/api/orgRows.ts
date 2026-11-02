import client from './client';

/**
 * Setup's one Remove / Restore for organisation rows (owner decision 2026-10-25). The server decides: a row nothing
 * points at is deleted; a row in use (or with history) is turned off and can be restored. The response says which.
 */
export type OrgRowKind = 'branch' | 'department' | 'designation' | 'grade' | 'costCenter' | 'location' | 'masterDataValue';

const PATH: Record<OrgRowKind, string> = {
  branch: '/api/branches',
  department: '/api/departments',
  designation: '/api/designations',
  grade: '/api/grades',
  costCenter: '/api/cost-centers',
  location: '/api/admin/locations',
  masterDataValue: '/api/admin/master-data/values',
};

export interface OrgRowUsageItem { kind: string; label: string; count: number }

export interface OrgRowRemoval {
  outcome: 'deleted' | 'turnedOff';
  /** Plain-language sentence from the server, e.g. "Turned off. Used by 12 employees. It stays in history and can be restored anytime." */
  message: string;
  usage: { total: number; summary: string; items: OrgRowUsageItem[] };
}

export const orgRowsApi = {
  remove: (kind: OrgRowKind, id: string) => client.delete<OrgRowRemoval>(`${PATH[kind]}/${id}`).then(r => r.data),
  restore: (kind: OrgRowKind, id: string) => client.post<{ outcome: 'restored'; message: string }>(`${PATH[kind]}/${id}/restore`).then(r => r.data),
};

/**
 * The same Remove / Restore contract for a Setup row that is not an organisation row (leave types, loan types,
 * templates, checklists, roles...). `path` is the collection route; the server answers DELETE `{path}/{id}` with an
 * `OrgRowRemoval` body and POST `{path}/{id}/restore`. `acknowledgeParam` names the query flag a confirmed warning sends, and `acknowledgeValue` what it carries
 * (a server that verifies the warning wants its code echoed back, e.g. LEAVE_TYPE_STATUTORY, not a bare true).
 */
export function setupRowApi(path: string, acknowledgeParam = 'acknowledge', acknowledgeValue: string | boolean = true) {
  return {
    remove: (id: string, opts?: { acknowledge?: boolean }) =>
      client.delete<OrgRowRemoval>(`${path}/${id}`, opts?.acknowledge ? { params: { [acknowledgeParam]: acknowledgeValue } } : undefined).then(r => r.data),
    restore: (id: string) => client.post<{ outcome: 'restored'; message: string }>(`${path}/${id}/restore`).then(r => r.data),
  };
}
