import { employeeWorkspaceHref } from './peopleRoutes';
import type { ReadinessCheck, ReadinessSample } from '../api/financeGl';

/**
 * Cost accounting v1 (WP-F): the pure rules behind the GL screens. Kept free of React so the rules that decide what a
 * user may pick, and which sentence they read, are unit-tested on their own (unit/glCostAccounting.spec.ts).
 */

export const COST_CENTRE_RULES = ['Required', 'Optional', 'NotAllowed'] as const;
export type CostCentreRule = (typeof COST_CENTRE_RULES)[number];

/** Older chart rows say Revenue where newer ones say Income. */
export function isPnlType(type: string | null | undefined): boolean {
  return type === 'Expense' || type === 'Income' || type === 'Revenue';
}

/** Only profit-and-loss accounts can carry a cost centre, so only they can require one. */
export const canRequireCostCentre = isPnlType;

/** True when the rule cannot be saved on an account of this type (the server answers 422 for the same case). */
export function ruleBlockedForType(rule: string, type: string): boolean {
  return rule === 'Required' && !canRequireCostCentre(type);
}

/** A row from a server that predates the rule reads as the migration default. */
export function cleanRule(rule: string | null | undefined): CostCentreRule {
  return (COST_CENTRE_RULES as readonly string[]).includes(rule ?? '') ? (rule as CostCentreRule) : 'Optional';
}

/** '' keeps everything (All rules). */
export function matchesRuleFilter(account: { costCenterRule?: string | null }, filter: string): boolean {
  return !filter || cleanRule(account.costCenterRule) === filter;
}

// ── What the server said ─────────────────────────────────────────────────────

const text = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null);

/**
 * The server's own refusal sentence in the viewer's language: Arabic prefers `messageAr`, anything else prefers
 * `message`, and each falls back to the other. Null when the body carries neither, so the caller can describe the error.
 */
export function pickServerMessage(data: unknown, locale: string): string | null {
  if (!data || typeof data !== 'object') return null;
  const body = data as { message?: unknown; messageAr?: unknown };
  return locale === 'ar' ? text(body.messageAr) ?? text(body.message) : text(body.message) ?? text(body.messageAr);
}

export function apiErrorCode(err: unknown): string | null {
  const data = (err as { response?: { data?: { code?: unknown; error?: unknown } } } | null | undefined)?.response?.data;
  return text(data?.code) ?? text(data?.error);
}

/** M-7: a payroll run awaits finance review or lock, so the change would alter what it posts. */
export function isLockedIntoApprovedRun(err: unknown): boolean {
  const status = (err as { response?: { status?: number } } | null | undefined)?.response?.status;
  return status === 409 && apiErrorCode(err) === 'locked_into_approved_run';
}

// ═══════════════════════════ Mappings: functional class and fixed cost centre ═══════════════════════════

/** Server order. CAPITALISABLE is reserved: stored, but refused for posting in v1, so the UI shows it disabled. */
export const FUNCTIONAL_CLASSES = ['COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE'] as const;
export type FunctionalClass = (typeof FUNCTIONAL_CLASSES)[number];
export const RESERVED_CLASS: FunctionalClass = 'CAPITALISABLE';

interface CostCentreLike { id: string; companyId?: string | null; isActive: boolean }

/**
 * The cost centres a fixed posting rule may point at: the viewed company's own active ones. A group-level
 * (null-company) cost centre can never be stamped on a company journal, so it is never offered; and the group
 * view offers none, because a fixed cost centre is set per company (AM-4).
 */
export function fixedCostCentreOptions<T extends CostCentreLike>(costCenters: readonly T[], scope: string | null): T[] {
  if (!scope) return [];
  return costCenters.filter((c) => c.isActive && c.companyId === scope);
}

export interface ClassRuleDraft {
  cls: string;
  /** '' = no override of its own (an inherited rule, or one just added and not yet given an account). */
  accountId: string;
  inherited: boolean;
  inheritedAccountId: string | null;
}

/** One driver's editable state: the base row (every class) plus one extra row per functional class. */
export interface DriverDraft {
  driverKey: string;
  accountId: string;
  fixedCostCentreId: string;
  /** '' = any class. Picking one makes the base row that class's rule. */
  cls: string;
  classRules: ClassRuleDraft[];
}

export interface MappingRowShape {
  driverKey: string;
  mappedAccountId: string | null;
  segmentCostCenterId: string | null;
  inherited: boolean;
  classRules?: { functionalClass: string; accountId: string; inherited: boolean }[];
}

/**
 * A company view starts rows it only inherits unset (no override of its own), so saving never copies the group
 * mapping into the company; a group view, or an existing company override, starts filled.
 */
export function draftFromRow(row: MappingRowShape, inCompanyView: boolean): DriverDraft {
  return {
    driverKey: row.driverKey,
    accountId: row.inherited ? '' : row.mappedAccountId ?? '',
    fixedCostCentreId: row.inherited ? '' : row.segmentCostCenterId ?? '',
    cls: '',
    classRules: (row.classRules ?? []).map((r) => {
      const inherited = inCompanyView && r.inherited;
      return { cls: r.functionalClass, accountId: inherited ? '' : r.accountId, inherited, inheritedAccountId: inherited ? r.accountId : null };
    }),
  };
}

