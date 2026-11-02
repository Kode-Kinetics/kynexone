import { expect, test } from '@playwright/test';
import { computePayPlan, type PayPlanLevelInput } from '../src/lib/payPlan';

const band = (min: number, mid: number, max: number) => ({ min, mid, max, currency: 'SAR' });
const lvl = (budgeted: number | null, pay: PayPlanLevelInput['pay']): PayPlanLevelInput => ({ budgeted, pay });

test('owner example: budget 10,000 and approved headcount 5 is 2,000 per head', () => {
  expect(computePayPlan({ approved: 5, budget: 10000, levels: [] }).avgPerHead).toBe(2000);
});

test('average per head is null, never 0 or NaN, when budget or headcount is missing', () => {
  expect(computePayPlan({ approved: 0, budget: 10000, levels: [] }).avgPerHead).toBeNull();
  expect(computePayPlan({ approved: 5, budget: 0, levels: [] }).avgPerHead).toBeNull();
  expect(computePayPlan({ approved: -1, budget: 10000, levels: [] }).avgPerHead).toBeNull();
});

test('planned cost sums heads x band over budgeted, priced levels only', () => {
  const plan = computePayPlan({
    approved: 6, budget: 10000,
    levels: [lvl(5, band(1500, 2000, 2500)), lvl(1, band(3000, 4000, 5000)), lvl(null, band(1, 1, 1)), lvl(3, null)],
  });
  expect(plan.plannedMin).toBe(5 * 1500 + 3000);
  expect(plan.plannedMid).toBe(5 * 2000 + 4000);
  expect(plan.plannedMax).toBe(5 * 2500 + 5000);
  expect(plan.pricedHeads).toBe(6);
  expect(plan.unpricedHeads).toBe(3);
  expect(plan.unpricedLevels).toBe(1);
});

test('over budget only when planned mid is strictly greater than the budget', () => {
  const over = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, band(2000, 2200, 2400))] });
  expect(over.plannedMid).toBe(11000);
  expect(over.difference).toBe(-1000);
  expect(over.flags).toContain('overBudget');
  const equal = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, band(2000, 2000, 2400))] });
  expect(equal.flags).not.toContain('overBudget');
  expect(equal.flags).not.toContain('underBudget');
});

test('below minimum when even the cheapest plan exceeds the budget', () => {
  const plan = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, band(2500, 3000, 3500))] });
  expect(plan.flags).toContain('belowMinimum');
  expect(plan.flags).toContain('overBudget');
  const ok = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, band(2000, 2000, 2500))] });
  expect(ok.flags).not.toContain('belowMinimum');
});

test('under budget when at least 25% of the budget is unspent', () => {
  const exactly = computePayPlan({ approved: 4, budget: 10000, levels: [lvl(3, band(2500, 2500, 2500))] });
  expect(exactly.plannedMid).toBe(7500);
  expect(exactly.flags).toContain('underBudget');
  const near = computePayPlan({ approved: 4, budget: 10000, levels: [lvl(3, band(2500, 2501, 2500))] });
  expect(near.flags).not.toContain('underBudget');
});

test('not comparable with no budget or nothing priced; no other flag is raised', () => {
  const noBudget = computePayPlan({ approved: 5, budget: 0, levels: [lvl(5, band(1, 2, 3))] });
  expect(noBudget.flags).toEqual(['notComparable']);
  expect(noBudget.difference).toBeNull();
  const nothingPriced = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, null), lvl(null, band(1, 2, 3))] });
  expect(nothingPriced.flags).toEqual(['notComparable']);
  expect(nothingPriced.plannedMid).toBeNull();
});

test('allocation: unallocated heads are approved minus budgeted, never negative', () => {
  expect(computePayPlan({ approved: 5, budget: 0, levels: [lvl(2, null), lvl(1, null)] }).unallocated).toBe(2);
  expect(computePayPlan({ approved: 2, budget: 0, levels: [lvl(2, null), lvl(1, null)] }).unallocated).toBe(0);
});

test('parity with the server rules on the same fixtures (owner example 10,000 / 5 heads)', () => {
  // Server: Plan(budget 10000, approved 5, levels [3 budgeted at mid 2500, 1 uncontrolled]) -> avg 2000, mid 7500, unallocatedBudget 2500.
  const plan = computePayPlan({ approved: 5, budget: 10000, currency: 'SAR', levels: [lvl(3, band(2000, 2500, 3000)), lvl(null, band(1, 1, 1))] });
  expect(plan.avgPerHead).toBe(2000);
  expect(plan.plannedMin).toBe(6000);
  expect(plan.plannedMid).toBe(7500);
  expect(plan.plannedMax).toBe(9000);
  expect(plan.unallocatedBudget).toBe(2500);
  expect(plan.uncontrolledLevels).toBe(1);
  expect(plan.unallocated).toBe(2);
  expect(plan.flags).toEqual(['underBudget']);
});

