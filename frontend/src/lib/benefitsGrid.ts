import type { MatrixComponent, MatrixGrade } from '../api/entitlements';
import { cellKey, type CellDraft } from './entitlementMatrix';

/**
 * The pure rules behind the look of Benefits by grade: what a cell shows, which chip a row carries, which grades are in
 * view, what the status band says, and the one total that is honest to show. No React and no network, so each is pinned in
 * unit/benefitsGrid.spec.ts.
 */

export type CellKind = 'unset' | 'notOffered' | 'set' | 'edited' | 'starts' | 'error' | 'locked' | 'readonly';

export interface CellViewInput {
  state: CellDraft['state'];
  /** Changed in this browser and not published yet. */
  dirty: boolean;
  /** The edit cannot be published as it stands (or the server refused it). */
  invalid: boolean;
  /** A published value that starts later: the grade has none until then. */
  startsOn?: string;
  /** A cash allowance waiting for Finance: it takes no value yet. */
  pending: boolean;
  /** Not set in this grid at all: a loan facility (set in Loans) or a benefit this company skips. */
  fixedElsewhere: boolean;
  /** The viewer may change values (entitlements.manage). */
  canEdit: boolean;
}

export interface CellView {
  kind: CellKind;
  /** Show the pencil: the cell can be edited by this viewer. Never the only cue; it sits beside the value text. */
  pencil: boolean;
  /** An empty cell the viewer can fill: it reads "+ Set value" instead of a bare "Not set". */
  setCall: boolean;
  /** The cell is a control (it opens the editor, read-only for a viewer who cannot change values). */
  interactive: boolean;
}

export function cellView(i: CellViewInput): CellView {
  if (i.pending) return { kind: 'locked', pencil: false, setCall: false, interactive: false };
  if (i.fixedElsewhere) return { kind: 'readonly', pencil: false, setCall: false, interactive: false };
  if (i.invalid) return { kind: 'error', pencil: i.canEdit, setCall: false, interactive: true };
  if (i.dirty) return { kind: 'edited', pencil: i.canEdit, setCall: false, interactive: true };
  if (i.state === 'unset') {
    return i.startsOn
      ? { kind: 'starts', pencil: i.canEdit, setCall: false, interactive: true }
      : { kind: 'unset', pencil: false, setCall: i.canEdit, interactive: true };
  }
  if (i.state === 'notOffered') return { kind: 'notOffered', pencil: i.canEdit, setCall: false, interactive: true };
  return { kind: 'set', pencil: i.canEdit, setCall: false, interactive: true };
}

export type OfferingMode = 'Group' | 'Adopted' | 'Tailored' | 'Skipped';

export interface RowChip {
  /** The short text shown, and its i18n key. */
  key: 'Waiting for Finance' | 'Not offered' | 'Tailored' | 'Adopted' | 'New' | 'Added by you';
  /** The full wording, for the chip's title and name (an i18n key). */
  title: string;
  tone: 'warning' | 'neutral' | 'info' | 'success';
}

/** At most one chip in the benefit column: Finance first, then the company's mode, then New, then Added by you. */
export function rowChip(row: { pending: boolean; mode?: OfferingMode; companyView: boolean; isNew: boolean; isCustom: boolean }): RowChip | null {
  if (row.pending) return { key: 'Waiting for Finance', title: 'Waiting for Finance', tone: 'warning' };
  if (row.companyView && row.mode === 'Skipped') return { key: 'Not offered', title: 'Not offered by this company', tone: 'neutral' };
  if (row.companyView && row.mode === 'Tailored') return { key: 'Tailored', title: 'Tailored for this company', tone: 'info' };
  if (row.companyView && row.mode === 'Adopted') return { key: 'Adopted', title: 'Adopted from the group', tone: 'success' };
  if (row.isNew) return { key: 'New', title: 'Added just now', tone: 'info' };
  if (row.isCustom) return { key: 'Added by you', title: 'Added by you', tone: 'neutral' };
  return null;
}

/**
 * Which grade columns are in view: those at least `threshold` of their width inside the scroller. Coordinates run along the
 * reading direction (the caller mirrors them in Arabic), so the same arithmetic serves both. 1-based, for "Grades 1-5 of 8".
 */