export interface MappingInput {
  driverKey: string;
  accountId: string;
  segmentCostCenterId: string | null;
  functionalClass: string | null;
}

/** The PUT body: the base row, then one row per class that has an account. Rows with no account are not sent. */
export function toMappingInputs(drafts: readonly DriverDraft[]): MappingInput[] {
  const out: MappingInput[] = [];
  for (const d of drafts) {
    if (d.accountId) {
      out.push({ driverKey: d.driverKey, accountId: d.accountId, segmentCostCenterId: d.cls ? null : d.fixedCostCentreId || null, functionalClass: d.cls || null });
    }
    for (const r of d.classRules) {
      if (r.accountId) out.push({ driverKey: d.driverKey, accountId: r.accountId, segmentCostCenterId: null, functionalClass: r.cls });
    }
  }
  return out;
}

export function mappingsChanged(base: readonly DriverDraft[], current: readonly DriverDraft[]): boolean {
  return JSON.stringify(toMappingInputs(base)) !== JSON.stringify(toMappingInputs(current));
}

export interface ExclusiveState {
  classDisabled: boolean;
  fixedDisabled: boolean;
  /** Why something is off: no account yet, the group view, or the other of the pair is in use. */
  why: 'account' | 'group' | 'class' | 'fixed' | null;
}

/** A posting rule uses either a functional class or a fixed cost centre, never both (the server answers 422 for both). */
export function exclusiveState(d: DriverDraft, inCompanyView: boolean): ExclusiveState {
  if (!d.accountId) return { classDisabled: true, fixedDisabled: true, why: 'account' };
  if (!inCompanyView) return { classDisabled: false, fixedDisabled: true, why: 'group' };
  if (d.cls) return { classDisabled: false, fixedDisabled: true, why: 'class' };
  if (d.fixedCostCentreId) return { classDisabled: true, fixedDisabled: false, why: 'fixed' };
  return { classDisabled: false, fixedDisabled: false, why: null };
}

export const setBaseAccount = (d: DriverDraft, accountId: string): DriverDraft =>
  ({ ...d, accountId, ...(accountId ? {} : { cls: '', fixedCostCentreId: '' }) });
export const setBaseClass = (d: DriverDraft, cls: string): DriverDraft =>
  ({ ...d, cls, fixedCostCentreId: cls ? '' : d.fixedCostCentreId });
export const setFixedCostCentre = (d: DriverDraft, id: string): DriverDraft =>
  ({ ...d, fixedCostCentreId: id, cls: id ? '' : d.cls });

/** Classes this driver has no rule for yet (the reserved one included, so it can be shown disabled). */
export function unusedClasses(d: DriverDraft): FunctionalClass[] {
  return FUNCTIONAL_CLASSES.filter((c) => c !== d.cls && !d.classRules.some((r) => r.cls === c));
}

export const addClassRule = (d: DriverDraft, cls: string): DriverDraft =>
  d.classRules.some((r) => r.cls === cls) ? d : { ...d, classRules: [...d.classRules, { cls, accountId: '', inherited: false, inheritedAccountId: null }] };
export const removeClassRule = (d: DriverDraft, cls: string): DriverDraft =>
  ({ ...d, classRules: d.classRules.filter((r) => r.cls !== cls) });
export const setClassRuleAccount = (d: DriverDraft, cls: string, accountId: string): DriverDraft =>
  ({ ...d, classRules: d.classRules.map((r) => (r.cls === cls ? { ...r, accountId } : r)) });

/** The class the base row and a class rule both claim, if any: the server would refuse the pair. */
export function duplicateClass(d: DriverDraft): string | null {
  return d.cls && d.classRules.some((r) => r.cls === d.cls) ? d.cls : null;
}

// ═══════════════════════════ Cost accounting readiness and the switch ═══════════════════════════

/** Display order: the design's six, then the checks the server adds. Unknown keys go last. */
export const CHECK_ORDER = [
  'employees-unresolved', 'invalid-reference', 'accounts-no-rule', 'no-company', 'rounding-default', 'mapping-invisible',
  'employees-no-company', 'currency-scale', 'small-cell', 'rules-not-enforced',
] as const;

export function orderChecks<T extends { key: string }>(checks: readonly T[]): T[] {
  const rank = (key: string) => {
    const i = (CHECK_ORDER as readonly string[]).indexOf(key);
    return i < 0 ? CHECK_ORDER.length : i;
  };
  return [...checks].sort((a, b) => rank(a.key) - rank(b.key));
}

export function readinessAllClear(checks: readonly ReadinessCheck[]): boolean {
  return checks.length > 0 && checks.every((c) => c.severity === 'ok' && c.count === 0);
}

export type FixTarget = { kind: 'link'; href: string } | { kind: 'tab'; tab: 'coa' } | { kind: 'default' };

