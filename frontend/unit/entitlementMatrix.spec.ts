import { expect, test } from '@playwright/test';
import type { MatrixCell, MatrixComponent, MatrixGrade } from '../src/api/entitlements';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  FLOOR_PROBLEM, applyFromGradeUpward, applySameForAll, blankDraft, cellKey, dependantKeys, draftFromCell, draftProblem, groupKeys,
  inputFromDraft, markRestNotOffered, monthStarts, percentFromRate, periodChoiceKeys, periodSuffixKeys, summarise, tierKeys, type CellDraft,
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
  expect(text(ticket)).toBe('{count} × {class} | a year | {who}, up to {count} | Non-Saudi employees');
  expect(inputFromDraft(ticket)).toMatchObject({ quantity: 1, coverageTier: 'Economy', dependantScope: 'Family', maxDependants: 3,
    nationalityScope: 'NonSaudi', nationalityBasis: 'Home-leave ticket per contract', amount: null });
  const education = draftFromCell(EDUCATION, 'g4', cell({ componentCode: 'EDUCATION', valueType: 'Amount', rate: null, amount: 10000,
    dependantScope: 'Children', maxDependants: 2, limitPeriod: 'Annual' }));
  expect(text(education)).toBe('SAR 10,000 | a year | {who}, up to {count}');
  expect(summarise(education, sar)[1].attach).toBe(true);
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
    ...Object.values(tierKeys), ...Object.values(periodSuffixKeys), ...Object.values(periodChoiceKeys), ...Object.values(dependantKeys), ...Object.values(groupKeys),
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
