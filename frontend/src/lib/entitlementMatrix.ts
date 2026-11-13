import type {
  DependantScope, MatrixCell, MatrixCellInput, MatrixComponent, MatrixGrade, MatrixValueType, NationalityScope,
} from '../api/entitlements';

/**
 * Benefits by grade (Release A R1): the pure rules behind the matrix screen — drafts, validation that mirrors the
 * server, the plain-language summary of a cell, and the bulk helpers. No React here, so it is unit-tested directly.
 * Every string returned is an English i18n key (src/i18n/releaseA/matrix.ts) for t().
 */

/** unset: no value yet · offered: a value · notOffered: "Not offered" for this grade · groupDefault: drop the company's own value. */
export type CellState = 'unset' | 'offered' | 'notOffered' | 'groupDefault';

export interface CellDraft {
  gradeId: string;
  componentCode: string;
  state: CellState;
  valueType: MatrixValueType | '';
  amount: string;
  /** Percent of basic as typed, 0–100 (sent as a fraction). */
  percent: string;
  coverageTier: string;
  quantity: string;
  dependantScope: DependantScope;
  maxDependants: string;
  limitPeriod: string;
  minServiceMonths: string;
  afterProbation: boolean;
  nationalityScope: NationalityScope;
  nationalityBasis: string;
  note: string;
}

export const cellKey = (gradeId: string, componentCode: string) => `${gradeId}|${componentCode}`;

/** Sentinel problem: the component's statutory floor is broken; show its legal reason (component.floorReason). */
export const FLOOR_PROBLEM = '__floor__';

const num = (v: number | null | undefined) => (v === null || v === undefined ? '' : String(v));

/** Percent shown for a stored fraction, without float noise (0.1 → "10", 0.125 → "12.5"). */
export const percentFromRate = (rate: number | null | undefined) =>
  rate === null || rate === undefined ? '' : String(Math.round(rate * 10000) / 100);

export function defaultDependantScope(component: MatrixComponent): DependantScope {
  return component.allowedDependantScopes.length === 1 ? component.allowedDependantScopes[0] : 'None';
}

/** A fresh draft. Floors are pre-filled with their lawful minimum, so opening one never starts below the floor. */
export function blankDraft(component: MatrixComponent, gradeId: string): CellDraft {
  const valueType: MatrixValueType | '' = component.code === 'MEDICAL' ? 'CoverageTier' : component.allowedValueTypes[0] ?? '';
  return {
    gradeId, componentCode: component.code, state: 'unset', valueType,
    amount: '', percent: '', coverageTier: component.code === 'MEDICAL' ? 'CchiBasic' : component.allowedCoverageTiers[0] ?? '',
    quantity: valueType === 'Quantity' ? '1' : '', dependantScope: defaultDependantScope(component), maxDependants: '',
    limitPeriod: component.defaultLimitPeriod ?? '', minServiceMonths: '', afterProbation: false,
    nationalityScope: 'Any', nationalityBasis: '', note: '',
  };
}

export function draftFromCell(component: MatrixComponent, gradeId: string, cell: MatrixCell | undefined): CellDraft {
  const blank = blankDraft(component, gradeId);
  if (!cell) return blank;
  if (!cell.eligible) return { ...blank, state: 'notOffered', note: cell.note ?? '' };
  return {
    gradeId, componentCode: component.code, state: 'offered', valueType: cell.valueType,
    amount: num(cell.amount), percent: cell.valueType === 'PercentOfBasic' ? percentFromRate(cell.rate) : '',
    coverageTier: cell.coverageTier ?? '', quantity: num(cell.quantity), dependantScope: cell.dependantScope,
    maxDependants: num(cell.maxDependants), limitPeriod: cell.limitPeriod ?? '', minServiceMonths: num(cell.minServiceMonths),
    afterProbation: cell.afterProbation, nationalityScope: cell.nationalityScope, nationalityBasis: cell.nationalityBasis ?? '',
    note: cell.note ?? '',
  };
}

export const sameDraft = (a: CellDraft, b: CellDraft) => JSON.stringify(a) === JSON.stringify(b);

const isMoney = (s: string) => /^\d+(\.\d{1,2})?$/.test(s.trim()) && Number(s) > 0;
const isWhole = (s: string, min: number, max: number) => /^\d+$/.test(s.trim()) && Number(s) >= min && Number(s) <= max;

/**
 * Why a draft can't be published (an i18n key, or {@link FLOOR_PROBLEM}); null when it can. Mirrors
 * EntitlementMatrixService.ValidateCell, so the server refusal is the backstop, not the first line.
 */
