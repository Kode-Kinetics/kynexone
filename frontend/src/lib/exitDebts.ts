/**
 * The loans and salary advances a leaver still owes, as the final settlement plan and the offboarding card list them.
 * Every open loan counts, whatever its repayment method; a loan on hold is listed but never recovered from the final pay.
 */
export interface ExitDebt {
  kind: 'Loan' | 'Advance';
  id: string;
  reference: string;
  /** Loans only: how the loan is repaid (BankTransfer, PayrollDeduction, ...). */
  repaymentMethod: string | null;
  /** A loan on hold (an estate or review case): listed, never recovered by the settlement. */
  held: boolean;
  outstandingBalance: number;
  currency: string | null;
  /** What the final settlement plans to recover from this debt (0 on the offboarding card). */
  plannedRecovery: number;
}

export interface CurrencyAmount { currency: string; amount: number }

export interface ExitDebtSummary {
  count: number;
  /** Outstanding per currency, never added across currencies. */
  byCurrency: CurrencyAmount[];
  /** The held part, per currency. */
  heldByCurrency: CurrencyAmount[];
}

function group(debts: ExitDebt[], fallbackCurrency: string): CurrencyAmount[] {
  const totals = new Map<string, number>();
  for (const d of debts) {
    const currency = d.currency || fallbackCurrency;
    totals.set(currency, Math.round(((totals.get(currency) ?? 0) + d.outstandingBalance) * 100) / 100);
  }
  return [...totals.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([currency, amount]) => ({ currency, amount }));
}

export function exitDebtSummary(debts: ExitDebt[], fallbackCurrency = ''): ExitDebtSummary {
  return {
    count: debts.length,
    byCurrency: group(debts, fallbackCurrency),
    heldByCurrency: group(debts.filter(d => d.held), fallbackCurrency),
  };
}

/** "SAR 10" for one currency, "AED 5 + SAR 10" for several, null for none: amounts in different currencies are never added. */
export function moneyByCurrency(rows: CurrencyAmount[], format: (amount: number, currency: string) => string): string | null {
  if (rows.length === 0) return null;
  return rows.map(r => format(r.amount, r.currency)).join(' + ');
}
