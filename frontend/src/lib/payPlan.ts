/**
 * Pay-range reconciliation for one department on the Headcount & budget screen.
 *
 * Mirrors the backend rules so the figures can recompute live while a budget or level headcount is
 * being typed. The server value (`payPlan`) is what was last saved; this is what the form says now.
 * A figure that cannot be known is null, never 0 or NaN.
 */

export type PayPlanFlag = 'notComparable' | 'overBudget' | 'belowMinimum' | 'underBudget' | 'currencyMismatch';

export interface PayBand { min: number; mid: number; max: number; currency: string }

export interface PayPlanLevelInput {
  /** Budgeted heads. null = uncontrolled (not planned), 0 = frozen. */
  budgeted: number | null;
  /** null = no range set for the level (or withheld; the caller decides which). */
  pay: PayBand | null;
}

export interface PayPlanInput {
  approved: number;
  budget: number;
  levels: PayPlanLevelInput[];
  /**
   * The company's default currency. A band in any other currency is left unpriced and flagged
   * currencyMismatch, as on the server. Without it the first band sets the reference.
   */
  currency?: string | null;
}

export interface PayPlanResult {
  avgPerHead: number | null;
  plannedMin: number | null;
  plannedMid: number | null;
  plannedMax: number | null;
  /** budget - plannedMid; null when not comparable. */
  difference: number | null;
  /** The server's name for `difference`: budget - plannedMid; null when not comparable. */
  unallocatedBudget: number | null;
  /** Levels with no budget row (uncontrolled). */
  uncontrolledLevels: number;
  pricedHeads: number;
  /** Budgeted heads on levels that have no pay range. */
  unpricedHeads: number;
  /** Levels with budgeted heads but no pay range. */
  unpricedLevels: number;
  allocated: number;
  unallocated: number;
  flags: PayPlanFlag[];
}

/** Unspent share of the budget at which the plan is flagged as under budget. */
export const UNDER_BUDGET_SHARE = 0.25;
const UNDER_BUDGET_DENOMINATOR = 1 / UNDER_BUDGET_SHARE;

/** Money is added and compared as whole minor units (cents) so float drift cannot move a boundary. */
const minor = (n: number) => Math.round(n * 100);

const cur = (c: string | null | undefined) => (c ?? '').trim().toUpperCase();

export function computePayPlan({ approved, budget, levels, currency }: PayPlanInput): PayPlanResult {
  let plannedMin = 0, plannedMid = 0, plannedMax = 0, pricedHeads = 0, unpricedHeads = 0, unpricedLevels = 0, allocated = 0;
  let uncontrolledLevels = 0, currencyMismatch = false;
  // Amounts are never summed across currencies: one reference, everything else is unpriced.
  const reference = cur(currency) || cur(levels.find(l => l.budgeted !== null && l.pay)?.pay?.currency);
  for (const level of levels) {
    const heads = level.budgeted;
    if (heads === null) { uncontrolledLevels += 1; continue; }
    const mismatched = !!level.pay && cur(level.pay.currency) !== reference;
    if (mismatched) currencyMismatch = true;
    if (!(heads > 0)) continue;
    allocated += heads;
    if (!level.pay || mismatched) { unpricedHeads += heads; unpricedLevels += 1; continue; }
    pricedHeads += heads;
    plannedMin += heads * minor(level.pay.min);
    plannedMid += heads * minor(level.pay.mid);
    plannedMax += heads * minor(level.pay.max);
  }

  const avgPerHead = approved > 0 && budget > 0 ? budget / approved : null;
  const budgetMinor = minor(budget);
  const comparable = budget > 0 && pricedHeads > 0;
  const flags: PayPlanFlag[] = [];
  if (!comparable) {
    flags.push('notComparable');
  } else {
    if (plannedMid > budgetMinor) flags.push('overBudget');
    if (plannedMin > budgetMinor) flags.push('belowMinimum');
    // Integer form of (budget - mid) >= 25% of budget.
    if ((budgetMinor - plannedMid) * UNDER_BUDGET_DENOMINATOR >= budgetMinor) flags.push('underBudget');
  }
  if (currencyMismatch) flags.push('currencyMismatch');

  return {
    avgPerHead,
    plannedMin: comparable ? plannedMin / 100 : null,
    plannedMid: comparable ? plannedMid / 100 : null,
    plannedMax: comparable ? plannedMax / 100 : null,
    difference: comparable ? (budgetMinor - plannedMid) / 100 : null,
    unallocatedBudget: comparable ? (budgetMinor - plannedMid) / 100 : null,
    uncontrolledLevels,
    pricedHeads, unpricedHeads, unpricedLevels, allocated,
    unallocated: Math.max(0, approved - allocated),
    flags,
  };
}