export function draftProblem(d: CellDraft, component: MatrixComponent): string | null {
  if (d.state === 'unset' || d.state === 'groupDefault') return null;
  if (d.note.length > 500) return 'Keep the note under 500 characters.';
  if (d.state === 'notOffered') return component.isFloor ? FLOOR_PROBLEM : null;
  if (!d.valueType || !component.allowedValueTypes.includes(d.valueType)) return 'Choose how this benefit is given.';
  switch (d.valueType) {
    case 'Amount': if (!isMoney(d.amount)) return 'Enter a positive amount with at most two decimals.'; break;
    case 'PercentOfBasic':
      if (!/^\d+(\.\d{1,2})?$/.test(d.percent.trim()) || Number(d.percent) <= 0 || Number(d.percent) > 100)
        return 'Enter a percentage of basic salary above 0 and at most 100.';
      break;
    case 'CoverageTier': if (!component.allowedCoverageTiers.includes(d.coverageTier)) return 'Choose the class of cover.'; break;
    case 'Quantity': if (!isWhole(d.quantity, 1, 99)) return 'Enter how many (1 to 99).'; break;
    default: break;
  }
  if (!component.allowedDependantScopes.includes(d.dependantScope)) return 'Choose who is covered.';
  if (d.maxDependants.trim() && (d.dependantScope === 'None' || !isWhole(d.maxDependants, 0, 20)))
    return 'Dependants covered must be between 0 and 20, and only when family members are covered.';
  if (d.minServiceMonths.trim() && !isWhole(d.minServiceMonths, 0, 600)) return 'Months of service must be between 0 and 600.';
  if (d.nationalityScope !== 'Any' && !d.nationalityBasis.trim()) return 'A nationality condition needs its legal basis recorded.';
  if (d.nationalityBasis.length > 300) return 'Keep the legal basis under 300 characters.';
  if (component.code === 'MEDICAL' && (d.afterProbation || d.nationalityScope !== 'Any' || d.dependantScope !== 'Family')) return FLOOR_PROBLEM;
  return null;
}

export function inputFromDraft(d: CellDraft): MatrixCellInput {
  if (d.state === 'groupDefault') return { gradeId: d.gradeId, componentCode: d.componentCode, eligible: false, useGroupDefault: true };
  if (d.state === 'notOffered') return { gradeId: d.gradeId, componentCode: d.componentCode, eligible: false, note: d.note.trim() || null };
  const vt = d.valueType || null;
  const tierAllowed = vt === 'CoverageTier' || vt === 'Quantity';
  return {
    gradeId: d.gradeId, componentCode: d.componentCode, eligible: true, valueType: vt,
    amount: vt === 'Amount' ? Number(d.amount) : null,
    rate: vt === 'PercentOfBasic' ? Math.round(Number(d.percent) * 100) / 10000 : null,
    coverageTier: tierAllowed && d.coverageTier ? d.coverageTier : null,
    quantity: vt === 'Quantity' ? Number(d.quantity) : null,
    dependantScope: d.dependantScope,
    maxDependants: d.dependantScope !== 'None' && d.maxDependants.trim() ? Number(d.maxDependants) : null,
    limitPeriod: d.limitPeriod || null,
    minServiceMonths: d.minServiceMonths.trim() ? Number(d.minServiceMonths) : null,
    afterProbation: d.afterProbation,
    nationalityScope: d.nationalityScope,
    nationalityBasis: d.nationalityScope === 'Any' ? null : d.nationalityBasis.trim(),
    note: d.note.trim() || null,
  };
}

/** One piece of a cell's plain-language summary: an i18n key and the values it fills, or raw text (a formatted
 * amount) that is shown as is. `attach` joins it to the previous piece with a space instead of a separator. */
export interface SummaryPart { key?: string; values?: Record<string, string | number>; raw?: string; attach?: boolean }

export const tierKeys: Record<string, string> = {
  CchiBasic: 'Basic class (CCHI)', C: 'Class C', B: 'Class B', A: 'Class A', VIP: 'VIP class', Economy: 'Economy', Business: 'Business',
};

/** An amount with the period it resets on, as ONE sentence (Arabic cannot reorder a suffix glued on). Monthly and
 * per-contract-year amounts read without a suffix, as before. */
export const amountPeriodKeys: Record<string, string> = {
  Annual: '{amount} a year', PerDay: '{amount} a day', Lifetime: '{amount} once',
};

