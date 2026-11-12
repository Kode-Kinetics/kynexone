import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { MatrixCell, MatrixComponent, MatrixGrade } from '../src/api/entitlements';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  FLOOR_PROBLEM, applyFromGradeUpward, applySameForAll, blankDraft, cellKey, dependantKeys, draftFromCell, draftProblem, groupKeys,
  inputFromDraft, markRestNotOffered, monthStarts, percentFromRate, periodChoiceKeys, amountPeriodKeys, quantityPeriodKeys, plainQuantityPeriodKeys, summarise, tierKeys, type CellDraft,
  guideProgress, restoreDrafts, savedFrom, defaultEffectiveFrom, draftStorageKey, gridToCsv, importGrid, parseCellText, splitTable,
} from '../src/lib/entitlementMatrix';

// Release A R1 — the pure rules behind Benefits by grade, with the Masar Holding demo values (plan §5).

const component = (over: Partial<MatrixComponent>): MatrixComponent => ({
  code: 'PER_DIEM', nameEn: 'Per diem', nameAr: 'بدل انتداب', class: 'Facility', floor: 'None', group: 'Facility',
  allowedValueTypes: ['Amount'], allowedCoverageTiers: [], allowedDependantScopes: ['None'], allowedLimitPeriods: ['PerDay'],
  defaultLimitPeriod: 'PerDay', isFloor: false, canBeSkipped: true, isLoanFacility: false, floorReason: null, ...over,
});
const HOUSING = component({ code: 'HOUSING', class: 'QiwaWage', floor: 'Housing', group: 'Wage', allowedValueTypes: ['Amount', 'PercentOfBasic', 'InKind'],
  allowedLimitPeriods: ['Monthly'], defaultLimitPeriod: 'Monthly', isFloor: true, canBeSkipped: false });
const MEDICAL = component({ code: 'MEDICAL', class: 'Contractual', floor: 'Medical', group: 'Contract', allowedValueTypes: ['CoverageTier'],
  allowedCoverageTiers: ['CchiBasic', 'C', 'B', 'A', 'VIP'], allowedDependantScopes: ['Family'], allowedLimitPeriods: ['PerTerm'],
  defaultLimitPeriod: 'PerTerm', isFloor: true, canBeSkipped: false });
const TICKET = component({ code: 'AIR_TICKET', class: 'Contractual', group: 'Contract', allowedValueTypes: ['Quantity'],
  allowedCoverageTiers: ['Economy', 'Business'], allowedDependantScopes: ['None', 'Spouse', 'Children', 'Family'],
  allowedLimitPeriods: ['Annual', 'PerTerm'], defaultLimitPeriod: 'Annual' });
const EDUCATION = component({ code: 'EDUCATION', class: 'Contractual', group: 'Contract', allowedDependantScopes: ['Children'],
  allowedLimitPeriods: ['Annual'], defaultLimitPeriod: 'Annual' });
const PER_DIEM = component({});

const grades: MatrixGrade[] = [1, 2, 3, 4, 5].map(i => ({ id: `g${i}`, code: `G${i}`, name: `Grade ${i}`, nameAr: null, level: i * 10 }));
const cell = (over: Partial<MatrixCell>): MatrixCell => ({
  gradeId: 'g3', componentCode: 'HOUSING', cellId: 'c', isCompanyOverride: false, inherited: false, eligible: true, valueType: 'PercentOfBasic',
  amount: null, rate: 0.25, maxOutstandingAmount: null, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null,
  limitPeriod: 'Monthly', minServiceMonths: null, afterProbation: false, nationalityScope: 'Any', nationalityBasis: null, sourceRule: null,
  note: null, effectiveFrom: '2026-11-01', effectiveTo: null, nextChangeOn: null, ...over,
});
const sar = (n: number) => `SAR ${n.toLocaleString('en-US')}`;
const text = (d: CellDraft) => summarise(d, sar).map(p => p.raw ?? p.key).join(' | ');

test('a percentage of basic round-trips exactly: 25% is stored as 0.25 and shown as 25', () => {
  const d = draftFromCell(HOUSING, 'g3', cell({}));
  expect(d.percent).toBe('25');
  expect(inputFromDraft(d)).toMatchObject({ valueType: 'PercentOfBasic', rate: 0.25, amount: null, limitPeriod: 'Monthly' });
  expect(inputFromDraft({ ...d, percent: '12.5' }).rate).toBe(0.125);
  expect(percentFromRate(0.1)).toBe('10');
  expect(text(d)).toBe('{percent}% of basic');
});

