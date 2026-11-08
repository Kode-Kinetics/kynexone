import { expect, test } from '@playwright/test';
import type { MatrixComponent, MatrixGrade } from '../src/api/entitlements';
import { blankDraft, cellKey, type CellDraft } from '../src/lib/entitlementMatrix';
import {
  cellView, gradeWindow, monthlyFixedTotals, needsValueCodes, rowChip, statusSummary,
} from '../src/lib/benefitsGrid';

// Benefits by grade: the pure rules behind the grid's layout (what a cell shows, which chip a row carries, which grades are
// in view, what the status band says, and the one total that is honest to show).

const component = (over: Partial<MatrixComponent>): MatrixComponent => ({
  code: 'TRANSPORT', nameEn: 'Transport', nameAr: 'مواصلات', class: 'QiwaWage', floor: 'None', group: 'Wage',
  allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [], allowedDependantScopes: ['None'], allowedLimitPeriods: ['Monthly'],
  defaultLimitPeriod: 'Monthly', isFloor: false, canBeSkipped: true, isLoanFacility: false, floorReason: null, ...over,
});
const grades: MatrixGrade[] = [1, 2, 3].map(i => ({ id: `g${i}`, code: `G${i}`, name: `Grade ${i}`, nameAr: null, level: i * 10 }));
const offeredAmount = (c: MatrixComponent, gradeId: string, amount: string): CellDraft =>
  ({ ...blankDraft(c, gradeId), state: 'offered', valueType: 'Amount', amount, limitPeriod: 'Monthly' });

const base = { state: 'offered' as const, dirty: false, invalid: false, pending: false, fixedElsewhere: false, canEdit: true };

test('every editable value cell shows a pencil, and an empty one says + Set value instead of a bare dash', () => {
  expect(cellView(base)).toMatchObject({ kind: 'set', pencil: true, setCall: false, interactive: true });
  expect(cellView({ ...base, state: 'notOffered' })).toMatchObject({ kind: 'notOffered', pencil: true, setCall: false });
  expect(cellView({ ...base, state: 'unset' })).toMatchObject({ kind: 'unset', pencil: false, setCall: true, interactive: true });
  expect(cellView({ ...base, dirty: true })).toMatchObject({ kind: 'edited', pencil: true });
  expect(cellView({ ...base, state: 'unset', startsOn: '2026-12-01' })).toMatchObject({ kind: 'starts', pencil: true, setCall: false });
  expect(cellView({ ...base, invalid: true, dirty: true })).toMatchObject({ kind: 'error', pencil: true });
});

test('a cell the viewer cannot change has no pencil and no call to set it; one Finance or Loans owns is not a control', () => {
  expect(cellView({ ...base, canEdit: false })).toMatchObject({ kind: 'set', pencil: false, setCall: false, interactive: true });
  expect(cellView({ ...base, state: 'unset', canEdit: false })).toMatchObject({ kind: 'unset', pencil: false, setCall: false });
  expect(cellView({ ...base, pending: true })).toMatchObject({ kind: 'locked', pencil: false, setCall: false, interactive: false });
  expect(cellView({ ...base, fixedElsewhere: true })).toMatchObject({ kind: 'readonly', pencil: false, interactive: false });
});

test('a row carries at most one chip, chosen by priority: Finance first, then the company mode, then New, then Added by you', () => {
  const row = { pending: false, mode: 'Group' as const, companyView: true, isNew: false, isCustom: true };
  expect(rowChip({ ...row, pending: true, mode: 'Tailored', isNew: true })).toMatchObject({ key: 'Waiting for Finance' });
  expect(rowChip({ ...row, mode: 'Skipped', isNew: true })).toMatchObject({ key: 'Not offered' });
  expect(rowChip({ ...row, mode: 'Tailored' })).toMatchObject({ key: 'Tailored', title: 'Tailored for this company' });
  expect(rowChip({ ...row, mode: 'Adopted' })).toMatchObject({ key: 'Adopted' });
  expect(rowChip({ ...row, isNew: true })).toMatchObject({ key: 'New' });
  expect(rowChip(row)).toMatchObject({ key: 'Added by you' });
  expect(rowChip({ ...row, isCustom: false })).toBeNull();
  // The group view has no company mode to show.
  expect(rowChip({ ...row, companyView: false, mode: 'Tailored', isCustom: false })).toBeNull();
});

test('the grades in view are the columns at least 60% inside the scroller, 1-based, in either direction', () => {
  const cols = [0, 1, 2, 3, 4, 5, 6, 7].map(i => ({ start: i * 120, end: i * 120 + 120 }));
  expect(gradeWindow({ start: 0, end: 600 }, cols)).toEqual({ from: 1, to: 5, total: 8 });
  // A column cut in half is not counted as in view.
  expect(gradeWindow({ start: 0, end: 660 }, cols)).toEqual({ from: 1, to: 5, total: 8 });
  expect(gradeWindow({ start: 0, end: 700 }, cols)).toEqual({ from: 1, to: 6, total: 8 });
  expect(gradeWindow({ start: 240, end: 840 }, cols)).toEqual({ from: 3, to: 7, total: 8 });
  expect(gradeWindow({ start: 0, end: 600 }, [])).toEqual({ from: 0, to: 0, total: 0 });
});

