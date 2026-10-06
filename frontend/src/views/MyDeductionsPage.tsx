'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { deductionsApi, type EmployeeDeductions } from '../api/deductions';
import { DeductionsHistory } from '../components/deductions/DeductionsHistory';
import { ReleaseAGate } from '../components/releaseA/ReleaseAGate';
import { useLocale } from '../contexts/LocaleContext';

const MONTHS = 6;

/**
 * My deductions / خصوماتي (Release A slice R3): every deduction from the employee's own finalised payslips, why it is made,
 * whether it counts toward the 50% limit, and what is left to repay. The API resolves the employee from the session,
 * never from the page.
 */
export function MyDeductionsPage() {
  return (
    <ReleaseAGate>
      <MyDeductionsBody />
    </ReleaseAGate>
  );
}

function MyDeductionsBody() {
  const { t } = useLocale();
  const [data, setData] = useState<EmployeeDeductions | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await deductionsApi.mine(MONTHS));
    } catch (e) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      setError(status === 403 ? t('Your account is not linked to an employee record. Ask HR to link it.') : t('Could not load your deductions.'));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <div className="space-y-4" data-testid="my-deductions">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('My deductions')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Every deduction from your pay, why it is made, and what is left to repay.')}</p>
      </div>
      {loading ? (
        <div className="space-y-3" aria-busy="true" aria-label={t('Loading your deductions')}>
          {[0, 1].map((i) => <div key={i} className="h-28 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />)}
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" aria-hidden />
          <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" aria-hidden /> {t('Retry')}
          </button>
        </div>
      ) : data ? (
        <DeductionsHistory data={data} audience="employee" months={MONTHS} />
      ) : null}
    </div>
  );
}
