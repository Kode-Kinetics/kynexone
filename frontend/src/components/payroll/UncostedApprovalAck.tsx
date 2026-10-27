'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { payrollApi } from '../../api/payroll';
import { useT } from '../../hooks/useT';
import { acknowledgeUncosted, type JournalCosting } from '../../lib/payrollCostLock';

/**
 * S-048: with cost accounting on, a final approval seals the journal as it stands. The server refuses (409
 * `uncosted_not_acknowledged`) unless the approver states how many employees post Optional-account lines without a cost centre.
 * This reads that count from the run's journal preview and holds the Approve button until the approver affirms it.
 */
export function useUncostedAck(runId: string, active: boolean) {
  const [costing, setCosting] = useState<JournalCosting | null>(null);
  const [affirmed, setAffirmed] = useState(false);
  const [loadFailed, setLoadFailed] = useState(false);

  const refresh = useCallback(() => {
    setAffirmed(false);
    if (!runId || !active) { setCosting(null); setLoadFailed(false); return Promise.resolve(); }
    return payrollApi.glJournal(runId)
      .then((j) => { setCosting(j.costing ?? null); setLoadFailed(false); })
      // The server still refuses an unacknowledged approval, and the refusal re-reads the count, so a failed read never lets one through.
      .catch(() => { setCosting(null); setLoadFailed(true); });
  }, [runId, active]);

  useEffect(() => { void refresh(); }, [refresh]);

  const count = costing?.enabled ? costing.uncostedEmployees ?? 0 : 0;
  return {
    count,
    needed: count > 0,
    affirmed,
    setAffirmed,
    loadFailed,
    refresh,
    /** The value for `expectedUncostedCount`. */
    expected: () => acknowledgeUncosted(costing?.enabled ? costing : null, affirmed),
  };
}

export function UncostedApprovalAck({ count, affirmed, onChange, amountHint }: { count: number; affirmed: boolean; onChange: (v: boolean) => void; amountHint?: string }) {
  const t = useT();
  return (
    <div data-testid="cc-approve-uncosted-ack" className="rounded-lg border border-amber-200 bg-amber-50 p-3 dark:border-amber-400/20 dark:bg-amber-500/10">
      <div className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-800 dark:text-amber-300" aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="text-xs font-bold text-amber-800 dark:text-amber-300">
            {t('{count, plural, one {# employee posts lines on Optional accounts without a cost centre} other {# employees post lines on Optional accounts without a cost centre}}', { count })}
          </p>
          <p className="mt-0.5 text-xs leading-5 text-amber-800 dark:text-amber-400">
            {t('Those lines will post uncosted. Review the cost-centre assignments first, then state the count to approve.')}{amountHint ? ` ${amountHint}` : ''}
          </p>
          <label className="mt-3 flex cursor-pointer items-start gap-2 text-xs font-semibold text-amber-800 dark:text-amber-300">
            <input type="checkbox" className="mt-0.5 h-4 w-4 shrink-0" checked={affirmed} onChange={(e) => onChange(e.target.checked)} />
            <span>{t('I have reviewed this and confirm that exactly {count} employees post uncosted.', { count })}</span>
          </label>
        </div>
      </div>
    </div>
  );
}
