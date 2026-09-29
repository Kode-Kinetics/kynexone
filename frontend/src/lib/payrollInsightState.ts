export type PayrollInsightState = 'loading' | 'unavailable' | 'empty' | 'active';

export function payrollInsightState(loading: boolean, failed: boolean, count: number): PayrollInsightState {
  if (loading) return 'loading';
  if (failed) return 'unavailable';
  return count > 0 ? 'active' : 'empty';
}

export function payrollInsightEmptyCopy(state: PayrollInsightState): string {
  if (state === 'unavailable') {
    return 'Payroll insight status is unavailable. A failed alert request does not prove payroll is clear.';
  }
  if (state === 'empty') {
    return 'No unacknowledged payroll insights were returned. Complete payroll validation before approval.';
  }
  return '';
}

export function payrollPeriodState(hasRun: boolean, overviewLoaded: boolean): 'unavailable' | 'no-run' | 'has-run' {
  if (!overviewLoaded) return 'unavailable';
  return hasRun ? 'has-run' : 'no-run';
}