test('a statutory floor can never be "Not offered", and medical has no conditions', () => {
  expect(draftProblem({ ...blankDraft(HOUSING, 'g1'), state: 'notOffered' }, HOUSING)).toBe(FLOOR_PROBLEM);
  expect(draftProblem({ ...blankDraft(PER_DIEM, 'g1'), state: 'notOffered' }, PER_DIEM)).toBeNull();
  const medical = { ...blankDraft(MEDICAL, 'g1'), state: 'offered' as const };
  expect(medical).toMatchObject({ valueType: 'CoverageTier', coverageTier: 'CchiBasic', dependantScope: 'Family' });
  expect(draftProblem(medical, MEDICAL)).toBeNull();
  expect(draftProblem({ ...medical, afterProbation: true }, MEDICAL)).toBe(FLOOR_PROBLEM);
  expect(draftProblem({ ...medical, nationalityScope: 'Saudi', nationalityBasis: 'x' }, MEDICAL)).toBe(FLOOR_PROBLEM);
});

test('values are checked before they reach the server', () => {
  const perDiem = { ...blankDraft(PER_DIEM, 'g1'), state: 'offered' as const };
  expect(draftProblem({ ...perDiem, amount: '' }, PER_DIEM)).toBe('Enter a positive amount with at most two decimals.');
  expect(draftProblem({ ...perDiem, amount: '150.555' }, PER_DIEM)).toBe('Enter a positive amount with at most two decimals.');
  expect(draftProblem({ ...perDiem, amount: '150' }, PER_DIEM)).toBeNull();
  const housing = { ...blankDraft(HOUSING, 'g1'), state: 'offered' as const, valueType: 'PercentOfBasic' as const };
  expect(draftProblem({ ...housing, percent: '120' }, HOUSING)).toBe('Enter a percentage of basic salary above 0 and at most 100.');
  const ticket = { ...blankDraft(TICKET, 'g1'), state: 'offered' as const, nationalityScope: 'NonSaudi' as const };
  expect(draftProblem(ticket, TICKET)).toBe('A nationality condition needs its legal basis recorded.');
  expect(draftProblem({ ...ticket, nationalityBasis: 'Home-leave ticket per contract' }, TICKET)).toBeNull();
  expect(draftProblem({ ...ticket, nationalityBasis: 'x', maxDependants: '3' }, TICKET))
    .toBe('Dependants covered must be between 0 and 20, and only when family members are covered.');
});

test('the Masar G4 ticket and education read as plain words', () => {
  const ticket = draftFromCell(TICKET, 'g4', cell({ componentCode: 'AIR_TICKET', valueType: 'Quantity', rate: null, quantity: 1, coverageTier: 'Economy',
    dependantScope: 'Family', maxDependants: 3, limitPeriod: 'Annual', nationalityScope: 'NonSaudi', nationalityBasis: 'Home-leave ticket per contract' }));
  expect(text(ticket)).toBe('{count} × {class} a year | {who}, up to {count} | Non-Saudi employees');
  expect(inputFromDraft(ticket)).toMatchObject({ quantity: 1, coverageTier: 'Economy', dependantScope: 'Family', maxDependants: 3,
    nationalityScope: 'NonSaudi', nationalityBasis: 'Home-leave ticket per contract', amount: null });
  const education = draftFromCell(EDUCATION, 'g4', cell({ componentCode: 'EDUCATION', valueType: 'Amount', rate: null, amount: 10000,
    dependantScope: 'Children', maxDependants: 2, limitPeriod: 'Annual' }));
  expect(text(education)).toBe('{amount} a year | {who}, up to {count}');
  expect(summarise(education, sar)[0].values).toEqual({ amount: 'SAR 10,000' });
  expect(text({ ...blankDraft(PER_DIEM, 'g1') })).toBe('Not set');
  expect(text(draftFromCell(PER_DIEM, 'g1', cell({ eligible: false, valueType: 'EligibilityOnly', rate: null })))).toBe('Not offered');
});

