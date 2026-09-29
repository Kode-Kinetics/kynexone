import client from './client';
import type { EstablishmentRow } from './planning';

// ── Staffing levels (tenant-configurable catalog — seeded data, never an enum) ──

export interface StaffingLevelDto {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  rank: number;
  isActive: boolean;
}

export interface StaffingLevelRequest {
  code: string;
  nameEn: string;
  nameAr?: string;
  rank: number;
  isActive?: boolean;
}

// ── Establishment matrix (per-department, per-level budgets) ──────────────────

/** One level row inside a department's matrix. `budgeted: null` = uncontrolled ("—"), `0` = frozen. */
export interface MatrixLevelCell {
  staffingLevelId: string;
  levelCode: string;
  levelNameEn: string;
  levelNameAr: string;
  budgeted: number | null;
  current: number;
  gap: number | null;
  exitingIncumbents: number;
  positionsAtLevel: number;
  positionsExceedBudget: boolean;
}

/** Superset of the legacy planning EstablishmentRow — existing envelope columns keep working. */
export interface MatrixRow extends EstablishmentRow {
  allocated: number;
  unallocated: number;
  managerEmployeeId: number | null;
  levels: MatrixLevelCell[];
  unclassifiedCount: number;
  unresolvedDepartmentCount: number;
}

/**
 * What `GET /api/establishment/matrix` actually returns. The rows live under `departments`;
 * the two siblings are tenant-wide facts that do not belong on any single row.
 */
export interface MatrixResponse {
  enforcementMode?: string | null;
  unresolvedDepartmentCount?: number | null;
  departments?: MatrixRow[] | null;
}

/** The property carrying the rows. Pinned by a contract test so the two sides cannot diverge. */
export const MATRIX_ENVELOPE_KEY = 'departments' as const;

/**
 * Read the rows out of whatever the endpoint returned. Accepts the envelope (current contract)
 * and a bare array (the shape this client wrongly assumed), and degrades to an empty list rather
 * than handing a non-iterable to a caller that will spread it.
 */
export function unwrapMatrix(data: unknown): MatrixRow[] {
  if (Array.isArray(data)) return data as MatrixRow[];
  if (data && typeof data === 'object') {
    const rows = (data as Record<string, unknown>)[MATRIX_ENVELOPE_KEY];
    if (Array.isArray(rows)) return rows as MatrixRow[];
  }
  return [];
}

export interface BudgetRowUpdate {
  staffingLevelId: string;
  /** null deletes the row → level returns to uncontrolled (unlimited). */
  budgetedHeadcount: number | null;
}

export interface BudgetSaveWarning {
  staffingLevelId: string;
  belowOccupancy?: boolean;
  sumExceedsEnvelope?: boolean;
  message?: string;
}

export interface BudgetSaveResult {
  warnings?: BudgetSaveWarning[];
}

// ── Level-mapping (designation → staffing level; preview → approve, opt-in) ───

export interface MappingSuggestion {
  designationId: string;
  titleEn: string;
  currentLevelId: string | null;
  suggestedLevelId: string | null;
  suggestedLevelName: string | null;
  basis: 'jobLevel' | 'levelRank' | null;
}

export interface MappingImpactCount {
  departmentId: string;
  departmentName: string;
  levelCode: string;
  levelNameEn: string;
  budgeted: number;
  projectedCurrent: number;
}

// ── Blocked-assignment 409 contract (ESTABLISHMENT_BUDGET_EXCEEDED) ───────────

export interface EstablishmentBlockedPayload {
  error: 'ESTABLISHMENT_BUDGET_EXCEEDED';
  departmentId: string;
  departmentName: string;
  staffingLevelId: string;
  levelCode: string;
  levelNameEn: string;
  levelNameAr: string;
  budgeted: number;
  current: number;
  attempted: number;
  exitingIncumbents: number;
  canEditEstablishment: boolean;
}

/**
 * Returns the structured ESTABLISHMENT_BUDGET_EXCEEDED payload when the given
 * error is the establishment guard's 409, otherwise null. Use in every catch
 * site that mutates department/designation assignment.
 */
export function establishmentBlockFromError(err: unknown): EstablishmentBlockedPayload | null {
  const e = err as { response?: { status?: number; data?: { error?: string } } };
  if (e?.response?.status === 409 && e.response.data?.error === 'ESTABLISHMENT_BUDGET_EXCEEDED') {
    return e.response.data as EstablishmentBlockedPayload;
  }
  return null;
}

export const establishmentApi = {
  levels: () =>
    client.get<StaffingLevelDto[]>('/api/establishment/levels').then(r => r.data),

  createLevel: (body: StaffingLevelRequest) =>
    client.post<StaffingLevelDto>('/api/establishment/levels', body).then(r => r.data),

  updateLevel: (id: string, body: StaffingLevelRequest) =>
    client.put<StaffingLevelDto>(`/api/establishment/levels/${id}`, body).then(r => r.data),

  deactivateLevel: (id: string) =>
    client.post(`/api/establishment/levels/${id}/deactivate`, {}).then(r => r.data),

  deleteLevel: (id: string) =>
    client.delete(`/api/establishment/levels/${id}`).then(r => r.data),

  /**
   * The endpoint returns an ENVELOPE, not a bare array:
   *   { enforcementMode, unresolvedDepartmentCount, departments: MatrixRow[] }
   *
   * It was typed and read as `MatrixRow[]`, so `setRows(...)` stored the object and the grouping
   * `useMemo` in EstablishmentPanel then did `for (const r of rows)` and threw
   * `TypeError: … is not iterable` — taking the whole Setup page down behind the error boundary
   * for anyone who opened Cost Centres & Budget. Reproduced against production on 2026-09-23.
   *
   * Same defect class as the employee field catalogue (`{fields: […]}` read as an array): the
   * server's shape was correct and stable the whole time; the client's reading of it was wrong.
   * A bare array is still accepted, so no deploy ordering can blank the screen.
   */
  matrix: () =>
    client.get<MatrixResponse | MatrixRow[]>('/api/establishment/matrix')
      .then(r => unwrapMatrix(r.data)),

  /** Reason is mandatory server-side; every budget mutation is audited with before/after per level. */
  saveBudgets: (departmentId: string, rows: BudgetRowUpdate[], reason: string) =>
    client.put<BudgetSaveResult>(`/api/establishment/departments/${departmentId}/budgets`, { rows, reason }).then(r => r.data),

  mappingSuggestions: () =>
    client.get<MappingSuggestion[]>('/api/establishment/level-mapping/suggestions').then(r => r.data),

  mappingImpact: (designationId: string, staffingLevelId: string | null) =>
    client.get<MappingImpactCount[]>('/api/establishment/level-mapping/impact', {
      params: { designationId, staffingLevelId },
    }).then(r => r.data),

  applyMapping: (pairs: Array<{ designationId: string; staffingLevelId: string | null }>) =>
    client.post('/api/establishment/level-mapping/apply', { pairs }).then(r => r.data),
};