/** Where a check is fixed. Null when there is nothing for the user to fix (a currency, a privacy note). */
export function checkFixTarget(key: string): FixTarget | null {
  switch (key) {
    case 'employees-unresolved': return { kind: 'link', href: '/setup?tab=departments' };
    case 'invalid-reference':
    case 'no-company': return { kind: 'link', href: '/setup?tab=costCenters' };
    case 'accounts-no-rule':
    case 'mapping-invisible':
    case 'rules-not-enforced': return { kind: 'tab', tab: 'coa' };
    case 'rounding-default': return { kind: 'default' };
    case 'employees-no-company': return { kind: 'link', href: '/people' };
    default: return null;
  }
}

/** The record behind one example row, when it has a page of its own. */
export function sampleHref(key: string, sample: ReadinessSample): string | null {
  const employeeId = typeof sample.employeeId === 'number' ? sample.employeeId : typeof sample.id === 'number' ? sample.id : null;
  if (key === 'employees-unresolved' || (key === 'invalid-reference' && sample.kind === 'employee')) {
    return employeeId && employeeId > 0 ? employeeWorkspaceHref(employeeId) : null;
  }
  if (key === 'invalid-reference' && sample.kind === 'department') return `/setup?tab=departments&department=${encodeURIComponent(String(sample.id))}`;
  if (key === 'invalid-reference' && sample.kind === 'position') return '/setup?tab=designations';
  return null;
}

export type BlockerLevel = 'none' | 'employee' | 'position' | 'department' | 'parentDepartment' | 'company';

/** The server's reason for a blocked employee: none at any level, or the level whose cost centre is invalid. */
export function blockerReason(sample: Pick<ReadinessSample, 'reason'>): BlockerLevel | null {
  switch (sample.reason) {
    case 'none': return 'none';
    case 'employee_cost_centre_invalid': return 'employee';
    case 'position_cost_centre_invalid': return 'position';
    case 'department_cost_centre_invalid': return 'department';
    case 'parent_department_cost_centre_invalid': return 'parentDepartment';
    case 'company_default_cost_centre_invalid': return 'company';
    default: return null;
  }
}

export interface BlockerFixLink { kind: 'employee' | 'department' | 'parentDepartment' | 'position'; href: string }

const departmentHref = (id: string) => `/setup?tab=departments&department=${encodeURIComponent(id)}`;

/**
 * Where a blocked employee (AM-1) can be fixed: the record at the level that is wrong comes first, then the others the
 * person sits under, then the person. There is no per-position page, so a position opens Designations.
 */
export function blockerFixLinks(sample: ReadinessSample): BlockerFixLink[] {
  const employeeId = typeof sample.employeeId === 'number' ? sample.employeeId : typeof sample.id === 'number' ? sample.id : null;
  const links: BlockerFixLink[] = [];
  const level = blockerReason(sample) === 'none' ? 'none' : sample.level;
  const department = sample.departmentId ? { kind: 'department' as const, href: departmentHref(sample.departmentId) } : null;
  const parent = sample.parentDepartmentId ? { kind: 'parentDepartment' as const, href: departmentHref(sample.parentDepartmentId) } : null;
  const position = sample.positionId ? { kind: 'position' as const, href: '/setup?tab=designations' } : null;
  // An invalid company default is fixed in the default field of this same panel, not on the person's records.
  const ordered = blockerReason(sample) === 'company' ? []
    : level === 'parentDepartment' ? [parent, department]
    : level === 'department' ? [department]
    : level === 'position' ? [position]
    : level === 'employee' ? []
    : [department, position];
  for (const link of ordered) if (link) links.push(link);
  if (employeeId && employeeId > 0) links.push({ kind: 'employee', href: employeeWorkspaceHref(employeeId) });
  return links;
}

export interface TurnOnGate {
  /** Blocking checks that have something counted. */
  hard: ReadinessCheck[];
  /** Cost centres with so few employees that their totals could point at one person. */
  exposure: number;
  /** The server refuses without `acknowledgeBlockers` when there are blockers or exposure. */
  needsAck: boolean;
  canConfirm: boolean;
}

export function turnOnGate(r: { checks: readonly ReadinessCheck[]; smallCellExposure: { count: number } }, ack: boolean, reason: string): TurnOnGate {
  const hard = orderChecks(r.checks.filter((c) => c.severity === 'blocker' && c.count > 0));
  const exposure = r.smallCellExposure.count;
  const needsAck = hard.length > 0 || exposure > 0;
  return { hard, exposure, needsAck, canConfirm: reason.trim().length > 0 && (!needsAck || ack) };
}

/**
 * The direction a mixed label ("5002 — Housing Allowance Expense") reads in: that of its first strong letter. A native
 * select cuts a long label at its end, so on an RTL page a Latin label must be set left to right or its beginning is lost.
 */
export function textDir(text: string): 'ltr' | 'rtl' {
  const m = /[A-Za-zÀ-ɏ֐-ࣿיִ-﷿ﹰ-﻿]/.exec(text);
  return m && /[֐-ࣿיִ-﷿ﹰ-﻿]/.test(m[0]) ? 'rtl' : 'ltr';
}
