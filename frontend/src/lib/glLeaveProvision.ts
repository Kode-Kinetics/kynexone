/**
 * The "Last measured" cell of the leave provision panel. A rehire before the first Lock of the new service period only has the
 * earlier period's measurement, which a settlement already paid; the server zeroes its figures and flags it, and the cell says so
 * rather than showing the old period as if it were current.
 */
export function lastMeasuredLabel(
  e: { lastMeasuredPeriod: string; notYetMeasured?: boolean },
  t: (key: string) => string,
): string {
  return e.notYetMeasured ? t('Not yet measured') : e.lastMeasuredPeriod;
}
