'use client';

import { useCallback, useEffect, useState } from 'react';
import { apiErrorReason } from '../../api/client';
import { financeErpApi, type GlPeriod } from '../../api/financeErp';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';

/**
 * GL period close / reopen for one scope (a company, or the whole group when companyId is null).
 *
 * A closed month blocks new GL postings dated into it (payroll lock/settle/remit/void, loan, advance and bonus
 * posts). Closing needs no reason; reopening does, because it re-allows postings into a month that may already
 * be in the ERP — the server audits both and its refusal is shown word for word.
 */
export function GlPeriodsPanel({ companyId = null }: { companyId?: string | null }) {
  const t = useT();
  const f = useFormat();
  const [periods, setPeriods] = useState<GlPeriod[] | null>(null);
  const [loadError, setLoadError] = useState('');
  const [newPeriod, setNewPeriod] = useState('');
  const [closeReason, setCloseReason] = useState('');
  const [reopening, setReopening] = useState<string | null>(null);
  const [reopenReason, setReopenReason] = useState('');
  const [busy, setBusy] = useState('');
  const [actionError, setActionError] = useState('');

  const load = useCallback(() => {
    setPeriods(null);
    setLoadError('');
    financeErpApi.listPeriods(companyId)
      .then(setPeriods)
      .catch(err => setLoadError(apiErrorReason(err, t('Accounting periods could not be loaded.'))));
  }, [companyId, t]);
  useEffect(() => { load(); }, [load]);

  const act = async (key: string, call: () => Promise<unknown>, after: () => void) => {
    setBusy(key);
    setActionError('');
    try { await call(); after(); load(); }
    catch (err) { setActionError(apiErrorReason(err, t('The period could not be updated.'))); }
    finally { setBusy(''); }
  };

  const closed = (p: GlPeriod) => p.status === 'Closed';

  return (
    <section className="surface space-y-3 p-4" aria-labelledby="gl-periods-heading">
      <div>
        <h3 id="gl-periods-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Accounting periods')}</h3>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Close a month once it is posted in your ERP. A closed month accepts no new payroll, loan, advance or bonus postings until it is reopened.')}
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Month to close')}
          <input type="month" className="input mt-1" value={newPeriod} onChange={e => setNewPeriod(e.target.value)} />
        </label>
        <label className="flex-1 text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Note (optional)')}
          <input className="input mt-1 w-full" value={closeReason} maxLength={500} onChange={e => setCloseReason(e.target.value)} />
        </label>
        <button type="button" className="btn-primary text-xs" disabled={!newPeriod || !!busy}
          onClick={() => act('close-new', () => financeErpApi.closePeriod(newPeriod, companyId, closeReason), () => { setNewPeriod(''); setCloseReason(''); })}>
          {busy === 'close-new' ? t('Closing…') : t('Close month')}
        </button>
      </div>

      {actionError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="gl-period-error">{actionError}</p>}

      {loadError ? (
        <div role="alert" className="text-xs text-rose-600 dark:text-rose-400">
          {loadError} <button type="button" className="underline" onClick={load}>{t('Try again')}</button>
        </div>
      ) : periods === null ? (
        <p className="text-xs text-slate-400">{t('Loading accounting periods…')}</p>
      ) : periods.length === 0 ? (
        <p className="text-xs text-slate-500">{t('No month has been closed yet. Every month is open for posting.')}</p>
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-white/5" data-testid="gl-periods">
          {periods.map(p => {
            const key = `${p.period}-${p.companyId ?? 'group'}`;
            const inherited = companyId !== null && p.companyId === null;
            return (
              <li key={key} className="py-2 text-xs">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono font-semibold">{p.period}</span>
                  <span className={`rounded-full px-2 py-0.5 font-semibold ${closed(p) ? 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200' : 'bg-emerald-100 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-300'}`}>
                    {closed(p) ? t('Closed') : t('Open')}
                  </span>
                  {inherited && <span className="text-slate-500">{t('Closed for the whole group')}</span>}
                  {closed(p) && p.closedAtUtc && (
                    <span className="text-slate-500">{t('Closed by {name} on {date}', { name: p.closedByName ?? '—', date: f.dateTime(p.closedAtUtc) })}</span>
                  )}
                  {!closed(p) && p.reopenReason && (
                    <span className="text-slate-500">{t('Reopened by {name}: {reason}', { name: p.reopenedByName ?? '—', reason: p.reopenReason })}</span>
                  )}
                  <span className="ms-auto flex gap-2">
                    {closed(p) && !inherited && (
                      <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={!!busy}
                        onClick={() => { setReopening(reopening === key ? null : key); setReopenReason(''); }}>
                        {t('Reopen month')}
                      </button>
                    )}
                    {!closed(p) && (
                      <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={!!busy}
                        onClick={() => act(`close-${key}`, () => financeErpApi.closePeriod(p.period, companyId), () => undefined)}>
                        {busy === `close-${key}` ? t('Closing…') : t('Close month')}
                      </button>
                    )}
                  </span>
                </div>
                {reopening === key && (
                  <div className="mt-2 flex flex-wrap items-end gap-2">
                    <label className="flex-1">
                      {t('Why reopen this month? (required)')}
                      <input className="input mt-1 w-full" value={reopenReason} maxLength={500} onChange={e => setReopenReason(e.target.value)} />
                    </label>
                    <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!reopenReason.trim() || !!busy}
                      onClick={() => act(`reopen-${key}`, () => financeErpApi.reopenPeriod(p.period, companyId, reopenReason), () => setReopening(null))}>
                      {busy === `reopen-${key}` ? t('Reopening…') : t('Confirm reopen')}
                    </button>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

export default GlPeriodsPanel;
