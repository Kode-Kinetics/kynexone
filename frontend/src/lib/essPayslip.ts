import type { EssPayslipLine } from '../api/ess';

/**
 * The employee's own payslips. Self-service links ("View Payslip", "My Payslips", the Last Payslip
 * card) must point HERE, never at /payroll: /payroll is the payroll team's screen and is gated on
 * payroll.read, which an employee does not hold, so the link bounced them to "Access Denied".
 */
export const ESS_PAYSLIPS_PATH = '/ess/payslips';

export interface PayslipSections {
  earnings: EssPayslipLine[];
  deductions: EssPayslipLine[];
  employerContributions: EssPayslipLine[];
  gross: number;
  totalDeductions: number;
  employerTotal: number;
  net: number;
  /** gross − deductions = net, to the cent. */
  reconciles: boolean;
}

const cents = (n: number) => Math.round(n * 100);
const sum = (lines: EssPayslipLine[]) => lines.reduce((acc, l) => acc + cents(l.amount), 0) / 100;

/**
 * Splits a payslip's lines into its sections. Employer contributions (e.g. the employer's GOSI
 * occupational hazard) are an employer cost: they get their own section and are never counted in
 * the employee's deductions.
 */
export function payslipSections(lines: EssPayslipLine[]): PayslipSections {
  const earnings = lines.filter((l) => l.type === 'Earning');
  const deductions = lines.filter((l) => l.type === 'Deduction');
  const employerContributions = lines.filter((l) => l.type === 'EmployerContribution');
  const netLines = lines.filter((l) => l.type === 'Net');
  const gross = sum(earnings);
  const totalDeductions = sum(deductions);
  const net = netLines.length > 0 ? sum(netLines) : (cents(gross) - cents(totalDeductions)) / 100;
  return {
    earnings,
    deductions,
    employerContributions,
    gross,
    totalDeductions,
    employerTotal: sum(employerContributions),
    net,
    reconciles: cents(gross) - cents(totalDeductions) === cents(net),
  };
}
