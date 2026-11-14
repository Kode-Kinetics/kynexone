'use client';

import type { PayrollRun } from '../../api/payroll';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { enumLabel } from '../../i18n/enumLabel';
import { splitRunsByVoided } from '../../lib/runOptions';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

/** The options of a payroll-run selector: live runs first, voided runs in their own "Voided runs" group. */
export function RunSelectOptions({ runs }: { runs: PayrollRun[] }) {
  const t = useT();
  const fmt = useFormat();
  const monthLabel = (month: number, year: number) => fmt.period(`${MONTHS[month - 1]} ${year}`, 'long');
  const { live, voided } = splitRunsByVoided(runs);
  const option = (r: PayrollRun) => <option key={r.id} value={r.id}>{monthLabel(r.month, r.year)} — {enumLabel(t, 'PayrollRunStatus', r.status)}</option>;
  return (
    <>
      {live.map(option)}
      {voided.length > 0 && <optgroup label={t('Voided runs')}>{voided.map(option)}</optgroup>}
    </>
  );
}
