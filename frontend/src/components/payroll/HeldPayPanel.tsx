'use client';

import { useCallback, useEffect, useId, useState } from 'react';
import { payrollApi, type PayrollHeldPayItem, type PayrollHeldPayView } from '../../api/payroll';
import { useAuth } from '../../contexts/AuthContext';
import { useT } from '../../hooks/useT';
import { serverMessage } from '../gl/glUi';

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';

/**
 * Pay held for suspended employees (server HeldPay). The run computes a suspended employee's pay and holds it instead of
 * paying it; the amount stays listed here, on its own run and on every later run of the company, until Finance pays it in
 * the next open run or forfeits it with a reason. The server refuses a decision by the run's preparer and records both.
 */
export function HeldPayPanel({ runId, amount }: { runId: string; amount: (n: number) => string }) {
  const t = useT();
  const { hasPermission } = useAuth();
  const canDecide = hasPermission('payroll.approve');
  const [view, setView] = useState<PayrollHeldPayView | null>(null);
  const [loadError, setLoadError] = useState('');
  const [forfeiting, setForfeiting] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const reasonId = useId();

  const load = useCallback(async () => {
    if (!runId) { setView(null); return; }
    try {
      setView(await payrollApi.runHeldPay(runId));
      setLoadError('');
    } catch (e) {
      setView(null);
      setLoadError(serverMessage(e, t) || t('Held pay could not be loaded.'));
    }
  }, [runId, t]);
  useEffect(() => { void load(); }, [load]);

  const decide = async (item: PayrollHeldPayItem, action: 'pay' | 'forfeit') => {
    setBusy(item.holdId); setNotice(null);
    try {
      await payrollApi.decideHeldPay(item.holdId, action, action === 'forfeit' ? reason.trim() : '');
      setNotice({ kind: 'ok', text: action === 'pay'
        ? t('The held pay was added to the next open run as an earning.')
        : t('The held pay was forfeited. The reason is in the audit trail.') });
      setForfeiting(null); setReason('');
      await load();
    } catch (e) {
      setNotice({ kind: 'error', text: serverMessage(e, t) || t('The action was refused.') });
    } finally {
      setBusy(null);
    }
  };

  if (loadError) return <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{loadError}</p>;
  if (!view || view.items.length === 0) return null;

  const statusText = (item: PayrollHeldPayItem) =>
    item.status === 'Released'
      ? (item.paidInPeriod ? t('Released — paid in {period}', { period: item.paidInPeriod }) : t('Released'))
      : item.status === 'Forfeited' ? t('Forfeited') : t('Held — awaiting a decision');

  return (
    <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 dark:border-amber-500/30 dark:bg-amber-500/10" data-testid="payroll-held-pay">
      <p className="text-xs font-semibold text-amber-900 dark:text-amber-200">
        {t('{count} held amount(s) for suspended employees', { count: view.items.length })}
      </p>
      <p className="mt-0.5 text-xs leading-5 text-amber-900 dark:text-amber-200">
        {t('A suspended employee’s pay is worked out and held, not paid and not lost. Finance pays it in the next open run or forfeits it with a reason.')}
      </p>
      <ul className="mt-2 space-y-2">
        {view.items.map(item => (
          <li key={item.holdId} className="text-xs text-slate-800 dark:text-slate-200" data-testid="held-pay-row">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span>
                <bdi className="font-semibold">{`${item.employeeCode} — ${item.employeeName}`}</bdi>
                <bdi className="ms-2 tabular-nums">{amount(item.amount)}</bdi>
                <span className="ms-2 opacity-80">{t('Held in {period}', { period: item.heldPeriod })}</span>
                <span className="ms-2 opacity-80">{statusText(item)}</span>
              </span>
              {canDecide && item.status === 'Held' && (
                <span className="flex gap-2">
                  <button type="button" data-testid="held-pay-release" disabled={busy != null} onClick={() => void decide(item, 'pay')}
                    className="inline-flex items-center rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50">
                    {t('Pay in next run')}
                  </button>
                  <button type="button" data-testid="held-pay-forfeit" disabled={busy != null}
                    onClick={() => { setForfeiting(forfeiting === item.holdId ? null : item.holdId); setReason(''); }}
                    className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
                    {t('Forfeit')}
                  </button>
                </span>
              )}
            </div>
            {forfeiting === item.holdId && (
              <div className="mt-2 space-y-1">
                <label htmlFor={reasonId} className="block font-semibold">{t('Why is this pay forfeited? (required)')}</label>
                <textarea id={reasonId} className={input} rows={2} value={reason} onChange={e => setReason(e.target.value)}
                  placeholder={t('Recorded against your identity on the audit trail.')} />
                <button type="button" data-testid="held-pay-forfeit-confirm" disabled={busy != null || reason.trim().length < 5}
                  onClick={() => void decide(item, 'forfeit')}
                  className="inline-flex items-center rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-rose-700 disabled:opacity-50">
                  {t('Forfeit held pay')}
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>
      {notice && (
        <p role={notice.kind === 'error' ? 'alert' : 'status'}
          className={`mt-2 text-xs ${notice.kind === 'error' ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-700 dark:text-emerald-300'}`}>
          {notice.text}
        </p>
      )}
    </div>
  );
}
