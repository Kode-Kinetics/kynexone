/** Unpaid-leave money no payroll run collected before the employee left, as the settlement detail reports it. */
export interface OutstandingLeaveDeductions {
  owedByEmployee: number;
  owedToEmployee: number;
  months: { period: string; amount: number }[];
}

export interface ExitLeaveMoney {
  owedByEmployee: number;
  owedToEmployee: number;
  months: string[];
}

/** The figures the settlement screen shows, or null when nothing is owed either way. The server's amounts are shown as they are. */
export function exitLeaveMoney(source: OutstandingLeaveDeductions | null | undefined): ExitLeaveMoney | null {
  if (!source) return null;
  if (!(source.owedByEmployee > 0) && !(source.owedToEmployee > 0)) return null;
  return { owedByEmployee: source.owedByEmployee, owedToEmployee: source.owedToEmployee, months: source.months.map(m => m.period) };
}
