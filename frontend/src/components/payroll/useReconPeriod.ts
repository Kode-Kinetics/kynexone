'use client';

import { parsePeriod } from '../../lib/payrollRecon';
import { usePeriodLabel } from './LockSummary';

/** "2026-11" as "November 2026" in the viewer's language and calendar; the raw text when it is not a month. */
export function useReconPeriod(): (period: string | null | undefined) => string {
  const label = usePeriodLabel();
  return period => {
    const p = parsePeriod(period);
    return p ? label(p) : (period ?? '—');
  };
}