test('"From G3 upward" copies by level, "Same for all" to every grade, "Mark the rest" never touches a floor', () => {
  const start = Object.fromEntries(grades.map(g => [cellKey(g.id, 'PER_DIEM'), blankDraft(PER_DIEM, g.id)]));
  start[cellKey('g3', 'PER_DIEM')] = { ...start[cellKey('g3', 'PER_DIEM')], state: 'offered', amount: '250' };
  const upward = applyFromGradeUpward(start, grades, 'PER_DIEM', 'g3');
  expect(grades.map(g => upward[cellKey(g.id, 'PER_DIEM')].amount)).toEqual(['', '', '250', '250', '250']);
  expect(upward[cellKey('g5', 'PER_DIEM')].gradeId).toBe('g5');
  const all = applySameForAll(start, grades, 'PER_DIEM', 'g3');
  expect(grades.every(g => all[cellKey(g.id, 'PER_DIEM')].amount === '250')).toBe(true);

  const rest = markRestNotOffered(start, grades, PER_DIEM);
  expect(grades.map(g => rest[cellKey(g.id, 'PER_DIEM')].state)).toEqual(['notOffered', 'notOffered', 'offered', 'notOffered', 'notOffered']);
  const kept = markRestNotOffered(start, grades, PER_DIEM, new Set([cellKey('g5', 'PER_DIEM')]));
  expect(kept[cellKey('g5', 'PER_DIEM')].state).toBe('unset');
  const housing = Object.fromEntries(grades.map(g => [cellKey(g.id, 'HOUSING'), blankDraft(HOUSING, g.id)]));
  expect(markRestNotOffered(housing, grades, HOUSING)).toBe(housing);
});

test('offering changes start on the first of a month', () => {
  expect(monthStarts('2026-10-06', 3)).toEqual(['2026-11-01', '2026-12-01', '2027-01-01']);
  expect(monthStarts('2026-11-01', 2)).toEqual(['2026-11-01', '2026-12-01']);
  expect(monthStarts('2026-12-31', 1)).toEqual(['2027-01-01']);
});

test('every word the matrix can show has Arabic', () => {
  const keys = [
    ...Object.values(tierKeys), ...Object.values(amountPeriodKeys), ...Object.values(quantityPeriodKeys), ...Object.values(periodChoiceKeys), ...Object.values(dependantKeys), ...Object.values(groupKeys),
    'Not set', 'Not offered', 'Provided in kind', '{percent}% of basic', '{count} × {class}', '{who}, up to {count}', 'After probation',
    'Saudi employees', 'Non-Saudi employees', 'After {months} months of service', 'Group default from the effective date',
    'Enter a positive amount with at most two decimals.', 'Enter a percentage of basic salary above 0 and at most 100.',
    'A nationality condition needs its legal basis recorded.', 'Choose the class of cover.', 'Enter how many (1 to 99).',
    'Choose how this benefit is given.', 'Choose who is covered.', 'Months of service must be between 0 and 600.',
    'Keep the note under 500 characters.', 'Keep the legal basis under 300 characters.',
  ];
  for (const key of keys) {
    expect(LOCALE_DICTS.en[key], key).toBe(key);
    expect(/[؀-ۿ]/.test(LOCALE_DICTS.ar[key] ?? ''), key).toBe(true);
  }
});

// A benefit the customer added: a contract benefit with no class of cover, no dependants and a yearly reset.
const PARKING = component({
  code: 'BEN_PARKING', nameEn: 'Parking', nameAr: 'موقف', class: 'Contractual', group: 'Contract', isCustom: true,
  allowedValueTypes: ['Amount', 'InKind', 'Quantity'], allowedLimitPeriods: ['Annual'], defaultLimitPeriod: 'Annual',
});

test('a customer-added benefit starts as a yearly amount and sends no class of cover or dependants', () => {
  const draft = { ...blankDraft(PARKING, 'g1'), state: 'offered' as const, amount: '1200' };
  expect(draft).toMatchObject({ valueType: 'Amount', coverageTier: '', dependantScope: 'None', limitPeriod: 'Annual' });
  expect(draftProblem(draft, PARKING)).toBeNull();
  expect(inputFromDraft(draft)).toMatchObject({ componentCode: 'BEN_PARKING', valueType: 'Amount', amount: 1200, coverageTier: null, dependantScope: 'None', limitPeriod: 'Annual' });
  expect(text(draft)).toBe('{amount} a year');
});

test('a customer-added count reads as a plain number, not "1 × " with no class', () => {
  const count = { ...blankDraft(PARKING, 'g1'), state: 'offered' as const, valueType: 'Quantity' as const, quantity: '2' };
  expect(draftProblem(count, PARKING)).toBeNull();
  expect(inputFromDraft(count)).toMatchObject({ valueType: 'Quantity', quantity: 2, coverageTier: null });
  expect(summarise(count, sar)).toEqual([{ key: plainQuantityPeriodKeys.Annual, values: { count: '2' } }]);
  expect(draftProblem({ ...count, quantity: '0' }, PARKING)).toBe('Enter how many (1 to 99).');
  // A built-in count keeps its class.
  const ticket = { ...blankDraft(TICKET, 'g1'), state: 'offered' as const, quantity: '1', coverageTier: 'Economy' };
  expect(summarise(ticket, sar)[0]).toEqual({ key: quantityPeriodKeys.Annual, values: { count: '1', class: tierKeys.Economy } });
});