test('server: a budgeted level with no band adds its heads to unpricedHeads and is not summed', () => {
  const plan = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(3, band(2000, 2500, 3000)), lvl(2, null)] });
  expect(plan.unpricedHeads).toBe(2);
  expect(plan.plannedMid).toBe(7500);
});

test('server: notComparable suppresses over, below and under', () => {
  const plan = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(5, null)] });
  expect(plan.flags).toEqual(['notComparable']);
  expect(plan.unallocatedBudget).toBeNull();
});

test('currency: a band in another currency is unpriced, flagged, and never summed', () => {
  const usd = { min: 1, mid: 1000, max: 2000, currency: 'USD' };
  const plan = computePayPlan({ approved: 5, budget: 10000, currency: 'SAR', levels: [lvl(3, band(2000, 2500, 3000)), lvl(2, usd)] });
  expect(plan.plannedMid).toBe(7500);
  expect(plan.unpricedHeads).toBe(2);
  expect(plan.unpricedLevels).toBe(1);
  expect(plan.flags).toContain('currencyMismatch');
  expect(plan.flags).toContain('underBudget');
});

test('currency: with no company currency the first priced band sets the reference; only mismatching levels drop out', () => {
  const usd = { min: 1, mid: 1000, max: 2000, currency: 'USD' };
  const plan = computePayPlan({ approved: 5, budget: 10000, levels: [lvl(3, band(2000, 2500, 3000)), lvl(2, usd)] });
  expect(plan.plannedMid).toBe(7500);
  expect(plan.flags).toContain('currencyMismatch');
});

test('currency: when every band is in a different currency nothing is compared', () => {
  const usd = { min: 1, mid: 1000, max: 2000, currency: 'USD' };
  const plan = computePayPlan({ approved: 5, budget: 10000, currency: 'SAR', levels: [lvl(5, usd)] });
  expect(plan.flags).toEqual(['notComparable', 'currencyMismatch']);
  expect(plan.plannedMid).toBeNull();
});

test('money is compared in minor units: a fractional mid x heads sums exactly (1500.5 x 3)', () => {
  const plan = computePayPlan({ approved: 3, budget: 4501.5, levels: [lvl(3, band(1500.5, 1500.5, 1500.5))] });
  expect(plan.plannedMid).toBe(4501.5);
  expect(plan.difference).toBe(0);
  expect(plan.flags).not.toContain('overBudget');
  expect(plan.flags).not.toContain('belowMinimum');
});

test('float drift cannot flip a boundary: 3 x 1.1 against a budget of 3.3 is not over budget', () => {
  // 3 * 1.1 is 3.3000000000000003 in floating point, which would read as over budget.
  expect(3 * 1.1 > 3.3).toBe(true);
  const plan = computePayPlan({ approved: 3, budget: 3.3, levels: [lvl(3, band(1.1, 1.1, 1.1))] });
  expect(plan.plannedMid).toBe(3.3);
  expect(plan.flags).not.toContain('overBudget');
  expect(plan.flags).not.toContain('belowMinimum');
  expect(plan.difference).toBe(0);
});

test('a one-cent overrun is over budget, and the under-budget boundary is exact in minor units', () => {
  expect(computePayPlan({ approved: 1, budget: 99.99, levels: [lvl(1, band(100, 100, 100))] }).flags).toContain('overBudget');
  // 0.1 + 0.2 style drift on the 25% line: unspent 25.05 of 100.20 is exactly 25%.
  const plan = computePayPlan({ approved: 1, budget: 100.2, levels: [lvl(1, band(75.15, 75.15, 75.15))] });
  expect(plan.flags).toContain('underBudget');
});

test('without a company currency, mixed band currencies are not summed: most heads sets the reference', () => {
  const plan = computePayPlan({
    approved: 5, budget: 10000,
    levels: [lvl(3, { min: 1000, mid: 1000, max: 1000, currency: 'USD' }), lvl(2, { min: 500, mid: 500, max: 500, currency: 'SAR' })],
  });
  expect(plan.plannedMid).toBe(3000);
  expect(plan.unpricedHeads).toBe(2);
  expect(plan.flags).toContain('currencyMismatch');
});

test('reference does not depend on level order', () => {
  const a = lvl(2, { min: 1000, mid: 1000, max: 1000, currency: 'USD' });
  const b = lvl(2, { min: 500, mid: 500, max: 500, currency: 'SAR' });
  expect(computePayPlan({ approved: 4, budget: 10000, levels: [a, b] }).plannedMid)
    .toBe(computePayPlan({ approved: 4, budget: 10000, levels: [b, a] }).plannedMid);
});
