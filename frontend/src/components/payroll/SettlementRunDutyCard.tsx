'use client';

import { useEffect, useState } from 'react';
import { CalendarClock } from 'lucide-react';
import { payrollApi } from '../../api/payroll';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { runDutySummary } from '../../lib/offboardingSettlement';

/**
 * G-006: on the Payroll dashboard, for the people who create payroll runs (payroll.write): how many approved final settlements
 * still need an off-cycle run and when the first is due, linking to the Final Settlement tab. Silent when nothing is waiting,
 * and when the list cannot be read (the tab itself says why).
 */
export function SettlementRunDutyCard({ onOpen }: { onOpen: () => void }) {
  const t = useT();
  const fmt = useFormat();
  const { hasPermission } = useAuth();
  const canAct = hasPermission('payroll.write');
  const [summary, setSummary] = useState<{ count: number; nextDue: string | null } | null>(null);

  useEffect(() => {
    if (!canAct) return;
    let cancelled = false;
    payrollApi.listFinalSettlements().then(r => { if (!cancelled) setSummary(runDutySummary(r.rows)); }).catch(() => {});
    return () => { cancelled = true; };
  }, [canAct]);

  if (!canAct || !summary || summary.count === 0) return null;
  return (
    <div className="surface flex flex-wrap items-center justify-between gap-3 border-s-4 border-s-amber-500 p-3.5" data-testid="settlement-run-duty-card">
      <p className="flex items-center gap-2 text-sm font-medium text-slate-800 dark:text-slate-100">
        <CalendarClock className="h-4 w-4 text-amber-600" aria-hidden="true" />
        {t('{count, plural, one {# settlement needs an off-cycle run. Next due {date}.} other {# settlements need an off-cycle run. Next due {date}.}}', { count: summary.count, date: fmt.date(summary.nextDue ?? '') })}
      </p>
      <button type="button" className="btn-primary" onClick={onOpen}>{t('Open Final Settlement')}</button>
    </div>
  );
}