test('the status band says how many values are set, and only mentions Finance, unpublished changes and the next start when there are any', () => {
  expect(statusSummary({ set: 36, total: 42, pendingFinance: 0, changes: 0, nextStart: null }).map(i => i.kind)).toEqual(['values']);
  expect(statusSummary({ set: 36, total: 42, pendingFinance: 2, changes: 3, nextStart: '2026-12-01' })).toEqual([
    { kind: 'values', set: 36, total: 42, complete: false },
    { kind: 'finance', count: 2 },
    { kind: 'changes', count: 3 },
    { kind: 'next', date: '2026-12-01' },
  ]);
  expect(statusSummary({ set: 42, total: 42, pendingFinance: 0, changes: 0, nextStart: null })[0]).toMatchObject({ complete: true });
});

test('the "Needs a value" filter keeps only benefits with an empty grade, never one waiting for Finance or with a value starting later', () => {
  const housing = component({ code: 'HOUSING' });
  const phone = component({ code: 'ALW_PHONE' });
  const meal = component({ code: 'BEN_MEAL' });
  const drafts: Record<string, CellDraft> = {
    [cellKey('g1', 'HOUSING')]: offeredAmount(housing, 'g1', '100'),
    [cellKey('g2', 'HOUSING')]: offeredAmount(housing, 'g2', '100'),
    [cellKey('g3', 'HOUSING')]: offeredAmount(housing, 'g3', '100'),
    [cellKey('g1', 'ALW_PHONE')]: blankDraft(phone, 'g1'),
    [cellKey('g2', 'ALW_PHONE')]: offeredAmount(phone, 'g2', '10'),
    [cellKey('g3', 'ALW_PHONE')]: offeredAmount(phone, 'g3', '10'),
    [cellKey('g1', 'BEN_MEAL')]: blankDraft(meal, 'g1'),
    [cellKey('g2', 'BEN_MEAL')]: blankDraft(meal, 'g2'),
    [cellKey('g3', 'BEN_MEAL')]: blankDraft(meal, 'g3'),
  };
  const codes = needsValueCodes([housing, phone, meal], grades, drafts, new Set([cellKey('g2', 'BEN_MEAL')]), new Set());
  expect([...codes].sort()).toEqual(['ALW_PHONE', 'BEN_MEAL']);
  expect([...needsValueCodes([housing, phone, meal], grades, drafts, new Set(), new Set(['BEN_MEAL']))].sort()).toEqual(['ALW_PHONE']);
});

test('the monthly total adds only fixed monthly amounts of wage benefits, is left out when it would add up one row, and ignores percentages', () => {
  const transport = component({ code: 'TRANSPORT' });
  const phone = component({ code: 'ALW_PHONE' });
  const housing = component({ code: 'HOUSING' });
  const medical = component({ code: 'MEDICAL', group: 'Contract', class: 'Contractual' });
  const drafts: Record<string, CellDraft> = {
    [cellKey('g1', 'TRANSPORT')]: offeredAmount(transport, 'g1', '400'),
    [cellKey('g2', 'TRANSPORT')]: offeredAmount(transport, 'g2', '600.50'),
    [cellKey('g1', 'ALW_PHONE')]: offeredAmount(phone, 'g1', '100'),
    [cellKey('g1', 'HOUSING')]: { ...blankDraft(housing, 'g1'), state: 'offered', valueType: 'PercentOfBasic', percent: '25', limitPeriod: 'Monthly' },
    [cellKey('g1', 'MEDICAL')]: offeredAmount(medical, 'g1', '5000'),
  };
  const totals = monthlyFixedTotals([transport, phone, housing, medical], grades, drafts);
  expect(totals).not.toBeNull();
  expect(totals!.byGrade).toEqual({ g1: 500, g2: 600.5 });
  expect(totals!.percentRows).toBe(1);
  // One contributing row is not a total of anything.
  expect(monthlyFixedTotals([transport, housing], grades, { [cellKey('g1', 'TRANSPORT')]: offeredAmount(transport, 'g1', '400') })).toBeNull();
  // A draft that is not valid yet does not count.
  expect(monthlyFixedTotals([transport, phone], grades, {
    [cellKey('g1', 'TRANSPORT')]: offeredAmount(transport, 'g1', 'abc'), [cellKey('g1', 'ALW_PHONE')]: offeredAmount(phone, 'g1', '100'),
  })).toBeNull();
});
