import { expect, test } from '@playwright/test';
import type { CompanyAccess } from '../src/api/auth';
import type { CompanyDto } from '../src/api/organization';
import { canListCompanies, canOpenGlAndRates, glCompanies, GL_AND_RATES_PERMISSIONS } from '../src/lib/setupAccess';
import { navigationGroups } from '../src/routes/navigation';

const caller = (roles: string[], permissions: string[]) => ({
  hasRole: (role: string) => roles.includes(role),
  hasPermission: (permission: string) => permissions.includes(permission),
});

test('a Payroll Manager or a Finance user opens GL & rates; a Recruiter does not', () => {
  expect(canOpenGlAndRates(caller(['Payroll Manager'], ['finance.gl.manage', 'payroll.rates.manage', 'organization.read']))).toBe(true);
  expect(canOpenGlAndRates(caller(['Finance'], ['finance.gl.read']))).toBe(true);
  expect(canOpenGlAndRates(caller(['Finance Approver'], ['payroll.rates.read']))).toBe(true);
  expect(canOpenGlAndRates(caller(['Recruiter'], ['organization.read']))).toBe(false);
});

test('Companies is offered only to the roles the company list serves', () => {
  for (const role of ['Admin', 'HR Manager', 'HR Officer', 'Auditor']) expect(canListCompanies(caller([role], [])), role).toBe(true);
  // organization.read without one of those roles is refused by GET /api/companies, and used to show an empty page.
  for (const role of ['Payroll Manager', 'Compliance Officer', 'Recruiter', 'HR Assistant']) expect(canListCompanies(caller([role], ['organization.read'])), role).toBe(false);
});

test('GL & rates uses the full company list when there is one, else the companies the sign-in grants', () => {
  const listed = [{ id: 'c1', legalNameEn: 'Listed Co', defaultCurrency: 'SAR' } as CompanyDto];
  const authorized: CompanyAccess[] = [
    { id: 'a1', name: 'Alpha Trading', code: 'ALPHA', countryCode: 'SA', isActive: true },
    { id: 'a2', name: 'Closed Co', code: 'CLOSED', countryCode: 'SA', isActive: false },
  ];
  expect(glCompanies(listed, authorized)).toBe(listed);
  const fallback = glCompanies([], authorized);
  expect(fallback.map(c => [c.id, c.legalNameEn, c.countryCode])).toEqual([['a1', 'Alpha Trading', 'SA']]);
  expect(fallback[0].defaultCurrency).toBe('');
});

test('the Setup menu entry opens for every permission that opens a Setup section', () => {
  const setup = navigationGroups.flatMap(group => group.items).find(item => item.path === '/setup');
  expect(setup?.requiredPermissions).toEqual(expect.arrayContaining(['organization.write', ...GL_AND_RATES_PERMISSIONS]));
});
