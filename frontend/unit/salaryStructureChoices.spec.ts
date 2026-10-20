import { expect, test } from '@playwright/test';
import { salaryStructureChoices, type SalaryStructureLike } from '../src/lib/salaryStructureChoices';

const A = 'company-a';
const B = 'company-b';
const structures: SalaryStructureLike[] = [
  { code: 'A-STAFF', name: 'Company A staff', companyId: A, isActive: true },
  { code: 'B-STAFF', name: 'Company B staff', companyId: B, isActive: true },
  { code: 'GROUP-EXEC', name: 'Group executives', companyId: null, isActive: true },
  { code: 'A-OLD', name: 'Company A (superseded)', companyId: A, isActive: false },
  { code: 'SHARED', name: 'Shared (group)', companyId: null, isActive: true },
  { code: 'SHARED', name: 'Shared (company A)', companyId: A, isActive: true },
];

test('offers only active structures of the employee company plus group-wide ones', () => {
  const choices = salaryStructureChoices(structures, A);
  expect(choices.map((c) => c.value).sort()).toEqual(['A-STAFF', 'GROUP-EXEC', 'SHARED']);
  expect(choices.find((c) => c.value === 'GROUP-EXEC')?.groupWide).toBe(true);
  expect(choices.some((c) => c.value === 'B-STAFF')).toBe(false);
  expect(choices.some((c) => c.value === 'A-OLD')).toBe(false);
});

test('a company structure wins over a group-wide one with the same code, as on the server', () => {
  const shared = salaryStructureChoices(structures, A).find((c) => c.value === 'SHARED');
  expect(shared).toEqual({ value: 'SHARED', name: 'Shared (company A)', groupWide: false, retainedCurrent: false });
});

test('without a company only group-wide structures are offered', () => {
  expect(salaryStructureChoices(structures, null).map((c) => c.value).sort()).toEqual(['GROUP-EXEC', 'SHARED']);
});

test('the employee current code is kept, first and marked, even when it is no longer offered', () => {
  const choices = salaryStructureChoices(structures, A, 'A-OLD');
  expect(choices[0]).toEqual({ value: 'A-OLD', name: 'Company A (superseded)', groupWide: false, retainedCurrent: true });
  const unknown = salaryStructureChoices(structures, A, 'PACKAGE-TEST')[0];
  expect(unknown).toEqual({ value: 'PACKAGE-TEST', name: 'PACKAGE-TEST', groupWide: false, retainedCurrent: true });
  // An offered current code is not duplicated.
  expect(salaryStructureChoices(structures, A, 'A-STAFF').filter((c) => c.value === 'A-STAFF')).toHaveLength(1);
});
