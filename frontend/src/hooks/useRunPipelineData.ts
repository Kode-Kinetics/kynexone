'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  payrollApi,
  type PayrollApproval, type PayrollPaymentBatch, type PayrollRun, type PayrollValidationResult,
} from '../api/payroll';
import type { PayrollReadinessWithPrerequisites } from '../lib/payrollPrerequisites';
import type { Prechecks } from '../lib/payrollPipeline';

export type LoadState = 'loading' | 'ready' | 'failed';

/**
 * Everything the pipeline reads about one run, from the existing endpoints. Each source fails on its own:
 * a figure that could not be read is `null`, never zero, so the pipeline says "could not be read" instead
 * of claiming "nothing open".
 */
export function useRunPipelineData(runId: string | null, refreshKey: number) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runsState, setRunsState] = useState<LoadState>('loading');
  const [allBatches, setAllBatches] = useState<PayrollPaymentBatch[] | null>(null);
  const [results, setResults] = useState<PayrollValidationResult[] | null>(null);
  const [overrideCount, setOverrideCount] = useState(0);
  const [approvals, setApprovals] = useState<PayrollApproval[] | null>(null);
  const [prechecks, setPrechecks] = useState<Prechecks | null>(null);
  const [tick, setTick] = useState(0);
  const seq = useRef(0);

  const reload = useCallback(() => setTick(n => n + 1), []);

  // Runs, and the payment batches that say which locked runs have a bank file.
  useEffect(() => {
    const mine = ++seq.current;
    Promise.allSettled([payrollApi.listAllRuns(), payrollApi.listPaymentBatches()]).then(([r, b]) => {
      if (seq.current !== mine) return;
      if (r.status === 'fulfilled') { setRuns(r.value); setRunsState('ready'); } else { setRuns([]); setRunsState('failed'); }
      setAllBatches(b.status === 'fulfilled' && Array.isArray(b.value) ? b.value : null);
    });
  }, [refreshKey, tick]);

  const run = runs.find(r => r.id === runId) ?? null;

  // Per-run detail. Validation results, overrides and approvals only exist once the run is processed.
  const status = run?.status ?? null;
  useEffect(() => {
    setResults(null); setApprovals(null); setOverrideCount(0);
    if (!runId || !status || status === 'Draft' || status === 'Voided') return;
    let live = true;
    Promise.allSettled([
      payrollApi.listValidationResults(runId),
      payrollApi.runValidationOverrides(runId),
      payrollApi.runApprovals(runId),
    ]).then(([v, o, a]) => {
      if (!live) return;
      setResults(v.status === 'fulfilled' && Array.isArray(v.value) ? v.value : null);
      setOverrideCount(o.status === 'fulfilled' && Array.isArray(o.value?.overrides) ? o.value.overrides.length : 0);
      setApprovals(a.status === 'fulfilled' && Array.isArray(a.value) ? a.value : null);
    });
    return () => { live = false; };
  }, [runId, status, refreshKey, tick]);

  // Pre-checks only matter while the run is a Draft.
  const companyId = run?.companyId ?? null;
  const year = run?.year;
  const month = run?.month;
  useEffect(() => {
    setPrechecks(null);
    if (!runId || status !== 'Draft') return;
    let live = true;
    Promise.allSettled([
      payrollApi.attendanceReconciliation(runId),
      payrollApi.getReadiness({ companyId: companyId ?? undefined, year, month }),
    ]).then(([att, ready]) => {
      if (!live) return;
      const pre = ready.status === 'fulfilled' ? (ready.value as PayrollReadinessWithPrerequisites).paymentPrerequisites ?? null : null;
      setPrechecks({
        staleAttendanceDays: att.status === 'fulfilled' && att.value ? (att.value.consumesAttendance ? (att.value.stale?.dayCount ?? 0) : 0) : null,
        employeesBlockedFromPayment: pre && typeof pre.blockedEmployees === 'number' ? pre.blockedEmployees : null,
        companyBlockers: pre ? (pre.companyBlocking?.length ?? 0) : null,
      });
    });
    return () => { live = false; };
  }, [runId, status, companyId, year, month, refreshKey, tick]);

  const openErrors = status && status !== 'Draft' && results
    ? results.filter(r => r.severity === 'Error' && !r.isResolved).length
    : (status === 'Draft' ? 0 : null);

  return { runs, runsState, run, allBatches, results, openErrors, overrideCount, approvals, prechecks, reload };
}