// ── Guided flow ──
const LOAN = component({ code: 'LOAN_CAR', isLoanFacility: true, group: 'Facility' });
const two = grades.slice(0, 2);
const draftsFor = (list: MatrixComponent[], set: Record<string, CellDraft['state']> = {}) =>
  Object.fromEntries(list.flatMap(c => two.map(g => {
    const k = cellKey(g.id, c.code);
    return [k, { ...blankDraft(c, g.id), state: set[k] ?? 'unset' }];
  })));

test('the step strip counts the values still needed, in screen order, and skips loans and skipped benefits', () => {
  const list = [HOUSING, PER_DIEM, LOAN];
  const drafts = draftsFor(list, { [cellKey('g1', 'HOUSING')]: 'offered' });
  const p = guideProgress(list, two, drafts, new Set(), () => false);
  expect(p).toEqual({ total: 4, set: 1, nextKey: cellKey('g2', 'HOUSING') });
  // A benefit this company skips needs no value; one already published to start later counts as set.
  expect(guideProgress(list, two, drafts, new Set([cellKey('g2', 'HOUSING')]), c => c === 'PER_DIEM')).toEqual({ total: 2, set: 2, nextKey: null });
});

test('"Save & next" moves past the current cell and wraps round to the first one still empty', () => {
  const list = [HOUSING, PER_DIEM];
  const drafts = draftsFor(list, { [cellKey('g2', 'PER_DIEM')]: 'offered' });
  expect(guideProgress(list, two, drafts, new Set(), () => false, cellKey('g1', 'PER_DIEM')).nextKey).toBe(cellKey('g1', 'HOUSING'));
  expect(guideProgress(list, two, drafts, new Set(), () => false, cellKey('g1', 'HOUSING')).nextKey).toBe(cellKey('g2', 'HOUSING'));
  // The cell being edited is never its own "next", even while it is still empty.
  const onlyOne = draftsFor([HOUSING], { [cellKey('g2', 'HOUSING')]: 'offered' });
  expect(guideProgress([HOUSING], two, onlyOne, new Set(), () => false, cellKey('g1', 'HOUSING')).nextKey).toBeNull();
});

test('unpublished edits are kept and come back only while the value they started from is unchanged', () => {
  const fresh = draftsFor([PER_DIEM]);
  const k1 = cellKey('g1', 'PER_DIEM'); const k2 = cellKey('g2', 'PER_DIEM');
  const edited = { ...fresh, [k1]: { ...fresh[k1], state: 'offered' as const, amount: '150' } };
  const saved = savedFrom(edited, fresh);
  expect(Object.keys(saved)).toEqual([k1]);
  // A reload with the same server values brings the edit back.
  expect(restoreDrafts(fresh, saved)).toMatchObject({ restored: 1, drafts: { [k1]: { amount: '150' } } });
  // Someone else published a value for that cell meanwhile: the old edit is dropped, never laid over the new value.
  const changedOnServer = { ...fresh, [k1]: { ...fresh[k1], state: 'notOffered' as const } };
  expect(restoreDrafts(changedOnServer, saved)).toEqual({ drafts: changedOnServer, restored: 0 });
  // A damaged entry, or a cell that no longer exists, is ignored.
  expect(restoreDrafts(fresh, { [k2]: { draft: null as never, base: fresh[k2] }, gone: saved[k1] }).restored).toBe(0);
  expect(draftStorageKey('', null)).toBe('kynex.benefitsByGrade.drafts.v1:group:current');
});

// ── Excel / CSV ──
test('the grid exports by code and imports back to the same values', () => {
  const list = [HOUSING, TICKET, PER_DIEM, LOAN];
  const drafts = draftsFor(list);
  drafts[cellKey('g1', 'HOUSING')] = { ...drafts[cellKey('g1', 'HOUSING')], state: 'offered', valueType: 'PercentOfBasic', percent: '25' };
  drafts[cellKey('g2', 'HOUSING')] = { ...drafts[cellKey('g2', 'HOUSING')], state: 'offered', valueType: 'InKind' };
  drafts[cellKey('g1', 'AIR_TICKET')] = { ...drafts[cellKey('g1', 'AIR_TICKET')], state: 'offered', quantity: '2', coverageTier: 'Business' };
  drafts[cellKey('g2', 'PER_DIEM')] = { ...drafts[cellKey('g2', 'PER_DIEM')], state: 'notOffered' };
  const csv = gridToCsv(list, two, drafts);
  expect(csv).toBe('Benefit,G1,G2\r\nHOUSING,25%,In kind\r\nAIR_TICKET,2x Business,\r\nPER_DIEM,,Not offered\r\n');
  const back = importGrid(csv, list, two);
  expect(back.errors).toEqual([]);
  expect(back.patches).toEqual([
    { key: cellKey('g1', 'HOUSING'), patch: { state: 'offered', valueType: 'PercentOfBasic', percent: '25' } },
    { key: cellKey('g2', 'HOUSING'), patch: { state: 'offered', valueType: 'InKind' } },
    { key: cellKey('g1', 'AIR_TICKET'), patch: { state: 'offered', valueType: 'Quantity', quantity: '2', coverageTier: 'Business' } },
    { key: cellKey('g2', 'PER_DIEM'), patch: { state: 'notOffered' } },
  ]);
});

