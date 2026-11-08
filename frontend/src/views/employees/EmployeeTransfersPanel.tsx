'use client';

import { useCallback, useEffect, useState } from 'react';
import { employeesApi } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import type { EmployeeTransferView } from '../../lib/transferSteps';
import { requestFailureReason } from '../../lib/requestFailure';
import { TransferDecisionList, TransferNoticeLine, useTransferDecisions } from '../../components/people/TransferDecisionList';

/**
 * Employment, Transfers: every transfer for this person with the step it waits at and who holds it, so the requester can
 * see where it stands; whoever may decide the current step (a named manager, HR) approves it here too.
 */
export function EmployeeTransfersPanel({ employeeId, onEmployeeChanged }: { employeeId: number; onEmployeeChanged?(): void }) {
  const { t } = useLocale();
  const [rows, setRows] = useState<EmployeeTransferView[] | null>(null);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    try { setRows(await employeesApi.transfersFor(employeeId)); setError(''); } catch (err) { setError(t(requestFailureReason(err))); setRows(prev => prev ?? []); }
  }, [employeeId, t]);
  useEffect(() => { void load(); }, [load]);
  const reload = useCallback(async () => { await load(); onEmployeeChanged?.(); }, [load, onEmployeeChanged]);
  const { decide, busyId, notice, dialog } = useTransferDecisions(reload);

  return (
    <section className="surface space-y-4 p-5" data-testid="employee-transfers">
      <h2 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Employment transfers')}</h2>
      <TransferNoticeLine notice={notice} />
      {error && <p role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{error}</p>}
      {rows === null ? <p aria-busy="true" className="text-sm text-slate-500">{t('Loading…')}</p>
        : rows.length === 0 ? <p className="text-sm text-slate-500">{t('No transfer requests are recorded.')}</p>
        : <TransferDecisionList rows={rows} busyId={busyId} onDecide={view => void decide(view)} />}
      {dialog}
    </section>
  );
}
