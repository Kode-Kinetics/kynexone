/** Splits payroll runs for a selector: live runs first (in the order given), voided runs set apart so they never sit beside the live one. */
export function splitRunsByVoided<T extends { status: string }>(runs: T[]): { live: T[]; voided: T[] } {
  return { live: runs.filter(r => r.status !== 'Voided'), voided: runs.filter(r => r.status === 'Voided') };
}