/** A count of a class (tickets) with the period it resets on, as one sentence. */
export const quantityPeriodKeys: Record<string, string> = {
  Annual: '{count} × {class} a year', PerDay: '{count} × {class} a day', Lifetime: '{count} × {class} once',
};

/** The same count with no class to name (a benefit the customer added has none), as one sentence. */
export const plainQuantityPeriodKeys: Record<string, string> = {
  Annual: '{count} a year', PerDay: '{count} a day', Lifetime: '{count} once',
};

/** The "Resets" picker's choices. */
export const periodChoiceKeys: Record<string, string> = {
  Monthly: 'Every month', Annual: 'Every year', PerDay: 'Every day', PerTerm: 'Every contract year', Lifetime: 'Once only',
};

export const dependantKeys: Record<DependantScope, string> = {
  None: 'Employee only', Spouse: 'Employee and spouse', Children: 'Children', Family: 'Employee and family',
};

/** What a cell gives, in words: "25% of basic", "1 × Economy a year · Employee and family, up to 3 · Non-Saudi". */
export function summarise(d: CellDraft, money: (n: number) => string): SummaryPart[] {
  if (d.state === 'unset') return [{ key: 'Not set' }];
  if (d.state === 'notOffered') return [{ key: 'Not offered' }];
  if (d.state === 'groupDefault') return [{ key: 'Group default from the effective date' }];
  const parts: SummaryPart[] = [];
  const period = d.limitPeriod && d.limitPeriod !== 'Monthly' && d.limitPeriod !== 'PerTerm' ? d.limitPeriod : null;
  switch (d.valueType) {
    case 'Amount':
      parts.push(period && amountPeriodKeys[period]
        ? { key: amountPeriodKeys[period], values: { amount: money(Number(d.amount)) } }
        : { raw: money(Number(d.amount)) });
      break;
    case 'PercentOfBasic': parts.push({ key: '{percent}% of basic', values: { percent: d.percent } }); break;
    case 'InKind': parts.push({ key: 'Provided in kind' }); break;
    case 'CoverageTier': parts.push({ key: tierKeys[d.coverageTier] ?? d.coverageTier }); break;
    case 'Quantity':
      // A built-in count has a class (Economy, Business); one the customer added does not, so it is just the number.
      parts.push(d.coverageTier
        ? { key: (period && quantityPeriodKeys[period]) || '{count} × {class}', values: { count: d.quantity, class: tierKeys[d.coverageTier] ?? d.coverageTier } }
        : { key: (period && plainQuantityPeriodKeys[period]) || '{count}', values: { count: d.quantity } });
      break;
    default: parts.push({ key: 'Eligible' });
  }
  if (d.dependantScope !== 'None') {
    parts.push(d.maxDependants.trim()
      ? { key: '{who}, up to {count}', values: { who: dependantKeys[d.dependantScope], count: d.maxDependants } }
      : { key: dependantKeys[d.dependantScope] });
  }
  if (d.nationalityScope === 'Saudi') parts.push({ key: 'Saudi employees' });
  if (d.nationalityScope === 'NonSaudi') parts.push({ key: 'Non-Saudi employees' });
  if (d.afterProbation) parts.push({ key: 'After probation' });
  if (d.minServiceMonths.trim() && Number(d.minServiceMonths) > 0) parts.push({ key: 'After {months} months of service', values: { months: d.minServiceMonths } });
  return parts;
}

type Drafts = Record<string, CellDraft>;

const copyValues = (from: CellDraft, to: CellDraft): CellDraft => ({ ...from, gradeId: to.gradeId, componentCode: to.componentCode });

/** Copies one grade's value of a benefit to every grade. */
export function applySameForAll(drafts: Drafts, grades: MatrixGrade[], code: string, sourceGradeId: string): Drafts {
  const source = drafts[cellKey(sourceGradeId, code)];
  if (!source) return drafts;
  const next = { ...drafts };
  for (const g of grades) next[cellKey(g.id, code)] = copyValues(source, next[cellKey(g.id, code)] ?? source);
  return next;
}

/** Copies one grade's value of a benefit to it and every grade above it (by level). */
export function applyFromGradeUpward(drafts: Drafts, grades: MatrixGrade[], code: string, sourceGradeId: string): Drafts {
  const source = drafts[cellKey(sourceGradeId, code)];
  const level = grades.find(g => g.id === sourceGradeId)?.level;
  if (!source || level === undefined) return drafts;
  const next = { ...drafts };
  for (const g of grades.filter(x => x.level >= level)) next[cellKey(g.id, code)] = copyValues(source, next[cellKey(g.id, code)] ?? source);
  return next;
}

