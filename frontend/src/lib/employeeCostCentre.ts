import { apiErrorCode } from './glCostAccounting';
import { msg } from '../i18n/translations';

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

/** What the API's employee detail sends as `costCentreResolution`: the server's own answer to "where does this person's cost land". */
export interface ServerResolution {
  costCenterId: string | null;
  code?: string | null;
  name?: string | null;
  nameAr?: string | null;
  /** Employee | Position | Department | ParentDepartment | CompanyDefault | None. */
  source: string;
  level?: string | null;
  /** 'none', or '<level>_cost_centre_invalid'. */
  unresolvedReason?: string | null;
}

const KIND_OF_SOURCE: Record<string, SourceKind> = { Employee: 'employee', Position: 'position', Department: 'department', ParentDepartment: 'parentDepartment', CompanyDefault: 'companyDefault' };

export interface ResolveInput {
  companyId?: string | null;
  employee: { costCenterId?: string | null; departmentId?: string | null; positionId?: string | null; costCenterIsOverride?: boolean | null; costCentreResolution?: ServerResolution | null };
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
 * Whether the stored cost centre is an override. The server's flag decides when the API sends it, then the server's resolution
 * (its source is Employee only for an override); only for an older response does a stored cost centre that differs from the
 * department's own count as one: that is the rule the server applies when it writes it.
 */
export function isOverride(input: ResolveInput): boolean {
  const { employee } = input;
  if (typeof employee.costCenterIsOverride === 'boolean') return employee.costCenterIsOverride && !!employee.costCenterId;
  if (!employee.costCenterId) return false;
  if (employee.costCentreResolution) return employee.costCentreResolution.source === 'Employee';
  const department = input.departments.find((d) => d.id === employee.departmentId);
  return employee.costCenterId !== (department?.costCenterId ?? null);
}

/** The server's order: employee override, position, department, parent department (loop-safe), company default. */
export function resolveCostCentre(input: ResolveInput, options: { ignoreOverride?: boolean } = {}): Resolution {
  const inferred = inferCostCentre(input, options);
  const server = input.employee.costCentreResolution;
  // The server's own answer wins when the API sent it. What removing an override would return to is not what the server resolved
  // (it resolved the override), so that question stays an inference from the lists.
  if (!server || options.ignoreOverride) return inferred;
  const kind = KIND_OF_SOURCE[server.source];
  if (kind && server.costCenterId) {
    return inferred.kind === kind && 'sourceName' in inferred
      ? { kind, costCentreId: server.costCenterId, sourceName: inferred.sourceName }
      : { kind, costCentreId: server.costCenterId };
  }
  if (server.unresolvedReason && server.unresolvedReason !== 'none') {
    const level = (server.level as SourceLevel | null | undefined) ?? 'company';
    return { kind: 'invalid', costCentreId: null, level, code: inferred.kind === 'invalid' ? inferred.code : '' };
  }
  return { kind: 'none', costCentreId: null };
}

/** The same order worked out from the lists, for a response that has no server resolution. */
function inferCostCentre(input: ResolveInput, options: { ignoreOverride?: boolean } = {}): Resolution {
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

/** The preview endpoint's DTO (api/employees EmployeeCostCentrePreview), restated here so this file stays free of the API client. */
export interface PreviewLike {
  costCentreId: string | null;
  code: string | null;
  source: string;
  sourceName: string | null;
  sourceNameAr: string | null;
  level: string | null;
  invalidReason: string | null;
}

const LEVEL_OF_REASON: Array<[string, SourceLevel]> = [
  ['employee_', 'employee'], ['position_', 'position'], ['parent_department_', 'parentDepartment'], ['parentdepartment_', 'parentDepartment'],
  ['department_', 'department'], ['company_', 'company'],
];
const LEVELS: ReadonlySet<string> = new Set(['employee', 'position', 'department', 'parentDepartment', 'company']);

/** The preview endpoint's answer in the screen's terms. An unusable cost centre is `invalid` at its level, with only the code the server sent. */
export function previewResolution(dto: PreviewLike, locale: string): Resolution {
  if (dto.invalidReason) {
    const reason = dto.invalidReason.toLowerCase();
    const level = dto.level && LEVELS.has(dto.level) ? dto.level as SourceLevel : LEVEL_OF_REASON.find(([prefix]) => reason.startsWith(prefix))?.[1] ?? 'company';
    return { kind: 'invalid', costCentreId: null, level, code: dto.code ?? '' };
  }
  const kind = KIND_OF_SOURCE[dto.source];
  if (!kind || !dto.costCentreId) return { kind: 'none', costCentreId: null };
  const sourceName = (locale === 'ar' && dto.sourceNameAr ? dto.sourceNameAr : dto.sourceName) ?? undefined;
  return sourceName && kind !== 'employee' && kind !== 'companyDefault' ? { kind, costCentreId: dto.costCentreId, sourceName } : { kind, costCentreId: dto.costCentreId };
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
 * 403). Nothing chosen is "Follow department": nothing is sent and the person inherits. On a saved hire draft that already
 * has an override (`hadOverride`), following again sends the explicit None (Guid.Empty) so the server removes it. The
 * department's own cost centre is a plain copy and needs no reason; a different one is an override and carries one.
 */
export function createCostCentreFields(input: { canManage: boolean; chosenId: string; departmentCostCentreId: string; reason: string; hadOverride?: boolean }): { costCenterId?: string; costCenterReason?: string } {
  if (!input.canManage) return {};
  const { chosenId, departmentCostCentreId } = input;
  if (!chosenId) return input.hadOverride ? { costCenterId: NO_COST_CENTRE } : {};
  if (chosenId === departmentCostCentreId) return { costCenterId: chosenId };
  return { costCenterId: chosenId, costCenterReason: input.reason.trim() };
}

/** True when the create form's choice is an override (a cost centre that is not the department's), so a reason is due. */
export function createNeedsReason(input: { canManage: boolean; chosenId: string; departmentCostCentreId: string }): boolean {
  return input.canManage && !!input.chosenId && input.chosenId !== input.departmentCostCentreId;
}

// ── Refusals ─────────────────────────────────────────────────────────────────

export type CostCentreErrorKind = 'reason' | 'decision' | 'ref' | 'locked' | 'stale' | 'forbidden' | 'other';

const REF_CODES = new Set(['cost_centre_inactive', 'cost_centre_other_company', 'cost_centre_not_found']);

export function costCentreErrorKind(err: unknown): CostCentreErrorKind {
  const status = (err as { response?: { status?: number } } | null | undefined)?.response?.status;
  const code = apiErrorCode(err);
  if (status === 422 && (code === 'cost_centre_reason_required' || code === 'reason_invalid_characters' || code === 'reason_too_long')) return 'reason';
  if (status === 422 && (code === 'cost_centre_decision_required' || code === 'cost_centre_decision_invalid')) return 'decision';
  if (status === 422 && code && REF_CODES.has(code)) return 'ref';
  if (status === 409 && code === 'locked_into_approved_run') return 'locked';
  if (status === 409 && code === 'cost_centre_state_changed') return 'stale';
  if (status === 403) return 'forbidden';
  return 'other';
}

const REFUSAL_BY_CODE: Record<string, string> = {
  cost_centre_reason_required: msg('Enter a reason for this change.'),
  reason_invalid_characters: msg('The reason contains characters that are not allowed. Remove them and try again.'),
  cost_centre_decision_required: msg('Choose whether to keep the override or follow the new department.'),
  cost_centre_decision_invalid: msg('Choose whether to keep the override or follow the new department.'),
  cost_centre_inactive: msg('This cost centre is inactive. Choose an active one.'),
  cost_centre_other_company: msg('This cost centre belongs to another company. Choose one of this company’s cost centres.'),
  cost_centre_not_found: msg('This cost centre no longer exists. Choose another.'),
  cost_centre_state_changed: msg('Someone changed this employee’s cost centre since you opened the form. Reload the employee and try again.'),
};

/**
 * The words for a cost-centre refusal, by the server's code (a translation key; the caller runs it through t). Null when the
 * code is not a cost-centre one, so the caller falls back to the server's own sentence. 403 is the permission sentence.
 */
export function costCentreRefusalKey(err: unknown): string | null {
  const status = (err as { response?: { status?: number } } | null | undefined)?.response?.status;
  const code = apiErrorCode(err);
  if (code && REFUSAL_BY_CODE[code] && (status === 422 || status === 409 || status === 400)) return REFUSAL_BY_CODE[code];
  if (status === 403) return msg('Only HR managers and admins can change this.');
  return null;
}

// ── The edit flow (Wave A: D2 keep-or-follow on a transfer, D4 the dropdown) ─────────────

export type CostCentreDecision = 'keep' | 'drop';

export interface EditCostCentreInput {
  /** organization.costcenter.manage. Without it nothing about the cost centre is sent. */
  canManage: boolean;
  /** The server says the person has an override now. */
  isOverride: boolean;
  /** The override's cost-centre id (null without one). */
  overrideId: string | null;
  /** What the person inherits for the department in the form (the server preview without a cost centre). */
  inheritedId: string | null;
  /** The dropdown: undefined = untouched, '' = "Follow department", an id = that cost centre. */
  choice: string | undefined;
  /** The department in the form differs from the saved one. */
  departmentChanged: boolean;
  /** The answer to "keep the override or follow the new department?". */
  decision: CostCentreDecision | undefined;
  reason: string;
  /** Cost-centre id → code, for the PUT's `changes.costCenter`. */
  codeOf: (id: string) => string;
}

export interface EditCostCentrePatch {
  changes?: { costCenter: string };
  costCenterReason?: string;
  costCenterDecision?: CostCentreDecision;
  expectedCostCenterOverrideId?: string | null;
}

/** What the dropdown amounts to: no change, follow (remove any override), or a specific cost centre. */
function editIntent(input: EditCostCentreInput): { kind: 'none' } | { kind: 'follow' } | { kind: 'pick'; id: string } {
  const { choice } = input;
  if (choice === undefined) return { kind: 'none' };
  if (input.isOverride && choice === input.overrideId) return { kind: 'none' };
  if (choice === '' || choice === input.inheritedId) return input.isOverride ? { kind: 'follow' } : { kind: 'none' };
  return { kind: 'pick', id: choice };
}

/** The keep-or-follow question is due: an override, a department change, a manager, and no explicit pick in the dropdown. */
export function needsDecision(input: EditCostCentreInput): boolean {
  return input.canManage && input.isOverride && input.departmentChanged && editIntent(input).kind === 'none';
}

/** A reason is due whenever the PUT changes the override or carries a decision. */
export function editCostCentreNeedsReason(input: EditCostCentreInput): boolean {
  if (!input.canManage) return false;
  return editIntent(input).kind !== 'none' || needsDecision(input);
}

/**
 * The cost-centre part of PUT /api/employees/{id} for the edit flow. An explicit pick wins and is sent as `changes.costCenter`
 * (an empty code removes the override); the inherited cost centre is never sent as a code, because the server would store it as
 * an override. Without a pick, an override plus a department change sends the decision with the override id it was made
 * against (the server answers 409 cost_centre_state_changed when that is no longer true). Never both a code and a decision.
 */
export function editCostCentrePatch(input: EditCostCentreInput): EditCostCentrePatch {
  if (!input.canManage) return {};
  const intent = editIntent(input);
  const reason = input.reason.trim();
  if (intent.kind === 'follow') return { changes: { costCenter: '' }, costCenterReason: reason };
  if (intent.kind === 'pick') {
    const code = input.codeOf(intent.id);
    return code ? { changes: { costCenter: code }, costCenterReason: reason } : {};
  }
  if (needsDecision(input) && input.decision) {
    return { costCenterDecision: input.decision, costCenterReason: reason, expectedCostCenterOverrideId: input.overrideId };
  }
  return {};
}

// ── The dropdown ─────────────────────────────────────────────────────────────

export interface CostCentreOption { value: string; code: string; name: string; label: string; inherit?: true }

/**
 * The company's active cost centres as `CODE — name` (the Arabic name in Arabic, falling back to English). With `inherited`,
 * the first option is "follow" (value '') carrying the inherited code and name, and that cost centre is not listed again.
 * Without a company nothing is offered: never every company's list.
 */
export function costCentreOptions(
  list: ReadonlyArray<CostCentreLike>,
  companyId: string | null | undefined,
  locale: string,
  inherited: { id: string | null; code: string; name: string } | null,
): CostCentreOption[] {
  if (!companyId) return [];
  const nameOf = (c: CostCentreLike) => (locale === 'ar' && c.nameAr ? c.nameAr : c.name);
  const label = (code: string, name: string) => (code ? (name ? `${code} — ${name}` : code) : '');
  const rows = list
    .filter((c) => c.isActive && c.companyId === companyId && c.id !== inherited?.id)
    .map((c) => ({ value: c.id, code: c.code, name: nameOf(c), label: label(c.code, nameOf(c)) }));
  if (!inherited) return rows;
  return [{ value: '', code: inherited.code, name: inherited.name, label: label(inherited.code, inherited.name), inherit: true }, ...rows];
}
