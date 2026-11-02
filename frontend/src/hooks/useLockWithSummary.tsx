'use client';

import { useRef, useState } from 'react';
import { payrollApi, type PayrollRun } from '../api/payroll';
import { requestFailureReason } from '../lib/requestFailure';
import { resolvePayrollRunCurrency } from '../lib/payrollCurrency';
import { previousRunFor } from '../lib/payrollPipeline';
import { LockSummary, usePeriodLabel } from '../components/payroll/LockSummary';
import { useConfirm } from '../components/ui/useConfirm';
import { useT } from './useT';

export type LockOutcome =
  | { status: 'locked'; run: PayrollRun }
  | { status: 'cancelled' }
  | { status: 'failed'; message: string };

/**
 * Lock a payroll run only after the approver has read the summary in the app's confirm dialog.
 *
 * Everything the summary shows is read when the button is pressed, not remembered from an earlier
 * render: the run's totals, the open validation results, the overrides, the exclusions and the company
 * currency. If any of it cannot be read, or the currency cannot be confirmed, nothing is locked and the
 * outcome says why; the summary is never shown with a figure missing or a guessed currency.
 */
export function useLockWithSummary() {
  const t = useT();
  const period = usePeriodLabel();
  const { confirm, dialog } = useConfirm();
  const busyRef = useRef(false);
  const [busyRunId, setBusyRunId] = useState<string | null>(null);

  const requestLock = async (runId: string): Promise<LockOutcome> => {
    if (busyRef.current) return { status: 'cancelled' };
    busyRef.current = true;
    setBusyRunId(runId);
    try {
      const [runsR, valR, ovrR, popR, coR] = await Promise.allSettled([
        payrollApi.listAllRuns(),
        payrollApi.listValidationResults(runId),
        payrollApi.runValidationOverrides(runId),
        payrollApi.runPopulation(runId),
        payrollApi.listCompanies(),
      ]);
      const failure = [runsR, valR, ovrR, popR].find(r => r.status === 'rejected') as PromiseRejectedResult | undefined;
      const allRuns = runsR.status === 'fulfilled' ? runsR.value : [];
      const run = allRuns.find(r => r.id === runId);
      if (failure || !run || valR.status !== 'fulfilled' || ovrR.status !== 'fulfilled' || popR.status !== 'fulfilled') {
        return { status: 'failed', message: t('The lock summary could not be built, so nothing was locked. {reason}', { reason: failure ? requestFailureReason(failure.reason) : t('The run could not be found.') }) };
      }
      const companies = coR.status === 'fulfilled' && Array.isArray(coR.value) ? coR.value : [];
      const currency = resolvePayrollRunCurrency(run, companies, coR.status === 'fulfilled' && Array.isArray(coR.value) ? 'loaded' : 'failed');
      if (currency.status !== 'resolved') {
        return { status: 'failed', message: t('The currency of this run could not be confirmed, so nothing was locked. {reason}', { reason: currency.status === 'unavailable' ? currency.reason : '' }) };
      }

      const results = Array.isArray(valR.value) ? valR.value : [];
      const openErrors = results.filter(r => r.severity === 'Error' && !r.isResolved).length;
      if (openErrors > 0) {
        return { status: 'failed', message: t('{count, plural, one {# validation error is still open} other {# validation errors are still open}}. Resolve them on the Validation tab, then lock.', { count: openErrors }) };
      }
      const warnings = results.filter(r => r.severity === 'Warning' && !r.isResolved).length;

      const ok = await confirm({
        title: t('Lock payroll for {period}?', { period: period(run) }),
        confirmLabel: t('Lock run'),
        tone: 'primary',
        message: (
          <LockSummary
            data={{
              run,
              previous: previousRunFor(run, allRuns),
              currency: currency.currency,
              openErrors,
              warnings,
              overrides: Array.isArray(ovrR.value?.overrides) ? ovrR.value.overrides.length : 0,
              excluded: popR.value?.excludedCount ?? 0,
            }}
          />
        ),
      });
      if (!ok) return { status: 'cancelled' };

      try {
        return { status: 'locked', run: await payrollApi.lockRun(runId) };
      } catch (e) {
        return { status: 'failed', message: t('The run could not be locked. {reason}', { reason: requestFailureReason(e) }) };
      }
    } finally {
      busyRef.current = false;
      setBusyRunId(null);
    }
  };

  return { requestLock, lockDialog: dialog, lockingRunId: busyRunId };
}
