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

/**
 * The payslip's month in the viewer's language ("September 2026", "سبتمبر 2026"), formatted from its year
 * and month rather than the server's English period label. Gregorian months with Western digits in Arabic,
 * as the rest of the app. A slip whose run is missing (year 0) keeps the server's label, or a dash.
 */
export function payslipMonthLabel(year: number, month: number, locale: string, fallback = ''): string {
  if (!year || !month || month < 1 || month > 12) return fallback || '—';
  const tag = locale === 'ar' ? 'ar-u-nu-latn' : locale;
  return new Intl.DateTimeFormat(tag, { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, 1)));
}

/**
 * A payslip line's name for the viewer: the pay component's Arabic name in Arabic when the catalogue has
 * one, otherwise the translated name of a standard line (Basic Salary, Net Pay…), otherwise as stored.
 */
export function payslipLineName(line: { name: string; nameAr?: string | null }, locale: string, translate: (key: string) => string): string {
  if (locale !== 'ar') return line.name;
  return line.nameAr?.trim() || translate(line.name);
}
