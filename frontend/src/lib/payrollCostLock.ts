import type { ReadinessSample } from '../api/financeGl';
import { blockerFixLinks, pickServerMessage, apiErrorCode, type BlockerFixLink } from './glCostAccounting';

/**
 * Cost accounting v1 (WP-F phase 3): the pure rules behind the Payroll lock preview and the "journal by cost centre"
 * report. Free of React so the grouping, the refusal handling and the CSV are unit-tested on their own
 * (unit/payrollCostLock.spec.ts).
 */

// ═══════════════════════════ The preview journal ═══════════════════════════

export interface JournalLine {
  componentCode: string;
  componentName: string;
  glAccount: string;
  glAccountName: string;
  entryType: 'DR' | 'CR';
  amount: number;
  /** Null for a balance-sheet line, an uncosted Optional line, or a line the preview cannot cost. */
  costCentreCode?: string | null;
  costCentreName?: string | null;
}

export interface LockBlocker {
  employeeId: number;
  employeeCode: string;
  /** employee | position | department | parentDepartment | company | costCentre */
  level: string;
  departmentId?: string | null;
  positionId?: string | null;
  parentDepartmentId?: string | null;
  /** cost_centre_required | cost_centre_invalid | cost_centre_functional_class_reserved */
  reason: string;
  accountCode?: string | null;
}

export interface LockGroup {
  error: string;
  accountCode?: string;
  accountName?: string;
  /** Invalid only: the level whose cost centre cannot be used, as a label. */
  level?: string;
  employeeCount: number;
  /** At most 10. */
  employees: { id: number; code: string }[];
  amount: number;
  message: string;
  messageAr: string;
}

export interface JournalCosting {
  enabled: boolean;
  blockers: LockBlocker[];
  groups: LockGroup[];
  roundingBlocked: boolean;
  fallbackEmployees?: number;
  fallbackAmount?: number;
  uncostedEmployees?: number;
  uncostedAmount?: number;
}

export type GroupMode = 'cc' | 'account';
export type JournalGroupKind = 'missing' | 'cc' | 'none' | 'account' | 'legacy';

export interface JournalGroup {
  /** missing | none | legacy, else the cost-centre code (cc mode) or the account code (account mode). */
  key: string;
  kind: JournalGroupKind;
  code: string;
  name: string;
  debit: number;
  credit: number;
  net: number;
  lines: JournalLine[];
}

const cents = (n: number) => Math.round(n * 100) / 100;
const accountCode = (glAccount: string) => glAccount.split(/\s+/, 1)[0] ?? glAccount;
const hasCc = (l: JournalLine) => !!l.costCentreCode;

function finish(g: Omit<JournalGroup, 'debit' | 'credit' | 'net'>): JournalGroup {
  const debit = cents(g.lines.filter((l) => l.entryType === 'DR').reduce((s, l) => s + l.amount, 0));
  const credit = cents(g.lines.filter((l) => l.entryType === 'CR').reduce((s, l) => s + l.amount, 0));
  return { ...g, debit, credit, net: cents(debit - credit) };
}

/**
 * The preview journal as the user reads it. Cost-centre mode: "Missing cost centre" first (only when the server reports
 * blockers, and only the uncosted lines on the accounts they name), then the cost centres by code, then "Not costed" last.
 * A posted run none of whose lines carries a cost centre is one "legacy" bucket. Account mode: one group per account code.
 */
export function groupJournal(entries: readonly JournalLine[], mode: GroupMode, blockedAccounts: ReadonlySet<string>, posted = false): JournalGroup[] {
  if (mode === 'account') {
    const byAccount = new Map<string, JournalLine[]>();
    for (const l of entries) {
      const key = accountCode(l.glAccount);
      byAccount.set(key, [...(byAccount.get(key) ?? []), l]);
    }
    return [...byAccount.entries()]
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
      .map(([key, lines]) => finish({ key, kind: 'account', code: key, name: lines[0].glAccountName, lines }));
  }
  if (posted && entries.length > 0 && !entries.some(hasCc)) {
    return [finish({ key: 'legacy', kind: 'legacy', code: '', name: '', lines: [...entries] })];
  }
  const missing: JournalLine[] = [];
  const none: JournalLine[] = [];
  const byCc = new Map<string, { name: string; lines: JournalLine[] }>();
  for (const l of entries) {
    if (hasCc(l)) {
      const code = l.costCentreCode as string;
      const g = byCc.get(code) ?? { name: l.costCentreName ?? '', lines: [] };
      g.lines.push(l);
      byCc.set(code, g);
    } else if (blockedAccounts.has(accountCode(l.glAccount))) missing.push(l);
    else none.push(l);
  }
  const groups: JournalGroup[] = [];
  if (missing.length) groups.push(finish({ key: 'missing', kind: 'missing', code: '', name: '', lines: missing }));
  [...byCc.entries()]
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .forEach(([code, g]) => groups.push(finish({ key: code, kind: 'cc', code, name: g.name, lines: g.lines })));
  if (none.length) groups.push(finish({ key: 'none', kind: 'none', code: '', name: '', lines: none }));
  return groups;
}

// ═══════════════════════════ Blocker cards ═══════════════════════════

/** 'none' (nothing at any level) or the level whose cost centre cannot be used; 'reserved' for a reserved functional class. */
export type RowReason = 'none' | 'employee' | 'position' | 'department' | 'parentDepartment' | 'company' | 'reserved';

export interface BlockerRow {
  employeeId: number;
  code: string;
  reason: RowReason;
  links: BlockerFixLink[];
}

