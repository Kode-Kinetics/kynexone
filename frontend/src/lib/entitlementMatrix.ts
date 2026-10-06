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

export const periodSuffixKeys: Record<string, string> = {
  Monthly: 'a month', Annual: 'a year', PerDay: 'a day', PerTerm: 'per contract year', Lifetime: 'once',
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
  const per = d.limitPeriod && d.limitPeriod !== 'Monthly' && d.limitPeriod !== 'PerTerm' ? periodSuffixKeys[d.limitPeriod] : null;
  switch (d.valueType) {
    case 'Amount':
      parts.push({ raw: money(Number(d.amount)) });
      if (per) parts.push({ key: per, attach: true });
      break;
    case 'PercentOfBasic': parts.push({ key: '{percent}% of basic', values: { percent: d.percent } }); break;
    case 'InKind': parts.push({ key: 'Provided in kind' }); break;
    case 'CoverageTier': parts.push({ key: tierKeys[d.coverageTier] ?? d.coverageTier }); break;
    case 'Quantity':
      parts.push({ key: '{count} × {class}', values: { count: d.quantity, class: tierKeys[d.coverageTier] ?? '' } });
      if (per) parts.push({ key: per, attach: true });
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