/** Marks every grade still without a value as "Not offered". Never for a statutory floor, and never over a value
 * that is already published to start later (`scheduled` holds those cell keys). */
export function markRestNotOffered(drafts: Drafts, grades: MatrixGrade[], component: MatrixComponent, scheduled: ReadonlySet<string> = new Set()): Drafts {
  if (component.isFloor || component.isLoanFacility) return drafts;
  const next = { ...drafts };
  for (const g of grades) {
    const key = cellKey(g.id, component.code);
    if (scheduled.has(key)) continue;
    const d = next[key] ?? blankDraft(component, g.id);
    if (d.state === 'unset') next[key] = { ...d, state: 'notOffered' };
  }
  return next;
}

/** The order rows appear in: wage allowances, contract benefits, facilities; loan facilities last. */
export const groupOrder: Array<MatrixComponent['group']> = ['Wage', 'Contract', 'Facility'];

export const groupKeys: Record<MatrixComponent['group'], string> = {
  Wage: 'Wage allowances — paid monthly, in the Qiwa contract',
  Contract: 'Contract benefits — fixed for each contract year',
  Facility: 'Facilities — the current policy applies when used',
};

/** The first days of the next <count> months, as ISO dates (today included when it is the 1st): offering changes start on one. */
export function monthStarts(today: string, count: number): string[] {
  const [y, m, d] = today.split('-').map(Number);
  const out: string[] = [];
  let year = y; let month = d === 1 ? m : m + 1;
  for (let i = 0; i < count; i++) {
    if (month > 12) { month = 1; year++; }
    out.push(`${year}-${String(month).padStart(2, '0')}-01`);
    month++;
  }
  return out;
}

// ── Guided flow ───────────────────────────────────────────────────────────────────────────────────

/** Where the grid stands for the step strip: grade values set out of the ones that need one, and the next one to set. */
export interface GuideProgress { total: number; set: number; nextKey: string | null }

/**
 * Counts the values the grid still needs and finds the next one, in the order the screen shows them (benefit by benefit,
 * grade by grade). Loan facilities (set in Loans) and benefits a company skips need none; a value already published to
 * start later counts as set. With `after`, the next one AFTER that cell, wrapping round, so "Save & next" moves on.
 */
export function guideProgress(components: MatrixComponent[], grades: MatrixGrade[], drafts: Drafts,
  scheduled: ReadonlySet<string>, skipped: (code: string) => boolean, after?: string | null): GuideProgress {
  const keys: string[] = [];
  for (const c of components) {
    if (c.isLoanFacility || skipped(c.code)) continue;
    for (const g of grades) keys.push(cellKey(g.id, c.code));
  }
  const open = (k: string) => drafts[k]?.state === 'unset' && !scheduled.has(k);
  const set = keys.filter(k => !open(k)).length;
  const start = after ? keys.indexOf(after) + 1 : 0;
  const ordered = [...keys.slice(start), ...keys.slice(0, start)];
  return { total: keys.length, set, nextKey: ordered.find(k => k !== after && open(k)) ?? null };
}

/** Unpublished edits kept in the browser, so a reload, a new benefit or leaving the page does not lose them. Each edit is
 * stored with the value it started from (`base`): it is restored only while the server still holds that value. */
export type SavedDrafts = Record<string, { draft: CellDraft; base: CellDraft }>;

export const draftStorageKey = (scope: string, asOf: string | null) => `kynex.benefitsByGrade.drafts.v1:${scope || 'group'}:${asOf || 'current'}`;

export function savedFrom(drafts: Drafts, original: Drafts): SavedDrafts {
  const saved: SavedDrafts = {};
  for (const [k, d] of Object.entries(drafts)) {
    const base = original[k];
    if (base && !sameDraft(d, base)) saved[k] = { draft: d, base };
  }
  return saved;
}

/**
 * The date a publish starts from. Editing the grid as of a later date publishes from that date (an earlier one would sit under it);
 * otherwise today. A date already chosen is kept while it is not before that floor. Never blank: the server refuses a publish without a date.
 */
export function defaultEffectiveFrom(current: string, view: { asOf: string; today: string }): string {
  const floor = view.asOf > view.today ? view.asOf : view.today;
  return current && current >= floor ? current : floor;
}

