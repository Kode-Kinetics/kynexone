'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { notifyApiError } from '../api/client';
import { renewalsApi, type HoldReason, type RenewalCaseItem, type RenewalRadar } from '../api/renewals';
import { Modal } from '../components/Modal';
import { ReleaseAGate } from '../components/releaseA/ReleaseAGate';
import { ChainDrawer } from '../components/renewals/ChainDrawer';
import { Radar, SelectionBar } from '../components/renewals/Radar';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { fill, filterItems, holdReasonKeys, type RadarFilter } from '../lib/renewalRadar';

const WINDOWS = [30, 60, 90, 120];
const HOLD_REASONS: HoldReason[] = ['Resignation', 'UnpaidLeave', 'Abroad', 'Transfer', 'LabourDispute'];

/**
 * Contract renewals (Release A slice R4): every fixed-term contract ending in the window, bucketed by how soon it ends,
 * the exceptions that need someone today, and for each review its Next line — what is due, by when, and what happens
 * if it is missed. Rows that can renew on current terms can be selected for R5's batch fast lane. ?contract={id}
 * opens that contract's history (the Compliance contracts register links here).
 */
export function ContractRenewalsPage({ batchActions }: { batchActions?: (caseIds: string[], done: () => void) => React.ReactNode } = {}) {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('contracts.renewal.manage');
  const [days, setDays] = useState(120);
  const [radar, setRadar] = useState<RenewalRadar | null>(null);
  const [failed, setFailed] = useState(false);
  const [filter, setFilter] = useState<RadarFilter>({ kind: 'all' });
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [chainFor, setChainFor] = useState<string | null>(null);
  const [busyCaseId, setBusyCaseId] = useState<string | null>(null);
  const [opening, setOpening] = useState(false);
  const [holdFor, setHoldFor] = useState<RenewalCaseItem | null>(null);
  const [holdReason, setHoldReason] = useState<HoldReason>('Resignation');
  const [cancelFor, setCancelFor] = useState<RenewalCaseItem | null>(null);
  const [cancelReason, setCancelReason] = useState('');

  const load = useCallback(async () => {
    setFailed(false);
    try {
      const next = await renewalsApi.radar({ days });
      setRadar(next);
      // A selection only ever holds rows that are still in view and still eligible.
      setSelected((s) => new Set([...s].filter((id) => next.items.some((i) => i.caseId === id && i.fastLaneEligible))));
      setFilter({ kind: 'all' });
    } catch {
      setFailed(true);
    }
  }, [days]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    const contract = new URLSearchParams(window.location.search).get('contract');
    if (contract) setChainFor(contract);
  }, []);

  const rows = useMemo(() => (radar ? filterItems(radar, filter) : []), [radar, filter]);

  const act = async (item: RenewalCaseItem, run: () => Promise<unknown>, failure: string) => {
    setBusyCaseId(item.caseId);
    try {
      await run();
      await load();
    } catch (err) {
      notifyApiError(err, t(failure));
    } finally {
      setBusyCaseId(null);
    }
  };

  const openNow = async () => {
    setOpening(true);
    try {
      await renewalsApi.openNow();
      await load();
    } catch (err) {
      notifyApiError(err, t('Reviews could not be opened. Please try again.'));
    } finally {
      setOpening(false);
    }
  };

  return (
    <ReleaseAGate>
      <div className="space-y-5">
        <header className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('Contract renewals')}</h1>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {t('Every contract ending in the next 120 days, what is due next, and what happens if it is missed.')}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <label className="text-xs text-slate-600 dark:text-slate-300">
              {t('Show contracts ending within')}{' '}
              <select value={days} onChange={(e) => setDays(Number(e.target.value))}
                className="rounded-lg border border-slate-200 bg-white px-2 py-1 text-xs dark:border-white/10 dark:bg-white/5">
                {WINDOWS.map((w) => <option key={w} value={w}>{fill(t('{n} days'), { n: w })}</option>)}
              </select>
            </label>
            {canManage && (
              <button type="button" disabled={opening} onClick={() => void openNow()}
                className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
                title={t('Reviews open automatically every day. This opens any that are due right now.')}>
                {opening ? t('Opening…') : t('Open due reviews now')}
              </button>
            )}
          </div>
        </header>

        {failed ? (
          <div className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-200">
            {t('Contract renewals could not be loaded.')}{' '}
            <button type="button" onClick={() => void load()} className="font-semibold underline">{t('Try again')}</button>
          </div>
        ) : !radar ? (
          <div className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
        ) : (
          <>
            {filter.kind !== 'all' && (
              <p className="text-xs text-slate-600 dark:text-slate-300">
                {fill(t('Showing {n} of {total} reviews.'), { n: rows.length, total: radar.items.length })}{' '}
                <button type="button" onClick={() => setFilter({ kind: 'all' })} className="font-semibold text-sapphire underline">{t('Show all')}</button>
              </p>
            )}
            <Radar radar={radar} filter={filter} onFilter={setFilter} rows={rows} selected={selected} onSelected={setSelected}
              canManage={canManage} onOpenChain={setChainFor} busyCaseId={busyCaseId}
              onHold={(item) => { setHoldReason('Resignation'); setHoldFor(item); }}
              onRelease={(item) => void act(item, () => renewalsApi.release(item.caseId), 'The hold could not be released.')}
              onCancel={(item) => { setCancelReason(''); setCancelFor(item); }} />
            <SelectionBar count={selected.size} onClear={() => setSelected(new Set())}>
              {batchActions?.([...selected], () => { setSelected(new Set()); void load(); })}
            </SelectionBar>
          </>
        )}
      </div>

      <ChainDrawer contractId={chainFor} canManage={canManage} onClose={() => setChainFor(null)} onChanged={() => void load()} />

      <Modal isOpen={holdFor != null} onClose={() => setHoldFor(null)} title={t('Put the review on hold')} size="sm"
        footer={(
          <button type="button" className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white"
            onClick={() => { const item = holdFor!; setHoldFor(null); void act(item, () => renewalsApi.hold(item.caseId, holdReason), 'The review could not be put on hold.'); }}>
            {t('Put on hold')}
          </button>
        )}>
        <label className="block text-xs text-slate-600 dark:text-slate-300">{t('Why is it on hold?')}
          <select value={holdReason} onChange={(e) => setHoldReason(e.target.value as HoldReason)}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-sm dark:border-white/10 dark:bg-white/5">
            {HOLD_REASONS.map((r) => <option key={r} value={r}>{t(holdReasonKeys[r])}</option>)}
          </select>
        </label>
        <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('Deadlines keep running while a review is on hold.')}</p>
      </Modal>

      <Modal isOpen={cancelFor != null} onClose={() => setCancelFor(null)} title={t('Cancel the renewal review')} size="sm"
        footer={(
          <button type="button" disabled={!cancelReason.trim()} className="rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
            onClick={() => { const item = cancelFor!; setCancelFor(null); void act(item, () => renewalsApi.cancel(item.caseId, cancelReason.trim()), 'The review could not be cancelled.'); }}>
            {t('Cancel review')}
          </button>
        )}>
        <label className="block text-xs text-slate-600 dark:text-slate-300">{t('Why is the review cancelled?')}
          <textarea value={cancelReason} onChange={(e) => setCancelReason(e.target.value)} rows={3} maxLength={500}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-sm dark:border-white/10 dark:bg-white/5" />
        </label>
        <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('A cancelled review is closed for good; the contract itself does not change.')}</p>
      </Modal>
    </ReleaseAGate>
  );
}