export interface BlockerCard {
  error: string;
  key: string;
  message: string;
  accountCode?: string;
  employeeCount: number;
  hidden: number;
  rows: BlockerRow[];
  /** Whether "Set company default" helps this card. */
  needsDefault: boolean;
}

const READINESS_REASON: Record<string, string> = {
  employee: 'employee_cost_centre_invalid', position: 'position_cost_centre_invalid', department: 'department_cost_centre_invalid',
  parentDepartment: 'parent_department_cost_centre_invalid', company: 'company_default_cost_centre_invalid',
};

function rowReason(b: LockBlocker | undefined, error: string): RowReason {
  if (error === 'cost_centre_functional_class_reserved') return 'reserved';
  if (!b || b.reason === 'cost_centre_required') return 'none';
  const level = b.level as RowReason;
  return level in READINESS_REASON ? level : 'company';
}

/** The ReadinessSample shape the shared fix-link rules read. */
function asSample(b: LockBlocker | undefined, id: number, code: string, reason: RowReason): ReadinessSample {
  if (!b || reason === 'reserved') return { id, code, employeeId: id, employeeCode: code, level: 'company', reason: 'none' };
  return {
    id, code, employeeId: id, employeeCode: code, level: reason === 'none' ? 'company' : reason,
    departmentId: b.departmentId ?? null, positionId: b.positionId ?? null, parentDepartmentId: b.parentDepartmentId ?? null,
    reason: reason === 'none' ? 'none' : READINESS_REASON[reason],
  };
}

export function hiddenEmployeeCount(g: Pick<LockGroup, 'employeeCount' | 'employees'>): number {
  return Math.max(0, g.employeeCount - g.employees.length);
}

/** One card per server group (the server already splits by account and by level), one row per employee, with fix links. */
export function blockerCards(groups: readonly LockGroup[], blockers: readonly LockBlocker[], locale: string): BlockerCard[] {
  return groups.map((g, i) => {
    const rows = g.employees.slice(0, 10).map((e): BlockerRow => {
      const match = blockers.find((b) => b.employeeId === e.id && b.reason === g.error && (!g.accountCode || b.accountCode === g.accountCode))
        ?? blockers.find((b) => b.employeeId === e.id && b.reason === g.error);
      const reason = rowReason(match, g.error);
      return { employeeId: e.id, code: e.code, reason, links: blockerFixLinks(asSample(match, e.id, e.code, reason)) };
    });
    return {
      error: g.error,
      key: `${g.error}-${g.accountCode ?? g.level ?? i}`,
      message: (locale === 'ar' ? g.messageAr || g.message : g.message || g.messageAr) ?? '',
      accountCode: g.accountCode,
      employeeCount: g.employeeCount,
      hidden: hiddenEmployeeCount(g),
      rows,
      needsDefault: g.error === 'cost_centre_required' || rows.some((r) => r.reason === 'company'),
    };
  });
}

// ═══════════════════════════ What Lock answered ═══════════════════════════

export type LockOutcome =
  | { kind: 'blocked'; error: string; message: string; blockers: LockBlocker[]; groups: LockGroup[] }
  /** M-6: the run was sent back to Processed. */
  | { kind: 'sent_back'; message: string }
  /** M-7: another run for this company awaits lock. */
  | { kind: 'locked_other'; message: string }
  /** The caller changed cost-accounting settings for this company and period, so someone else must lock. */
  | { kind: 'sod'; message: string }
  | { kind: 'other'; message: string | null };

const BLOCKING_CODES = new Set([
  'cost_centre_required', 'cost_centre_invalid', 'cost_centre_functional_class_reserved', 'cost_centre_rounding_required', 'cost_centre_not_allowed',
]);

/** Sorts a failed Lock into what the screen does next. The server's own sentence is shown in the viewer's language. */
export function classifyLockError(err: unknown, locale: string): LockOutcome {
  const response = (err as { response?: { status?: number; data?: unknown } } | null | undefined)?.response;
  const data = (response?.data ?? {}) as { blockers?: unknown; groups?: unknown };
  const code = apiErrorCode(err);
  const message = pickServerMessage(response?.data, locale);
  if (response?.status === 422 && code && BLOCKING_CODES.has(code)) {
    return {
      kind: 'blocked', error: code, message: message ?? '',
      blockers: Array.isArray(data.blockers) ? (data.blockers as LockBlocker[]) : [],
      groups: Array.isArray(data.groups) ? (data.groups as LockGroup[]) : [],
    };
  }
  if (response?.status === 409 && code === 'cost_centre_changed_after_approval') return { kind: 'sent_back', message: message ?? '' };
  if (response?.status === 409 && code === 'locked_into_approved_run') return { kind: 'locked_other', message: message ?? '' };
  if (response?.status === 403 && code === 'separation_of_duties') return { kind: 'sod', message: message ?? '' };
  return { kind: 'other', message };
}

// ═══════════════════════════ Approve: the uncosted acknowledgement (S-048) ═══════════════════════════

/**
 * The count sent as `expectedUncostedCount`: only one the approver affirmed. Echoing the server's own number back without
 * the tick would satisfy the check while acknowledging nothing.
 */
export function acknowledgeUncosted(costing: Pick<JournalCosting, 'uncostedEmployees'> | null | undefined, affirmed: boolean): number | null {
  const n = costing?.uncostedEmployees ?? 0;
  return affirmed && n > 0 ? n : null;
}

/** The user may read the preview but not lock (payroll.lock missing): the Lock button is replaced by a note. */
export const readOnlyLockNote = (mayLock: boolean): boolean => !mayLock;