export function restoreDrafts(fresh: Drafts, saved: SavedDrafts | null | undefined): { drafts: Drafts; restored: number } {
  if (!saved) return { drafts: fresh, restored: 0 };
  const drafts = { ...fresh };
  let restored = 0;
  for (const [k, entry] of Object.entries(saved)) {
    if (!fresh[k] || !entry?.draft || !entry.base || !sameDraft(entry.base, fresh[k])) continue;
    drafts[k] = entry.draft;
    restored++;
  }
  return { drafts, restored };
}

// ── Excel / CSV in and out ────────────────────────────────────────────────────────────────────────

/**
 * The grid as text, one benefit per row and one grade per column, keyed by codes so a file round-trips whatever the
 * language: `Benefit,G1,G2…` then `HOUSING,25%,25%,…`. A value is written the way an admin types it in Excel: an amount
 * (`1500`), a percentage of basic (`25%`), `In kind`, a count (`2x`, or `1x Economy` with a class), a class of cover
 * (`B`), or `Not offered`. Empty means not set. Loan limits are set in Loans and are left out. Conditions and dependants
 * are not in the file: an import changes values only and keeps a cell's conditions as they are.
 */
export function cellText(d: CellDraft): string {
  if (d.state === 'notOffered') return 'Not offered';
  if (d.state !== 'offered') return '';
  switch (d.valueType) {
    case 'Amount': return d.amount;
    case 'PercentOfBasic': return `${d.percent}%`;
    case 'InKind': return 'In kind';
    case 'CoverageTier': return d.coverageTier;
    case 'Quantity': return d.coverageTier ? `${d.quantity}x ${d.coverageTier}` : `${d.quantity}x`;
    default: return '';
  }
}

const csvField = (v: string) => (/[",\n\r]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);

export function gridToCsv(components: MatrixComponent[], grades: MatrixGrade[], drafts: Drafts): string {
  const rows = [['Benefit', ...grades.map(g => g.code)]];
  for (const c of components.filter(x => !x.isLoanFacility))
    rows.push([c.code, ...grades.map(g => { const d = drafts[cellKey(g.id, c.code)]; return d ? cellText(d) : ''; })]);
  return rows.map(r => r.map(csvField).join(',')).join('\r\n') + '\r\n';
}

/** Splits pasted Excel cells (tab-separated) or a CSV file into rows of cells; quoted fields may hold commas and line breaks. */
export function splitTable(text: string): string[][] {
  const clean = text.replace(/^﻿/, '');
  const firstLine = clean.split(/\r?\n/)[0] ?? '';
  const sep = firstLine.includes('\t') ? '\t' : ',';
  const rows: string[][] = []; let row: string[] = []; let field = ''; let quoted = false;
  for (let i = 0; i < clean.length; i++) {
    const ch = clean[i];
    if (quoted) {
      if (ch === '"' && clean[i + 1] === '"') { field += '"'; i++; }
      else if (ch === '"') quoted = false;
      else field += ch;
    } else if (ch === '"' && field === '') quoted = true;
    else if (ch === sep) { row.push(field); field = ''; }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && clean[i + 1] === '\n') i++;
      row.push(field); rows.push(row); row = []; field = '';
    } else field += ch;
  }
  if (field !== '' || row.length > 0) { row.push(field); rows.push(row); }
  return rows.filter(r => r.some(cell => cell.trim() !== ''));
}

/** Arabic-Indic and Persian digits, the Arabic decimal and thousands marks, read as ASCII. */
const asciiDigits = (v: string) => v
  .replace(/[٠-٩]/g, ch => String(ch.charCodeAt(0) - 0x0660))
  .replace(/[۰-۹]/g, ch => String(ch.charCodeAt(0) - 0x06F0))
  .replace(/٫/g, '.').replace(/[٬,\s]/g, '');

const NOT_OFFERED = /^(not offered|no|n\/a|-|غير مقدمة|غير مقدَّمة|لا)$/i;
const IN_KIND = /^(in kind|in-kind|عيني|عيناً|عينًا|عينا)$/i;

export interface GridImportError { row: number; column: number; benefit: string; grade: string; message: string }
export interface GridImport { patches: { key: string; patch: Partial<CellDraft> }[]; errors: GridImportError[] }