test('pasted Excel cells are read by grade and benefit name too, with Arabic digits and thousands separators', () => {
  const pasted = 'Benefit\tGrade 1\tG2\nPer diem\t١٬٥٠٠\t1,250.50\nبدل السكن\t٢٥٪\t\n';
  const housing = { ...HOUSING, nameEn: 'Housing allowance', nameAr: 'بدل السكن' };
  expect(importGrid(pasted, [housing, PER_DIEM], two)).toEqual({ errors: [], patches: [
    { key: cellKey('g1', 'PER_DIEM'), patch: { state: 'offered', valueType: 'Amount', amount: '1500' } },
    { key: cellKey('g2', 'PER_DIEM'), patch: { state: 'offered', valueType: 'Amount', amount: '1250.5' } },
    { key: cellKey('g1', 'HOUSING'), patch: { state: 'offered', valueType: 'PercentOfBasic', percent: '25' } },
  ] });
});

test('a value the benefit does not allow is reported with its row and column, never guessed', () => {
  const r = importGrid('Benefit,G1,G9\nHOUSING,Not offered,10\nPER_DIEM,25%,\nUNKNOWN,1,\nMEDICAL,Z,\n', [HOUSING, PER_DIEM, MEDICAL], two);
  expect(r.patches).toEqual([]);
  expect(r.errors.map(e => [e.row, e.column, e.message])).toEqual([
    [2, 2, 'This benefit is required by law and cannot be Not offered.'],
    [2, 3, 'This grade is not in the grid.'],
    [3, 2, 'This benefit cannot be a percentage of basic salary.'],
    [4, 1, 'This benefit is not in the grid.'],
    [5, 2, 'This value could not be read.'],
  ]);
  expect(parseCellText('150%', HOUSING).error).toBe('Enter a percentage of basic salary above 0 and at most 100.');
  expect(parseCellText('B', MEDICAL).patch).toEqual({ state: 'offered', valueType: 'CoverageTier', coverageTier: 'B' });
  expect(parseCellText('3x First', TICKET).error).toBe('That class does not apply to this benefit.');
  expect(splitTable('"A, B",C\n"x ""y""",\n')).toEqual([['A, B', 'C'], ['x "y"', '']]);
});

// ── Demo R1 F1: Publish must never send a blank date ──────────────────────────────────────────
test('F1: with no date chosen yet, a publish starts today', () => {
  expect(defaultEffectiveFrom('', { asOf: '2026-11-12', today: '2026-11-12' })).toBe('2026-11-12');
});

test('F1: viewing the grid as of a later date publishes from that date', () => {
  expect(defaultEffectiveFrom('', { asOf: '2027-01-01', today: '2026-11-12' })).toBe('2027-01-01');
  expect(defaultEffectiveFrom('2026-11-12', { asOf: '2027-01-01', today: '2026-11-12' })).toBe('2027-01-01');
});

test('F1: a date already chosen is kept while it is not before the floor, and a stale one is lifted', () => {
  expect(defaultEffectiveFrom('2026-12-01', { asOf: '2026-11-12', today: '2026-11-12' })).toBe('2026-12-01');
  expect(defaultEffectiveFrom('2026-10-01', { asOf: '2026-11-12', today: '2026-11-12' })).toBe('2026-11-12');
});

test('F1: the matrix screen sets the default before it returns (the old early return skipped it)', () => {
  const src = fs.readFileSync(path.resolve(process.cwd(), 'src', 'components', 'entitlements', 'EntitlementMatrix.tsx'), 'utf8');
  const body = src.slice(src.indexOf('const apply = useCallback'), src.indexOf('const load = useCallback'));
  expect(body.indexOf('setEffectiveFrom(')).toBeGreaterThan(-1);
  expect(body.indexOf('setEffectiveFrom(')).toBeLessThan(body.indexOf('return kept.restored'));
});
