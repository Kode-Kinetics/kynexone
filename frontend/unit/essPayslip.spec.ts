import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { ESS_PAYSLIPS_PATH, payslipSections } from '../src/lib/essPayslip';

// Asif's September payslip, as GET /api/ess/payslips/{id} returns it: a non-Saudi driver on
// SAR 4,800 with a SAR 400 loan instalment. The employer's 2% occupational hazard (96) is an employer
// cost. Before the fix the page counted it and showed "Total deductions 496" against "Net pay 4,400".
const ASIF = [
  { name: 'Basic Salary', amount: 4800, type: 'Earning' },
  { name: 'Loan instalment', amount: 400, type: 'Deduction' },
  { name: 'Occupational Hazard (Employer)', amount: 96, type: 'EmployerContribution' },
  { name: 'Net pay', amount: 4400, type: 'Net' },
];

test('the employer hazard is listed separately and never counted in the deductions', () => {
  const s = payslipSections(ASIF);
  expect(s.totalDeductions).toBe(400);
  expect(s.deductions.map((l) => l.name)).toEqual(['Loan instalment']);
  expect(s.employerContributions.map((l) => l.name)).toEqual(['Occupational Hazard (Employer)']);
  expect(s.employerTotal).toBe(96);
});

test('gross minus deductions equals net', () => {
  const s = payslipSections(ASIF);
  expect(s.gross).toBe(4800);
  expect(s.net).toBe(4400);
  expect(s.reconciles).toBe(true);
});

test('a Saudi payslip counts the employee GOSI share and not the employer share', () => {
  const s = payslipSections([
    { name: 'Basic Salary', amount: 10000, type: 'Earning' },
    { name: 'GOSI Annuities (Employee)', amount: 900, type: 'Deduction' },
    { name: 'SANED (Employee)', amount: 75, type: 'Deduction' },
    { name: 'GOSI Annuities (Employer)', amount: 900, type: 'EmployerContribution' },
    { name: 'SANED (Employer)', amount: 75, type: 'EmployerContribution' },
    { name: 'Occupational Hazard (Employer)', amount: 200, type: 'EmployerContribution' },
    { name: 'Net pay', amount: 9025, type: 'Net' },
  ]);
  expect(s.totalDeductions).toBe(975);
  expect(s.employerTotal).toBe(1175);
  expect(s.reconciles).toBe(true);
});

test('figures that do not add up are flagged, not hidden', () => {
  const s = payslipSections([
    { name: 'Basic Salary', amount: 4800, type: 'Earning' },
    { name: 'Loan instalment', amount: 400, type: 'Deduction' },
    { name: 'Net pay', amount: 4300, type: 'Net' },
  ]);
  expect(s.reconciles).toBe(false);
});

// The self-service payslip links went to /payroll, which an employee (ess.read, no payroll.read) is
// denied. They must go to the employee's own payslip page, and that page must admit ess.read.
test('self-service payslip links open a page an employee can access', () => {
  const root = join(__dirname, '..');
  const ess = readFileSync(join(root, 'src/views/EmployeeSelfServicePage.tsx'), 'utf8');
  expect(ess).not.toMatch(/['"]\/payroll['"]/);
  // "View Payslip" button and the Last Payslip tile; the Pay tab is in routes/essSections.ts.
  expect(ess.match(/href=\{ESS_PAYSLIPS_PATH\}/g)?.length).toBe(2);
  expect(readFileSync(join(root, 'src/routes/essSections.ts'), 'utf8')).toMatch(/label: 'My Payslips', tab: 'Payslips', path: '\/ess\/payslips'/);

  const page = readFileSync(join(root, 'app/(dashboard)', ESS_PAYSLIPS_PATH.slice(1), 'page.tsx'), 'utf8');
  expect(page).toMatch(/<PermissionGate permissions=\{\['ess\.read'\]\}>/);
});
