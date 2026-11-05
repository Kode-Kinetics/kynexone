import { expect, test } from '@playwright/test';
import {
  allShownSelected,
  clearShown,
  selectAllShown,
  selectedIds,
  shownEmployees,
  toggleEmployee,
  type PickEmployee,
} from '../src/lib/attendanceSelection';

const e = (id: number, department: string, status = 'Active'): PickEmployee => ({ id, employeeCode: `E${id}`, fullName: `Person ${id}`, department, status });
const none = new Map<number, PickEmployee>();

test('shown = Active only, narrowed by the department filter', () => {
  const rows = [e(1, 'Finance'), e(2, 'Sales'), e(3, 'Finance', 'Terminated'), e(4, 'Finance', 'On Leave')];
  expect(shownEmployees(rows, '').map((r) => r.id)).toEqual([1, 2]);
  expect(shownEmployees(rows, 'Finance').map((r) => r.id)).toEqual([1]);
  expect(shownEmployees(rows, 'Nobody')).toEqual([]);
});

test('toggle adds and removes one employee', () => {
  const a = toggleEmployee(none, e(1, 'A'), 10);
  expect(selectedIds(a.selection)).toEqual([1]);
  const b = toggleEmployee(a.selection, e(1, 'A'), 10);
  expect(selectedIds(b.selection)).toEqual([]);
});

test('toggle refuses to go past the cap and says so', () => {
  const full = new Map([[1, e(1, 'A')], [2, e(2, 'A')]]);
  const r = toggleEmployee(full, e(3, 'A'), 2);
  expect(r.atCap).toBe(true);
  expect(selectedIds(r.selection)).toEqual([1, 2]);
  // removing at the cap is always allowed
  expect(selectedIds(toggleEmployee(full, e(1, 'A'), 2).selection)).toEqual([2]);
});

test('select all shown adds exactly the shown rows (search + department already applied) and keeps earlier picks', () => {
  const earlier = toggleEmployee(none, e(99, 'Legal'), 100).selection;
  const shown = shownEmployees([e(1, 'Finance'), e(2, 'Sales'), e(3, 'Finance')], 'Finance');
  const r = selectAllShown(earlier, shown, 100);
  expect(selectedIds(r.selection)).toEqual([1, 3, 99]);
  expect(r.added).toBe(2);
  expect(r.capped).toBe(false);
});

test('select all shown stops at the cap and reports it', () => {
  const shown = [e(1, 'A'), e(2, 'A'), e(3, 'A')];
  const r = selectAllShown(none, shown, 2);
  expect(selectedIds(r.selection)).toEqual([1, 2]);
  expect(r.capped).toBe(true);
});

test('selections survive a new search: they live outside the result list', () => {
  const picked = selectAllShown(none, [e(1, 'A'), e(2, 'A')], 10).selection;
  const afterNewSearch = shownEmployees([e(5, 'B')], '');
  expect(allShownSelected(picked, afterNewSearch)).toBe(false);
  expect(selectedIds(picked)).toEqual([1, 2]);
});

test('allShownSelected is false for an empty list', () => {
  expect(allShownSelected(none, [])).toBe(false);
  const picked = selectAllShown(none, [e(1, 'A')], 10).selection;
  expect(allShownSelected(picked, [e(1, 'A')])).toBe(true);
});

test('clear shown removes only what is shown, not selections made under other searches', () => {
  const picked = selectAllShown(none, [e(1, 'A'), e(2, 'A'), e(3, 'B')], 10).selection;
  const cleared = clearShown(picked, [e(1, 'A'), e(2, 'A')]);
  expect(selectedIds(cleared)).toEqual([3]);
});

test('functions never mutate their input', () => {
  const before = new Map([[1, e(1, 'A')]]);
  toggleEmployee(before, e(2, 'A'), 10);
  selectAllShown(before, [e(3, 'A')], 10);
  clearShown(before, [e(1, 'A')]);
  expect(selectedIds(before)).toEqual([1]);
});
