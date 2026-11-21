import { expect, test } from '@playwright/test';
import { normaliseEmployeePackage } from '../src/lib/employeeBenefitPackage';

// The Benefits panel reads data.enrollments.filter(...): a reply without the lists (an empty array, an empty object, a
// partial payload) used to crash the whole record with "Cannot read properties of undefined (reading 'filter')".
test('a reply that is an empty list becomes an empty package with both lists present', () => {
  const pkg = normaliseEmployeePackage([] as unknown, 7);
  expect(pkg.employeeId).toBe(7);
  expect(pkg.enrollments).toEqual([]);
  expect(pkg.additionalRequests).toEqual([]);
});

test('a reply with null or no body becomes an empty package', () => {
  expect(normaliseEmployeePackage(null, 3).enrollments).toEqual([]);
  expect(normaliseEmployeePackage(undefined, 3).additionalRequests).toEqual([]);
});

test('a partial object keeps what it has and defaults the missing lists', () => {
  const pkg = normaliseEmployeePackage({ employeeId: 9, employeeName: 'Test Person', enrollments: [{ id: 'e1' }] } as unknown, 9);
  expect(pkg.employeeName).toBe('Test Person');
  expect(pkg.enrollments).toHaveLength(1);
  expect(pkg.additionalRequests).toEqual([]);
});

test('a non-array list value is replaced by an empty list', () => {
  const pkg = normaliseEmployeePackage({ enrollments: null, additionalRequests: 'x' } as unknown, 1);
  expect(pkg.enrollments).toEqual([]);
  expect(pkg.additionalRequests).toEqual([]);
});
