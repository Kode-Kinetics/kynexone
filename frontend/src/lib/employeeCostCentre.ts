import { apiErrorCode } from './glCostAccounting';

/**
 * Cost accounting v1 (WP-F), screen 6 and AM-30: where an employee's cost centre comes from, and the request shapes that
 * change it. Kept free of React so the rules are unit-tested on their own (unit/employeeCostCentre.spec.ts).
 */

/** The explicit "None" for a form's cost-centre id: removes the override so the person inherits. Null/absent means unchanged. */
export const NO_COST_CENTRE = '00000000-0000-0000-0000-000000000000';

// ── Where it comes from ──────────────────────────────────────────────────────

export type SourceKind = 'employee' | 'position' | 'department' | 'parentDepartment' | 'companyDefault';
/** The step names in the server's own vocabulary (blocker `level`). */
export type SourceLevel = 'employee' | 'position' | 'department' | 'parentDepartment' | 'company';

interface CostCentreLike { id: string; code: string; name: string; nameAr?: string | null; companyId?: string | null; isActive: boolean }

export interface ResolveInput {
  companyId?: string | null;
  employee: { costCenterId?: string | null; departmentId?: string | null; positionId?: string | null; costCenterIsOverride?: boolean | null };
  departments: ReadonlyArray<{ id: string; nameEn: string; nameAr?: string | null; parentDepartmentId?: string | null; costCenterId?: string | null }>;
  positions: ReadonlyArray<{ id: string; title: string; costCenterId?: string | null }>;
  costCentres: ReadonlyArray<CostCentreLike>;
  /** undefined = the reader cannot see finance settings, so the company default is unknown (not "none"). */
  companyDefaultId?: string | null;
}

export type Resolution =
  | { kind: SourceKind; costCentreId: string; sourceName?: string }
  | { kind: 'none'; costCentreId: null }
  | { kind: 'unknown'; costCentreId: null }
  | { kind: 'invalid'; costCentreId: null; level: SourceLevel; code: string };

const MAX_DEPTH = 20;
const LEVEL: Record<SourceKind, SourceLevel> = { employee: 'employee', position: 'position', department: 'department', parentDepartment: 'parentDepartment', companyDefault: 'company' };

/**
 * Whether the stored cost centre is an override. The server's flag decides when the API sends it; until it does, a stored
 * cost centre that differs from the department's own is one: that is the rule the server applies when it writes it.
 */
export function isOverride(input: ResolveInput): boolean {
  const { employee } = input;
  if (typeof employee.costCenterIsOverride === 'boolean') return employee.costCenterIsOverride && !!employee.costCenterId;
  if (!employee.costCenterId) return false;
  const department = input.departments.find((d) => d.id === employee.departmentId);
  return employee.costCenterId !== (department?.costCenterId ?? null);
}

/** The server's order: employee override, position, department, parent department (loop-safe), company default. */
export function resolveCostCentre(input: ResolveInput, options: { ignoreOverride?: boolean } = {}): Resolution {
  const check = (id: string, kind: SourceKind, sourceName?: string): Resolution => {
    const cc = input.costCentres.find((c) => c.id === id);
    return cc && cc.isActive && (!input.companyId || cc.companyId === input.companyId)
      ? { kind, costCentreId: id, sourceName }
      : { kind: 'invalid', costCentreId: null, level: LEVEL[kind], code: cc?.code ?? id };
  };
  if (!options.ignoreOverride && isOverride(input) && input.employee.costCenterId) return check(input.employee.costCenterId, 'employee');

  const position = input.positions.find((p) => p.id === input.employee.positionId);
  if (position?.costCenterId) return check(position.costCenterId, 'position', position.title);

  const seen = new Set<string>();
  let cursor = input.employee.departmentId ?? null;
  for (let depth = 0; cursor && depth < MAX_DEPTH; depth++) {
    if (seen.has(cursor)) break;
    seen.add(cursor);
    const department = input.departments.find((d) => d.id === cursor);
    if (!department) break;
    if (department.costCenterId) return check(department.costCenterId, depth === 0 ? 'department' : 'parentDepartment', department.nameEn);
    cursor = department.parentDepartmentId ?? null;
  }

  if (input.companyDefaultId === undefined) return { kind: 'unknown', costCentreId: null };
  if (input.companyDefaultId) return check(input.companyDefaultId, 'companyDefault');
  return { kind: 'none', costCentreId: null };
}

// ── Request shapes (AM-30) ───────────────────────────────────────────────────

/** PUT /api/employees/{id}: setting or re-pointing the override. The reason is required (M-10) and audited. */
export function overridePatch(code: string, reason: string) {
  return { changes: { costCenter: code }, costCenterReason: reason.trim() };
}

/** PUT /api/employees/{id}: an empty code removes the override, so the person inherits. */
export function removeOverridePatch(reason: string) {
  return { changes: { costCenter: '' }, costCenterReason: reason.trim() };
}

/**
 * The create form's cost-centre fields. A user without organization.costcenter.manage sends none (the server refuses any,
 * 403). The department's own cost centre is a plain copy and needs no reason; a different one is an override and carries
 * one. Choosing "None" where the department has one sends the explicit None (Guid.Empty), never null.
 */
export function createCostCentreFields(input: { canManage: boolean; chosenId: string; departmentCostCentreId: string; reason: string }): { costCenterId?: string; costCenterReason?: string } {
  if (!input.canManage) return {};
  const { chosenId, departmentCostCentreId } = input;
  if (!chosenId) return departmentCostCentreId ? { costCenterId: NO_COST_CENTRE } : {};
  if (chosenId === departmentCostCentreId) return { costCenterId: chosenId };
  return { costCenterId: chosenId, costCenterReason: input.reason.trim() };
}

/** True when the create form's choice is an override (a cost centre that is not the department's), so a reason is due. */
export function createNeedsReason(input: { canManage: boolean; chosenId: string; departmentCostCentreId: string }): boolean {
  return input.canManage && !!input.chosenId && input.chosenId !== input.departmentCostCentreId;
}

// ── Refusals ─────────────────────────────────────────────────────────────────

export type CostCentreErrorKind = 'reason' | 'locked' | 'forbidden' | 'other';

export function costCentreErrorKind(err: unknown): CostCentreErrorKind {
  const status = (err as { response?: { status?: number } } | null | undefined)?.response?.status;
  const code = apiErrorCode(err);
  if (status === 422 && code === 'cost_centre_reason_required') return 'reason';
  if (status === 409 && code === 'locked_into_approved_run') return 'locked';
  if (status === 403) return 'forbidden';
  return 'other';
}
