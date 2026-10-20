'use client';
import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { payrollApi, type AttendanceReconciliation } from '../../api/payroll';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { requestFailureReason } from '../../lib/requestFailure';
import { msg } from '../../i18n/translations';

/** Why a day is stale, in plain words (AttendanceImpactReconciliation's reason codes). */
const STALE_REASONS: Record<string, string> = {
  impacts_without_record: msg('Deductions with no attendance record'),
  deductions_differ: msg('Deductions do not match the day'),
  punches_differ: msg('The punches for this day have changed'),
  punches_moved: msg('A punch now belongs to another day'),
};

/**
 * The run's attendance check before Process. Stale days (ERR_ATTENDANCE_IMPACT_STALE) block Process: their pending
 * deductions no longer match their punches, and the one action is to recompute exactly those days. The server finds
 * the days again when asked; the browser names none.
 */
export function AttendanceReconciliationPanel({ runId, onRecomputed }: { runId: string; onRecomputed?: () => void }) {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<AttendanceReconciliation | null>(null);
  const [loadError, setLoadError] = useState('');
  const [working, setWorking] = useState(false);
  const [outcome, setOutcome] = useState('');
  const [actionError, setActionError] = useState('');

  const load = useCallback(() => {
    setLoadError('');
    payrollApi.attendanceReconciliation(runId)
      .then(setData)
      .catch(e => { setData(null); setLoadError(requestFailureReason(e)); });
  }, [runId]);

  useEffect(() => { setData(null); setOutcome(''); setActionError(''); load(); }, [load]);

  const recompute = async () => {
    setWorking(true); setActionError(''); setOutcome('');
    try {
      const r = await payrollApi.recomputeStaleAttendance(runId);
      setOutcome(r.remaining === 0
        ? t('{count} attendance day(s) recomputed. Process the run again.', { count: r.recomputed })
        : t('{count} attendance day(s) recomputed; {remaining} still differ (a locked period, or the day changed again). Review them before processing.', { count: r.recomputed, remaining: r.remaining }));
      load();
      onRecomputed?.();
    } catch (e) {
      setActionError(t('The days could not be recomputed. {reason}', { reason: requestFailureReason(e) }));
    } finally { setWorking(false); }
  };

  if (loadError) {
    return (
      <p className="text-sm text-slate-500 dark:text-slate-400" data-testid="attendance-check-failed">
        {t('The attendance check could not be loaded. {reason}', { reason: loadError })}
      </p>
    );
  }
  if (!data || !data.consumesAttendance) return null;
  const stale = data.stale;

  return (
    <div className="space-y-3" data-testid="attendance-reconciliation">
      {stale.dayCount > 0 && (
        <div className="surface space-y-2 border border-rose-200 px-5 py-4 dark:border-rose-500/30" data-testid="attendance-stale">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <p className="flex items-start gap-2 text-sm text-rose-700 dark:text-rose-400">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
              <span>
                {t('{days} attendance day(s) for {employees} employee(s) have deductions that no longer match their punches. This run cannot be processed until they are recomputed.', { days: stale.dayCount, employees: stale.employeeCount })}
              </span>
            </p>
            <button
              type="button"
              onClick={recompute}
              disabled={working}
              className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90 disabled:opacity-50"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${working ? 'animate-spin' : ''}`} aria-hidden />
              {working ? t('Recomputing…') : t('Recompute these days')}
            </button>
          </div>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Each day is worked out again from its punches with today’s rules. Paid and locked days are never changed.')}
          </p>
          <details className="text-sm">
            <summary className="cursor-pointer text-xs font-medium text-slate-600 dark:text-slate-300">{t('Show the days')}</summary>
            <table className="mt-2 w-full text-start text-xs">
              <thead>
                <tr className="text-slate-500">
                  <th className="py-1 text-start font-medium">{t('Employee')}</th>
                  <th className="py-1 text-start font-medium">{t('Date')}</th>
                  <th className="py-1 text-start font-medium">{t('Why')}</th>
                </tr>
              </thead>
              <tbody>
                {stale.days.map(d => (
                  <tr key={`${d.employeeId}-${d.workDate}`} className="border-t border-slate-100 dark:border-white/5">
                    <td className="py-1">{d.employeeName ?? d.employeeCode ?? `#${d.employeeId}`}</td>
                    <td className="py-1">{f.date(d.workDate)}</td>
                    <td className="py-1">{t(STALE_REASONS[d.reason] ?? STALE_REASONS.deductions_differ)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </details>
        </div>
      )}
      {outcome && <p className="text-sm text-emerald-700 dark:text-emerald-400" role="status">{outcome}</p>}
      {actionError && <p className="text-sm text-rose-700 dark:text-rose-400" role="alert">{actionError}</p>}
    </div>
  );
}