/** One typed value for one benefit, as a draft change, or an i18n-key error. Never guesses a value type the benefit forbids. */
export function parseCellText(raw: string, c: MatrixComponent): { patch?: Partial<CellDraft>; error?: string } {
  const text = raw.trim();
  if (!text) return {};
  const allowed = (v: MatrixValueType) => c.allowedValueTypes.includes(v);
  if (NOT_OFFERED.test(text)) return c.isFloor ? { error: 'This benefit is required by law and cannot be Not offered.' } : { patch: { state: 'notOffered' } };
  if (IN_KIND.test(text)) return allowed('InKind') ? { patch: { state: 'offered', valueType: 'InKind' } } : { error: 'This benefit cannot be given in kind.' };
  const tier = c.allowedCoverageTiers.find(x => x.toLowerCase() === text.toLowerCase());
  if (tier && allowed('CoverageTier')) return { patch: { state: 'offered', valueType: 'CoverageTier', coverageTier: tier } };
  const percent = /^(.+?)\s*[%٪]$/.exec(text);
  if (percent) {
    const n = asciiDigits(percent[1]);
    if (!allowed('PercentOfBasic')) return { error: 'This benefit cannot be a percentage of basic salary.' };
    return /^\d+(\.\d+)?$/.test(n) && Number(n) > 0 && Number(n) <= 100
      ? { patch: { state: 'offered', valueType: 'PercentOfBasic', percent: String(Number(n)) } }
      : { error: 'Enter a percentage of basic salary above 0 and at most 100.' };
  }
  const count = /^(?:[x×]\s*(\S+)|(\S+?)\s*[x×])(?:\s+(.+))?$/i.exec(text);
  if (count) {
    if (!allowed('Quantity')) return { error: 'This benefit is not given as a number of items.' };
    const n = asciiDigits(count[1] ?? count[2]);
    if (!/^\d+$/.test(n) || Number(n) < 1 || Number(n) > 99) return { error: 'Enter how many (1 to 99).' };
    const cls = count[3]?.trim();
    const tierFor = cls ? c.allowedCoverageTiers.find(x => x.toLowerCase() === cls.toLowerCase()) : undefined;
    if (cls && !tierFor) return { error: 'That class does not apply to this benefit.' };
    return { patch: { state: 'offered', valueType: 'Quantity', quantity: n, ...(tierFor ? { coverageTier: tierFor } : {}) } };
  }
  const n = asciiDigits(text);
  if (/^\d+(\.\d{1,2})?$/.test(n) && Number(n) > 0) {
    if (allowed('Amount')) return { patch: { state: 'offered', valueType: 'Amount', amount: String(Number(n)) } };
    if (allowed('Quantity') && /^\d+$/.test(n) && Number(n) <= 99) return { patch: { state: 'offered', valueType: 'Quantity', quantity: n } };
    return { error: 'This benefit is not given as an amount.' };
  }
  return { error: 'This value could not be read.' };
}

/**
 * Reads pasted Excel cells or a CSV file into draft changes. The first row names the grades (code or name), the first
 * column the benefits (code, or the English or Arabic name). Nothing is published: the changes land in the grid as
 * unpublished edits, and every value that could not be read is reported with its row and column.
 */
export function importGrid(text: string, components: MatrixComponent[], grades: MatrixGrade[]): GridImport {
  const [header, ...body] = splitTable(text);
  const result: GridImport = { patches: [], errors: [] };
  if (!header) return result;
  const norm = (v: string) => v.trim().toLowerCase();
  const gradeAt = header.map((h, i) => i === 0 ? undefined : grades.find(g => [g.code, g.name, g.nameAr ?? ''].some(x => x && norm(x) === norm(h))));
  body.forEach((cells, r) => {
    const label = cells[0] ?? '';
    const c = components.find(x => !x.isLoanFacility && [x.code, x.nameEn, x.nameAr].some(n => n && norm(n) === norm(label)));
    if (!c) {
      if (label.trim()) result.errors.push({ row: r + 2, column: 1, benefit: label, grade: '', message: 'This benefit is not in the grid.' });
      return;
    }
    cells.slice(1).forEach((cell, i) => {
      const g = gradeAt[i + 1];
      if (!cell.trim()) return;
      if (!g) { result.errors.push({ row: r + 2, column: i + 2, benefit: c.code, grade: header[i + 1] ?? '', message: 'This grade is not in the grid.' }); return; }
      const parsed = parseCellText(cell, c);
      if (parsed.error) result.errors.push({ row: r + 2, column: i + 2, benefit: c.code, grade: g.code, message: parsed.error });
      else if (parsed.patch) result.patches.push({ key: cellKey(g.id, c.code), patch: parsed.patch });
    });
  });
  return result;
}
