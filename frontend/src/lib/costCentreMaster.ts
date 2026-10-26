import type { CostCenterRequest } from '../api/organization';
import { NO_COST_CENTRE } from './employeeCostCentre';
import { apiErrorCode } from './glCostAccounting';

/**
 * Cost accounting v1 (WP-F), screen 1: the pure rules of the cost-centre master. The tree, the parent choices, the
 * filters, the code format, the M-8 usage counts and the request body that follows the API's contract
 * (null/absent = unchanged, empty = clear). Unit-tested on its own (unit/costCentreMaster.spec.ts).
 */

export interface MasterItem {
  id: string;
  code: string;
  name: string;
  nameAr?: string | null;
  companyId?: string | null;
  isActive: boolean;
  parentId?: string | null;
  functionalClass?: string | null;
  glSegment?: string | null;
}

export interface TreeRow<T extends MasterItem = MasterItem> { item: T; depth: number; hasChildren: boolean }

const byCode = (a: MasterItem, b: MasterItem) => a.code.localeCompare(b.code, 'en', { sensitivity: 'base', numeric: true });

/** Depth-first rows: roots in code order, each followed by its children when `expanded` has the parent. Loop-safe. */
export function buildTree<T extends MasterItem>(items: readonly T[], expanded: ReadonlySet<string>): TreeRow<T>[] {
  const ids = new Set(items.map((i) => i.id));
  const children = new Map<string, T[]>();
  const roots: T[] = [];
  for (const item of items) {
    if (item.parentId && ids.has(item.parentId) && item.parentId !== item.id) {
      const list = children.get(item.parentId) ?? [];
      list.push(item);
      children.set(item.parentId, list);
    } else roots.push(item);
  }
  const rows: TreeRow<T>[] = [];
  const seen = new Set<string>();
  const walk = (item: T, depth: number) => {
    if (seen.has(item.id)) return;
    seen.add(item.id);
    const kids = (children.get(item.id) ?? []).slice().sort(byCode);
    rows.push({ item, depth, hasChildren: kids.length > 0 });
    if (expanded.has(item.id)) for (const kid of kids) walk(kid, depth + 1);
  };
  for (const root of roots.slice().sort(byCode)) walk(root, 0);
  return rows;
}

export function descendantIds(items: readonly MasterItem[], id: string): Set<string> {
  const out = new Set<string>();
  const queue = [id];
  while (queue.length) {
    const current = queue.pop()!;
    for (const item of items) {
      if (item.parentId === current && !out.has(item.id) && item.id !== id) { out.add(item.id); queue.push(item.id); }
    }
  }
  return out;
}

/** The parents a cost centre may sit under: its own company's, active (or the one it already has), never itself or below itself. */
export function parentOptions<T extends MasterItem>(items: readonly T[], editing: { id?: string; companyId?: string | null; parentId?: string | null }): T[] {
  if (!editing.companyId) return [];
  const blocked = editing.id ? descendantIds(items, editing.id) : new Set<string>();
  return items
    .filter((i) => i.companyId === editing.companyId && i.id !== editing.id && !blocked.has(i.id) && (i.isActive || i.id === editing.parentId))
    .sort(byCode);
}

// ── Filters ──────────────────────────────────────────────────────────────────

/** The company filter value for cost centres that have none. */
export const NO_COMPANY_FILTER = '__none__';

export interface MasterFilters { company: string; cls: string; status: string; search: string }

export const isFiltering = (f: MasterFilters) => !!(f.company || f.cls || f.status || f.search.trim());

export function filterItems<T extends MasterItem>(items: readonly T[], f: MasterFilters): T[] {
  const q = f.search.trim().toLowerCase();
  return items.filter((i) =>
    (!f.company || (f.company === NO_COMPANY_FILTER ? !i.companyId : i.companyId === f.company))
    && (!f.cls || i.functionalClass === f.cls)
    && (!f.status || (f.status === 'active' ? i.isActive : !i.isActive))
    && (!q || [i.code, i.name, i.nameAr ?? '', i.glSegment ?? ''].some((v) => v.toLowerCase().includes(q))));
}

// ── Code ─────────────────────────────────────────────────────────────────────

export const CODE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$/;

export function codeProblem(code: string): 'required' | 'format' | null {
  const c = code.trim();
  if (!c) return 'required';
  return CODE_PATTERN.test(c) ? null : 'format';
}

// ── M-8: what uses a cost centre ─────────────────────────────────────────────

export type InUseKey = 'departments' | 'employees' | 'positions' | 'rules' | 'children' | 'default';

/** The server's count names, in the order the dialog lists them. */
const IN_USE_ORDER: Array<[serverKey: string, key: InUseKey]> = [
  ['department', 'departments'], ['employee', 'employees'], ['position', 'positions'], ['mapping', 'rules'], ['activeChild', 'children'], ['companyDefault', 'default'],
];

export function inUseFromError(err: unknown): { counts: Record<string, number> } | null {
  const ax = err as { response?: { status?: number; data?: { counts?: Record<string, number> } } } | null | undefined;
  if (ax?.response?.status !== 409 || apiErrorCode(err) !== 'cost_centre_in_use') return null;
  return { counts: ax.response.data?.counts ?? {} };
}

export function inUseCounts(counts: Record<string, number>): Array<{ key: InUseKey; count: number }> {
  return IN_USE_ORDER.map(([serverKey, key]) => ({ key, count: Number(counts[serverKey] ?? 0) })).filter((c) => c.count > 0);
}

// ── The write ────────────────────────────────────────────────────────────────

export interface MasterForm {
  companyId: string;
  code: string;
  name: string;
  nameAr: string;
  isActive: boolean;
  parentId: string;
  functionalClass: string;
  glSegment: string;
  reason: string;
}

/**
 * The request. On an update, a parent, class or ERP code that did not change is left out (null/absent means unchanged on
 * the server); clearing one sends the empty Guid / an empty string. A new cost centre sends only the values it has.
 */
export function saveBody(editing: MasterItem | null, form: MasterForm): CostCenterRequest {
  const body: CostCenterRequest = {
    companyId: form.companyId || undefined,
    code: form.code.trim(),
    name: form.name.trim(),
    nameAr: form.nameAr,
    isActive: form.isActive,
  };
  const reason = form.reason.trim();
  if (!editing) {
    if (form.parentId) body.parentId = form.parentId;
    if (form.functionalClass) body.functionalClass = form.functionalClass;
    if (form.glSegment.trim()) body.glSegment = form.glSegment.trim();
    if (reason) body.reason = reason;
    return body;
  }
  if (form.parentId !== (editing.parentId ?? '')) body.parentId = form.parentId || NO_COST_CENTRE;
  if (form.functionalClass !== (editing.functionalClass ?? '')) body.functionalClass = form.functionalClass;
  if (form.glSegment.trim() !== (editing.glSegment ?? '')) body.glSegment = form.glSegment.trim();
  if (reason) body.reason = reason;
  return body;
}

// ── Department import: links the file would clear ───────────────────────────

/**
 * How many existing links a department file would clear. The preview's X-Links-To-Clear header says so; each such link also
 * carries a "Clears …" warning on its row, so the count survives a proxy that hides the header.
 */
export function linksToClearFrom(header: string | undefined, rows: ReadonlyArray<{ warnings?: readonly string[] }>): number {
  const fromHeader = Number.parseInt(header ?? '', 10);
  const fromRows = rows.reduce((n, r) => n + (r.warnings ?? []).filter((w) => /^Clears the /.test(w)).length, 0);
  return Math.max(Number.isFinite(fromHeader) ? fromHeader : 0, fromRows);
}