export function gradeWindow(view: { start: number; end: number }, cols: { start: number; end: number }[], threshold = 0.6): { from: number; to: number; total: number } {
  const total = cols.length;
  if (total === 0) return { from: 0, to: 0, total: 0 };
  const inView = cols.flatMap((c, i) => {
    const width = c.end - c.start;
    const overlap = Math.min(c.end, view.end) - Math.max(c.start, view.start);
    return width > 0 && overlap / width >= threshold ? [i + 1] : [];
  });
  return inView.length === 0 ? { from: 1, to: 1, total } : { from: inView[0], to: inView[inView.length - 1], total };
}

export type StatusItem =
  | { kind: 'values'; set: number; total: number; complete: boolean }
  | { kind: 'finance'; count: number }
  | { kind: 'changes'; count: number }
  | { kind: 'next'; date: string };

/** What the status band beside the title says. Every number is already on the page; nothing here is fetched. */
export function statusSummary(s: { set: number; total: number; pendingFinance: number; changes: number; nextStart: string | null }): StatusItem[] {
  return [
    { kind: 'values', set: s.set, total: s.total, complete: s.total > 0 && s.set === s.total } as StatusItem,
    ...(s.pendingFinance > 0 ? [{ kind: 'finance', count: s.pendingFinance } as StatusItem] : []),
    ...(s.changes > 0 ? [{ kind: 'changes', count: s.changes } as StatusItem] : []),
    ...(s.nextStart ? [{ kind: 'next', date: s.nextStart } as StatusItem] : []),
  ];
}

/**
 * The benefits with at least one empty grade the admin can fill now: not one waiting for Finance, and not a grade whose
 * value simply starts later. This is what the "Needs a value" filter keeps.
 */
export function needsValueCodes(components: MatrixComponent[], grades: MatrixGrade[], drafts: Record<string, CellDraft>,
  scheduledKeys: ReadonlySet<string>, pendingCodes: ReadonlySet<string>): Set<string> {
  const codes = new Set<string>();
  for (const c of components) {
    if (c.isLoanFacility || pendingCodes.has(c.code)) continue;
    if (grades.some(g => { const key = cellKey(g.id, c.code); return drafts[key]?.state === 'unset' && !scheduledKeys.has(key); })) codes.add(c.code);
  }
  return codes;
}

const FIXED_MONTHLY = (c: MatrixComponent, d: CellDraft | undefined) =>
  !!d && c.group === 'Wage' && !c.isLoanFacility && d.state === 'offered' && d.valueType === 'Amount' && d.limitPeriod === 'Monthly';

/**
 * Per grade, the fixed monthly amounts of the wage benefits added together. A percentage of basic is not an amount until a
 * salary is known, and a contract benefit is not monthly pay, so neither is added; `percentRows` says how many wage rows
 * were left out for being a percentage. null (the row is omitted) when fewer than two rows contribute: a total of one
 * line, or of an amount that cannot be read yet, would say nothing.
 */
export function monthlyFixedTotals(components: MatrixComponent[], grades: MatrixGrade[], drafts: Record<string, CellDraft>): { byGrade: Record<string, number>; percentRows: number } | null {
  const byGrade: Record<string, number> = {};
  const contributing = new Set<string>();
  let percentRows = 0;
  for (const c of components) {
    if (c.group !== 'Wage' || c.isLoanFacility) continue;
    let percent = false;
    for (const g of grades) {
      const d = drafts[cellKey(g.id, c.code)];
      if (d?.state === 'offered' && d.valueType === 'PercentOfBasic') percent = true;
      if (!FIXED_MONTHLY(c, d)) continue;
      const amount = Number(d!.amount);
      if (!/^\d+(\.\d{1,2})?$/.test(d!.amount.trim()) || !(amount > 0)) continue;
      byGrade[g.id] = Math.round(((byGrade[g.id] ?? 0) + amount) * 100) / 100;
      contributing.add(c.code);
    }
    if (percent) percentRows += 1;
  }
  return contributing.size >= 2 ? { byGrade, percentRows } : null;
}
