'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiErrorReason } from '../../api/client';
import { financeErpApi, type GlPeriod } from '../../api/financeErp';
import { useCompany } from '../../contexts/CompanyContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';

/**
 * GL period close / reopen for one scope (a company, or the group view when companyId is null).
 *
 * A closed month blocks new GL postings dated into it (payroll lock/settle/remit/void, loan, advance and bonus
 * posts). A period row belongs to ONE scope: a company, or the whole group (companyId null). Every action on an
 * existing row is therefore sent with THAT ROW's own company — never the panel's — so in the group view closing a
 * company's reopened month closes that company only, and reopening a company's month reopens it. Closing a month
 * for every company at once is a separate, explicit action that names the companies it will block, because it
 * stops payroll, loan, advance and bonus postings for all of them.
 *
 * Closing needs no reason; reopening does. The server audits both and its refusal is shown word for word.
 */
export function GlPeriodsPanel({ companyId = null }: { companyId?: string | null }) {
  const t = useT();
  const f = useFormat();
  const { companies } = useCompany();
  const groupView = companyId === null;
  const [periods, setPeriods] = useState<GlPeriod[] | null>(null);
  const [loadError, setLoadError] = useState('');
  const [newPeriod, setNewPeriod] = useState('');
  // Group view: which company the new close is for ('' = every company, which asks for confirmation first).
  const [newTarget, setNewTarget] = useState('');
  const [closeReason, setCloseReason] = useState('');
  const [reopening, setReopening] = useState<string | null>(null);
  const [reopenReason, setReopenReason] = useState('');
  const [confirmingGroupClose, setConfirmingGroupClose] = useState<{ key: string; period: string; reason?: string } | null>(null);
  const [busy, setBusy] = useState('');
  const [actionError, setActionError] = useState('');

  const companyName = useMemo(() => {
    const names = new Map(companies.map(c => [c.id, c.name]));
    return (id: string | null) => (id === null ? t('All companies') : names.get(id) ?? t('Company {id}', { id: id.slice(0, 8) }));
  }, [companies, t]);
  const companyList = companies.map(c => c.name).join(', ');

  const load = useCallback(() => {
    setPeriods(null);
    setLoadError('');
    financeErpApi.listPeriods(companyId)
      .then(setPeriods)
      .catch(err => setLoadError(apiErrorReason(err, t('Accounting periods could not be loaded.'))));
  }, [companyId, t]);
  useEffect(() => { load(); }, [load]);
  useEffect(() => { setNewTarget(''); setConfirmingGroupClose(null); }, [companyId]);

  const act = async (key: string, call: () => Promise<unknown>, after: () => void) => {
    setBusy(key);
    setActionError('');
    try { await call(); after(); load(); }
    catch (err) { setActionError(apiErrorReason(err, t('The period could not be updated.'))); }
    finally { setBusy(''); }
  };

  const closed = (p: GlPeriod) => p.status === 'Closed';
  // The scope a NEW close is written to: the panel's company, or (group view) the chosen company / every company.
  const newScope = groupView ? (newTarget || null) : companyId;

  const closeGroupWide = (key: string, period: string, reason?: string) =>
    act(key, () => financeErpApi.closePeriod(period, null, reason), () => {
      setConfirmingGroupClose(null);
      // Clear the form only if it still shows the month just closed: a user who has already typed the
      // next month while the request was in flight must not have it wiped.
      if (key === 'close-new') {
        setNewPeriod(cur => (cur === period ? '' : cur));
        setCloseReason(cur => (cur === (reason ?? '') ? '' : cur));
      }
    });

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
        {groupView && (
          <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Company')}
            <select className="select mt-1" value={newTarget} onChange={e => { setNewTarget(e.target.value); setConfirmingGroupClose(null); }}
              data-testid="gl-period-close-target">
              <option value="">{t('All companies')}</option>
              {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
        )}
        <label className="flex-1 text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Note (optional)')}
          <input className="input mt-1 w-full" value={closeReason} maxLength={500} onChange={e => setCloseReason(e.target.value)} />
        </label>
        <button type="button" className="btn-primary text-xs" disabled={!newPeriod || !!busy}
          onClick={() => newScope === null
            ? setConfirmingGroupClose({ key: 'close-new', period: newPeriod, reason: closeReason })
            : act('close-new', () => financeErpApi.closePeriod(newPeriod, newScope, closeReason), () => {
                const sent = newPeriod, sentReason = closeReason;
                setNewPeriod(cur => (cur === sent ? '' : cur));
                setCloseReason(cur => (cur === sentReason ? '' : cur));
              })}>
          {busy === 'close-new' ? t('Closing…') : newScope === null ? t('Close for all companies') : t('Close month')}
        </button>
      </div>

      {confirmingGroupClose && (
        <div role="alertdialog" aria-labelledby="gl-group-close-title" className="space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-xs text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200"
          data-testid="gl-period-group-close-confirm">
          <p id="gl-group-close-title" className="font-semibold">
            {t('Close {period} for all companies?', { period: confirmingGroupClose.period })}
          </p>
          <p>
            {companyList
              ? t('No payroll, loan, advance or bonus posting dated in {period} will be accepted for any of these companies until the month is reopened: {companies}.',
                { period: confirmingGroupClose.period, companies: companyList })
              : t('No payroll, loan, advance or bonus posting dated in {period} will be accepted for any company in the group until the month is reopened.',
                { period: confirmingGroupClose.period })}
          </p>
          <div className="flex gap-2">
            <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!!busy}
              onClick={() => closeGroupWide(confirmingGroupClose.key, confirmingGroupClose.period, confirmingGroupClose.reason)}>
              {busy === confirmingGroupClose.key ? t('Closing…') : t('Close for all companies')}
            </button>
            <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!!busy} onClick={() => setConfirmingGroupClose(null)}>
              {t('Cancel')}
            </button>
          </div>
        </div>
      )}

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
            // A company's own view lists the group-wide rows that also bind it; those are the group's decision.
            const inherited = !groupView && p.companyId === null;
            const rowIsGroupWide = p.companyId === null;
            return (
              <li key={key} className="py-2 text-xs" data-testid={`gl-period-${key}`}>
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono font-semibold">{p.period}</span>
                  {groupView && (
                    <span className="rounded bg-slate-100 px-1.5 py-0.5 font-medium text-slate-700 dark:bg-white/10 dark:text-slate-200" data-testid="gl-period-company">
                      {companyName(p.companyId)}
                    </span>
                  )}
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
                  {!inherited && (
                    <span className="ms-auto flex gap-2">
                      {closed(p) && (
                        <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={!!busy}
                          onClick={() => { setReopening(reopening === key ? null : key); setReopenReason(''); }}>
                          {rowIsGroupWide ? t('Reopen for all companies') : t('Reopen month')}
                        </button>
                      )}
                      {!closed(p) && (
                        <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={!!busy}
                          onClick={() => rowIsGroupWide
                            ? setConfirmingGroupClose({ key: `close-${key}`, period: p.period })
                            : act(`close-${key}`, () => financeErpApi.closePeriod(p.period, p.companyId), () => undefined)}>
                          {busy === `close-${key}` ? t('Closing…') : rowIsGroupWide ? t('Close for all companies') : t('Close month')}
                        </button>
                      )}
                    </span>
                  )}
                </div>
                {reopening === key && (
                  <div className="mt-2 flex flex-wrap items-end gap-2">
                    <label className="flex-1">
                      {t('Why reopen this month? (required)')}
                      <input className="input mt-1 w-full" value={reopenReason} maxLength={500} onChange={e => setReopenReason(e.target.value)} />
                    </label>
                    <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!reopenReason.trim() || !!busy}
                      onClick={() => act(`reopen-${key}`, () => financeErpApi.reopenPeriod(p.period, p.companyId, reopenReason), () => setReopening(null))}>
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
