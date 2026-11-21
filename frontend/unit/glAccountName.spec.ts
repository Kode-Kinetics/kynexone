import { expect, test } from '@playwright/test';
import { glAccountName } from '../src/lib/glAccountName';
import { erpFormatName } from '../src/lib/erpFormatText';
import { translate } from '../src/i18n/translations';

const en = (key: string) => translate('en', key);
const ar = (key: string) => translate('ar', key);
const latin = (s: string) => s.match(/[A-Za-z]{3,}/g) ?? [];

// The names the chart of accounts is seeded with (Models/FinanceGl.cs) plus the label the ledger stored for attendance
// contra postings. A tenant may rename any account; a renamed account is shown exactly as the customer typed it.
const DEFAULT_NAMES = [
  'Cash/Bank', 'Employee Loans Receivable', 'Employee Salary Advances', 'Employee Overpayment Receivable', 'Prepaid Statutory Remittance',
  'Salaries Payable', 'Social Insurance Payable (Employee)', 'Income Tax Payable', 'Fixed Deductions Payable',
  'Social Insurance Employer Payable', 'Loan & Advance Deductions Payable', 'Other Deductions', 'Bonus Payable',
  'End of Service Benefit Provision', 'Final Settlement Payable', 'Accrued Annual Leave Provision', 'Opening Balance Equity',
  'Basic Salary Expense', 'Housing Allowance Expense', 'Transport Allowance Expense', 'Other Allowances Expense', 'Overtime Expense',
  'Absence, Late & Early Deductions (contra)', 'Attendance & Late Deductions (contra)', 'Unpaid Leave Deductions (contra)', 'Other Earnings',
  'Employer Social Insurance Expense', 'End of Service Benefit Expense', 'Leave Encashment Expense', 'Payment in Lieu of Notice',
  'Final Settlement Deductions (contra)', 'Employee Receivable Write-off Expense', 'Annual Leave Provision Expense',
  'Payroll Rounding Differences', 'Employee Bonus Expense',
];

test.describe('GL account names', () => {
  test('English shows the name as stored', () => {
    for (const name of DEFAULT_NAMES) expect(glAccountName(en, name)).toBe(name);
  });
  test('every seeded account name reads in Arabic', () => {
    for (const name of DEFAULT_NAMES) {
      const shown = glAccountName(ar, name);
      expect(shown, name).not.toBe(name);
      expect(latin(shown), name).toEqual([]);
    }
  });
  test('a name the customer chose is never altered', () => {
    expect(glAccountName(ar, 'Evostel salaries control')).toBe('Evostel salaries control');
  });
});

test('the Oracle Fusion template format reads in Arabic', () => {
  const shown = erpFormatName(ar, 'Oracle Fusion FBDI GlInterface.csv (template – validate in a test instance)');
  expect(shown).toContain('Oracle Fusion');
  expect(shown).not.toContain('template');
});
