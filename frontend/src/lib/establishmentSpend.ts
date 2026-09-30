/**
 * Salary spend on the establishment (cost centre / department) view.
 *
 * A department's "current monthly spend" is the sum of its salaries; in a one-person department it
 * IS that person's salary. Since #131 the planning and establishment endpoints send it as null,
 * never zero, to anyone without payroll.read or employees.sensitive. The panel used to add null
 * into its totals as 0 and divide it by the budget, so a restricted viewer saw "0%" utilisation on
 * every budgeted department, which reads as "nothing is being spent". A withheld figure is shown
 * as Restricted, and it never contributes to a total.
 */

export const SPEND_RESTRICTED_LABEL = 'Restricted';

/** Mirrors PlanningController.SpendWithheldNote's reason. */
export const SPEND_RESTRICTED_REASON =
  'Salary spend is shown to holders of the payroll.read or employees.sensitive permission.';

/** True when the server withheld the figure (null or missing), as opposed to a real zero. */
export function isSpendWithheld(spend: number | null | undefined): spend is null | undefined {
  return spend === null || spend === undefined;
}

/**
 * Total spend for a group of departments. If any department's spend is withheld the total is
 * withheld too: a sum of only the visible rows would understate it and look authoritative.
 */
export function totalSpend(values: ReadonlyArray<number | null | undefined>): number | null {
  let total = 0;
  for (const value of values) {
    if (isSpendWithheld(value)) return null;
    total += value;
  }
  return total;
}

export type Utilisation =
  | { state: 'restricted' }
  | { state: 'no-budget' }
  | { state: 'known'; percent: number };

/** Spend as a share of budget. Withheld spend is restricted, whether or not there is a budget. */
export function spendUtilisation(spend: number | null | undefined, budget: number): Utilisation {
  if (isSpendWithheld(spend)) return { state: 'restricted' };
  if (!(budget > 0)) return { state: 'no-budget' };
  return { state: 'known', percent: Math.round((spend / budget) * 100) };
}
