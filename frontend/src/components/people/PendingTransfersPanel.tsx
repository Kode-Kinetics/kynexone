'use client';

import { useCallback, useEffect, useState } from 'react';
import { employeesApi } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import type { EmployeeTransferView } from '../../lib/transferSteps';
import { TransferDecisionList, TransferNoticeLine, useTransferDecisions } from './TransferDecisionList';

/**
 * Approval Center: the employee transfers waiting on the signed-in user. A transfer is not an approval request, so it is
 * not in the queue below; this is where its deciders (current manager, new manager, HR) find it. Hidden when nothing waits
 * and for anyone who holds neither approve permission (the server would refuse the list).
 */
export function PendingTransfersPanel() {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const allowed = hasPermission('manager.approve') || hasPermission('employees.approve');
  const [rows, setRows] = useState<EmployeeTransferView[]>([]);

  const load = useCallback(async () => {
    try { setRows(await employeesApi.pendingTransfers()); } catch { setRows([]); }
  }, []);
  useEffect(() => { if (allowed) void load(); }, [allowed, load]);
  const { decide, busyId, notice, dialog } = useTransferDecisions(load);

  if (!allowed || (rows.length === 0 && !notice)) return null;
  return (
    <section aria-label={t('Transfers waiting for you')} className="surface space-y-3 p-4">
      <div>
        <h2 className="text-lg font-semibold text-slate-950 dark:text-white">{t('Transfers waiting for you')}</h2>
        <p className="text-sm text-slate-500 dark:text-slate-400">{t('Approve the step you hold. The transfer then moves to the next person, and is applied after HR approves it.')}</p>
      </div>
      <TransferNoticeLine notice={notice} />
      {rows.length > 0 && <TransferDecisionList rows={rows} busyId={busyId} onDecide={view => void decide(view)} />}
      {dialog}
    </section>
  );
}
