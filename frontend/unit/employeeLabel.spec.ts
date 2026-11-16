import { expect, test } from '@playwright/test';
import { employeeLabel, employeeLabelsFromRecords } from '../src/lib/employeeLabel';

// A dropdown or table that knows the employee's name must not print "#226".
test.describe('employee labels', () => {
  test('a known name reads as code and name, never the id', () => {
    expect(employeeLabel(226, { 226: 'EVT2-0001 · Fahad Alharbi' }, id => `Employee ${id}`)).toBe('EVT2-0001 · Fahad Alharbi');
  });

  test('an unknown name falls back to the supplied words, not a bare hash and number', () => {
    expect(employeeLabel(226, {}, id => `Employee ${id}`)).toBe('Employee 226');
    expect(employeeLabel(226, undefined, id => `Employee ${id}`)).toBe('Employee 226');
  });

  test('labels are built from records that carry a name, skipping those that do not', () => {
    const labels = employeeLabelsFromRecords([
      { employeeId: 1, employeeName: 'Fahad Alharbi', employeeCode: 'EVT2-0001' },
      { employeeId: 2, employeeName: 'Khalid Alotaibi' },
      { employeeId: 3 },
    ]);
    expect(labels).toEqual({ 1: 'EVT2-0001 · Fahad Alharbi', 2: 'Khalid Alotaibi' });
  });
});
