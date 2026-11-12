'use client';

import { AlertTriangle, CheckCircle2, Info } from 'lucide-react';
import type { PayrollGLJournal, PostedGlJournal } from '../../api/payroll';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { hasCostCentres, journalTitle } from '../../lib/glJournalView';
import { Badge } from '../gl/glUi';

/**
 * Payroll → GL Journal for a run that has POSTED. The ledger holds one journal per event (the accrual Lock wrote, the
 * net-pay settlement, each remittance, a void ...). They are shown separately, each balanced on its own; merging them
 * showed one account as both debit and credit and doubled the headline total. The headline is the accrual journal.
 */
export function PostedJournalsView({ journal }: { journal: PayrollGLJournal }) {
  const t = useT();
  const fmt = useFormat();
  const journals = journal.journals ?? [];
  const accrual = journals.find(j => j.kind === 'accrual') ?? null;
  const others = journals.filter(j => j !== accrual);

  return (
    <section data-testid="gl-posted-journals" className="space-y-4">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div className="surface p-4">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Period')}</p>
          <p className="text-lg font-semibold text-slate-900 dark:text-white">{journal.period}</p>
        </div>
        <div className="surface p-4" data-testid="gl-headline-debits">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Total debits')}</p>
          <p className="text-lg font-semibold tabular-nums text-sapphire dark:text-cyanAccent">{fmt.money(journal.totalDebits, journal.currency)}</p>
          <p className="text-caption text-slate-500 dark:text-slate-400">{t('Payroll accrual')}</p>
        </div>
        <div className="surface p-4" data-testid="gl-headline-credits">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Total credits')}</p>
          <p className="text-lg font-semibold tabular-nums text-sapphire dark:text-cyanAccent">{fmt.money(journal.totalCredits, journal.currency)}</p>
          <p className="text-caption text-slate-500 dark:text-slate-400">{t('Payroll accrual')}</p>
        </div>
      </div>

      {others.length > 0 && (
        <p role="note" className="flex items-start gap-2 rounded-lg bg-slate-50 px-2 py-2 sm:px-3 text-xs text-slate-700 dark:bg-white/5 dark:text-slate-300">
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span>{t('The totals above are the payroll accrual only. Settlement, remittance and void entries are separate journals, listed below, each balanced on its own.')}</span>
        </p>
      )}

      {accrual && <JournalBlock j={accrual} currency={journal.currency} />}

      {others.length > 0 && (
        <h3 className="pt-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Other journals posted for this run')}</h3>
      )}
      {others.map((j, i) => <JournalBlock key={`${j.eventType}-${i}`} j={j} currency={journal.currency} />)}
    </section>
  );
}

function JournalBlock({ j, currency }: { j: PostedGlJournal; currency: string }) {
  const t = useT();
  const fmt = useFormat();
  const amt = (n: number) => fmt.number(n, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const title = t(journalTitle(j));
  const showCostCentre = hasCostCentres(j.entries);
  return (
    <div data-testid={`gl-journal-${j.kind}${j.group ? `-${j.group}` : ''}`} className="surface overflow-hidden">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-4 py-3 dark:border-white/5">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{title}</p>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Entry date')}: <span className="tabular-nums">{fmt.date(j.entryDate, 'medium')}</span>
            {j.postedByName && <> · {t('Posted by')}: <bdi>{j.postedByName}</bdi></>}
          </p>
        </div>
        <div className="flex items-center gap-2">
          {j.isReversed && <Badge tone="amber">{t('Reversed')}</Badge>}
          {j.isBalanced
            ? <Badge tone="emerald"><CheckCircle2 className="h-3 w-3" aria-hidden="true" />{t('Balanced')}</Badge>
            : <Badge tone="rose"><AlertTriangle className="h-3 w-3" aria-hidden="true" />{t('Out of balance')}</Badge>}
        </div>
      </div>
      <div className="overflow-x-auto">
        <table aria-label={t('Journal lines for {title}', { title })} className={`w-full text-sm ${showCostCentre ? 'min-w-[440px]' : ''}`}>
          <thead>
            <tr className="border-b border-slate-100 dark:border-white/5">
              <th className="px-2 py-2 sm:px-3 text-start text-xs font-semibold uppercase text-fg-muted">{t('Account')}</th>
              {showCostCentre && <th className="px-2 py-2 sm:px-3 text-start text-xs font-semibold uppercase text-fg-muted">{t('Cost centre')}</th>}
              <th className="px-2 py-2 sm:px-3 text-end text-xs font-semibold uppercase text-fg-muted">{t('Debit')} ({currency})</th>
              <th className="px-2 py-2 sm:px-3 text-end text-xs font-semibold uppercase text-fg-muted">{t('Credit')} ({currency})</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {j.entries.map((e, i) => (
              <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-2 py-2 sm:px-3 text-slate-700 dark:text-slate-300">
                  <span className="me-2 font-mono text-xs text-slate-500"><bdi>{e.glAccount}</bdi></span>{e.glAccountName}
                </td>
                {showCostCentre && (
                  <td className="px-2 py-2 sm:px-3 text-slate-700 dark:text-slate-300">
                    {e.costCentreCode ? (
                      <>
                        <span><bdi>{e.costCentreCode}</bdi>{e.costCentreName ? <> — {e.costCentreName}</> : null}</span>
                        {e.costCentreErpCode && <span className="block text-caption text-slate-500 dark:text-slate-400">{t('ERP code')}: <bdi>{e.costCentreErpCode}</bdi></span>}
                      </>
                    ) : <span className="text-slate-400">—</span>}
                  </td>
                )}
                <td className="px-2 py-2 sm:px-3 text-end text-xs font-semibold whitespace-nowrap sm:text-sm tabular-nums text-sapphire dark:text-cyanAccent">{e.entryType === 'DR' ? amt(e.amount) : ''}</td>
                <td className="px-2 py-2 sm:px-3 text-end text-xs font-semibold whitespace-nowrap sm:text-sm tabular-nums text-emerald-600 dark:text-emerald-400">{e.entryType === 'CR' ? amt(e.amount) : ''}</td>
              </tr>
            ))}
            <tr className="border-t-2 border-slate-200 dark:border-white/10">
              <td colSpan={showCostCentre ? 2 : 1} className="px-2 py-2 sm:px-3 text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Total')}</td>
              <td className="px-2 py-2 sm:px-3 text-end text-sm font-semibold tabular-nums text-sapphire dark:text-cyanAccent">{amt(j.totalDebits)}</td>
              <td className="px-2 py-2 sm:px-3 text-end text-sm font-semibold tabular-nums text-emerald-600 dark:text-emerald-400">{amt(j.totalCredits)}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Posted without any cost-centre line: a fact about the posting, not about today's company setting. */
export function PostedWithoutCostCentresNote() {
  const t = useT();
  return (
    <div data-testid="gl-posted-without-cost-centres" role="note" className="flex flex-wrap items-center gap-2 rounded-lg bg-slate-50 px-2 py-2 sm:px-3 text-xs text-slate-700 dark:bg-white/5 dark:text-slate-300">
      <span>{t('This run was posted without cost centres.')}</span>
    </div>
  );
}
