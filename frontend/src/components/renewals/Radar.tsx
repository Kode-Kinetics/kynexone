'use client';

import { Fragment, type ReactNode } from 'react';
import type { RenewalCaseItem, RenewalRadar, RenewalUnopened } from '../../api/renewals';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  actionKeys, badgeText, blockText, closedStateKeys, companyName, fill, formatDay, nextLine, personName, stageKeys, toggleAll, toggleSelection,
  unopenedReasonKeys,
  type RadarFilter,
} from '../../lib/renewalRadar';
import { StatusChip } from '../StatusChip';

/** One tile: a count, what it counts (always visible), and a click that lists exactly those records. */
function Tile({ label, definition, count, active, tone, onClick }: {
  label: string; definition: string; count: number; active: boolean; tone: 'neutral' | 'warn'; onClick: () => void;
}) {
  const ring = active ? 'ring-2 ring-sapphire' : 'ring-1 ring-slate-200 dark:ring-white/10';
  const number = tone === 'warn' && count > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-slate-900 dark:text-white';
  return (
    <button type="button" onClick={onClick} aria-pressed={active}
      className={`surface rounded-xl p-3 text-start transition-shadow hover:shadow-soft ${ring}`}>
      <span className="block text-xs font-semibold text-slate-600 dark:text-slate-300">{label}</span>
      <span className={`mt-1 block text-2xl font-bold tracking-tight ${number}`}>{count}</span>
      <span className="mt-1 block text-[11px] leading-snug text-slate-500 dark:text-slate-400">{definition}</span>
    </button>
  );
}

const badgeTone = (code: string): 'rose' | 'amber' | 'blue' | 'slate' =>
  code === 'Art55Threshold' || code === 'NoticeDatePassed' || code === 'QiwaOverdue' || code === 'ExpiredNoOutcome' || code === 'ExpiredHoldoverPending' ? 'rose'
    : code === 'ChainUnconfirmed' || code === 'OnHold' || code === 'OffboardingOpen' ? 'amber'
      : code === 'Art55Meter' ? 'blue' : 'slate';

export interface RadarProps {
  radar: RenewalRadar;
  filter: RadarFilter;
  onFilter: (filter: RadarFilter) => void;
  rows: RenewalCaseItem[];
  selected: ReadonlySet<string>;
  onSelected: (next: Set<string>) => void;
  canManage: boolean;
  onOpenChain: (contractId: string) => void;
  onHold: (item: RenewalCaseItem) => void;
  onRelease: (item: RenewalCaseItem) => void;
  busyCaseId: string | null;
}

/** The renewal dashboard body: bucket tiles, exception tiles, the due-without-a-review list and the case list. */
export function Radar(props: RadarProps) {
  const { radar, filter, onFilter, rows, selected, onSelected, canManage } = props;
  const { t, locale } = useLocale();
  const f = useFormat();
  const isActive = (kind: string, key: string) => filter.kind === kind && 'key' in filter && filter.key === key;
  const pick = (kind: 'bucket' | 'exception', key: string, caseIds: string[]) =>
    onFilter(isActive(kind, key) ? { kind: 'all' } : { kind, key, caseIds });

  const ex = radar.exceptions;
  const exceptionTiles: { key: string; label: string; definition: string; ids: string[]; count: number }[] = [
    { key: 'expiringWithoutCase', label: t('Due without a review'), definition: t('Fixed-term contracts whose review should be open but is not.'),
      ids: [], count: ex.expiringWithoutCase.length },
    { key: 'activeWithoutOpenReview', label: t('Active contract with no open review'),
      definition: t('Contracts still in force whose only review is closed. Their deadlines still run.'),
      ids: [], count: ex.activeWithoutOpenReview.length },
    { key: 'needsConfirmation', label: t('History to confirm'), definition: t('Reviews waiting for HR to confirm earlier contracts and nationality.'),
      ids: ex.needsConfirmation.caseIds, count: ex.needsConfirmation.count },
    { key: 'noticeDatePassed', label: t('Notice date passed'), definition: t('No decision by the notice date: the contract renews on its current terms.'),
      ids: ex.noticeDatePassed.caseIds, count: ex.noticeDatePassed.count },
    { key: 'qiwaOverdue', label: t('Qiwa overdue'), definition: t('The Qiwa reply window or the Qiwa deadline has passed.'),
      ids: ex.qiwaOverdue.caseIds, count: ex.qiwaOverdue.count },
    { key: 'art55Threshold', label: t('At the Art. 55 limit'), definition: t('Saudi contracts that can only become indefinite or not be renewed.'),
      ids: ex.art55Threshold.caseIds, count: ex.art55Threshold.count },
    { key: 'expiredNoOutcome', label: t('Ended with no outcome'), definition: t('The contract end date passed and the review is still open.'),
      ids: ex.expiredNoOutcome.caseIds, count: ex.expiredNoOutcome.count },
    { key: 'expiredHoldoverPending', label: t('Expired — holdover pending'),
      definition: t('Marked expired with the review still open: the contract continues by law until the holdover is recorded.'),
      ids: ex.expiredHoldoverPending.caseIds, count: ex.expiredHoldoverPending.count },
  ];
  const rec = radar.reconciliation;
  const showUnopened = isActive('exception', 'expiringWithoutCase');
  const showClosedOnly = isActive('exception', 'activeWithoutOpenReview');
  const showNotYet = isActive('exception', 'notYetDue');
  const eligibleInView = rows.filter((r) => r.fastLaneEligible);
  const allEligibleSelected = eligibleInView.length > 0 && eligibleInView.every((r) => selected.has(r.caseId));

  return (
    <div className="space-y-5">
      <section aria-labelledby="renewal-buckets">
        <h2 id="renewal-buckets" className="mb-2 text-sm font-semibold text-slate-700 dark:text-slate-200">{t('Contracts ending')}</h2>
        <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
          {radar.buckets.map((b) => (
            <Tile key={b.key}
              label={b.key === 'overdue' ? t('Overdue: contract already ended') : fill(t('In {from}–{to} days'), { from: b.fromDays, to: b.toDays })}
              definition={b.key === 'overdue' ? t('Open reviews whose contract end date has passed.') : t('Open reviews whose contract ends in this window.')}
              count={b.count} tone={b.key === 'overdue' ? 'warn' : 'neutral'}
              active={isActive('bucket', b.key)} onClick={() => pick('bucket', b.key, b.caseIds)} />
          ))}
        </div>
      </section>

      <section aria-labelledby="renewal-exceptions">
        <h2 id="renewal-exceptions" className="mb-2 text-sm font-semibold text-slate-700 dark:text-slate-200">{t('Needs attention')}</h2>
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-4">
          {exceptionTiles.map((x) => (
            <Tile key={x.key} label={x.label} definition={x.definition} count={x.count} tone="warn"
              active={isActive('exception', x.key)} onClick={() => pick('exception', x.key, x.ids)} />
          ))}
        </div>
      </section>

      <p className="text-xs text-slate-600 dark:text-slate-300" aria-label={t('How the contracts add up')}>
        {fillTemplate(t('{due} fixed-term contracts in force are due: {open} with an open review, {none} without one, {closed} with only a closed review.'), {
          due: rec.dueActiveContracts,
          open: <DrillLink count={rec.withOpenReview} label={t('Show the contracts with an open review')} active={isActive('exception', 'reconOpen')}
            onClick={() => pick('exception', 'reconOpen', rec.openReviewCaseIds)} />,
          none: <DrillLink count={rec.withoutReview} label={t('Show the contracts without a review')} active={showUnopened}
            onClick={() => pick('exception', 'expiringWithoutCase', [])} />,
          closed: <DrillLink count={rec.withClosedReviewOnly} label={t('Show the contracts with only a closed review')} active={showClosedOnly}
            onClick={() => pick('exception', 'activeWithoutOpenReview', [])} />,
        })}
        {rec.notYetDue > 0 && (
          <>
            {' '}
            {fillTemplate(t('{n} more open later.'), {
              n: <DrillLink count={rec.notYetDue} label={t('Show the contracts whose review opens later')} active={showNotYet}
                onClick={() => pick('exception', 'notYetDue', [])} />,
            })}
          </>
        )}
      </p>

      {showNotYet ? (
        <ContractList rows={rec.notYetDueContracts} label={t('Reviews that open later')} empty={t('Nothing in this view.')}
          reason={(u) => fill(t('The renewal review opens on {date}.'), { date: formatDay(u.opensOn, f, radar.today) })}
          onOpenChain={props.onOpenChain} today={radar.today} />
      ) : null}

      {showNotYet ? null : showClosedOnly ? (
        <section aria-label={t('Active contract with no open review')} className="surface overflow-x-auto rounded-xl">
          {ex.activeWithoutOpenReview.length === 0 ? (
            <p className="p-6 text-center text-sm text-slate-500 dark:text-slate-400">{t('Every contract in force has an open review.')}</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-slate-200 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">
                  <th className="p-3 text-start">{t('Employee')}</th>
                  <th className="p-3 text-start">{t('Contract ends')}</th>
                  <th className="p-3 text-start">{t('Last review')}</th>
                  <th className="p-3 text-start"><span className="sr-only">{t('Actions')}</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {ex.activeWithoutOpenReview.map((u) => (
                  <tr key={u.contractId}>
                    <td className="p-3">
                      <span className="font-medium text-slate-800 dark:text-slate-100">{u.employee ? personName(u.employee, locale) : u.contractNumber}</span>
                      <span className="block text-xs text-slate-500">{u.employee?.code} · {u.contractNumber}</span>
                    </td>
                    <td className="p-3 text-slate-600 dark:text-slate-300">{formatDay(u.endDate, f, radar.today)}</td>
                    <td className="p-3 text-slate-600 dark:text-slate-300">{t(closedStateKeys[u.caseState] ?? u.caseState)}</td>
                    <td className="p-3 text-end">
                      <button type="button" onClick={() => props.onOpenChain(u.contractId)}
                        className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-white/10 dark:text-slate-200 dark:hover:bg-white/5">
                        {t('Contract history')}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      ) : showUnopened ? (
        <ContractList rows={ex.expiringWithoutCase} label={t('Due without a review')} empty={t('Every due contract has its review open.')}
          reason={(u) => (u.blockReason ? blockText(u.blockReason, locale).title : t(unopenedReasonKeys[u.reason] ?? u.reason))}
          detail={(u) => (u.blockReason ? blockText(u.blockReason, locale).fix : null)}
          onOpenChain={props.onOpenChain} today={radar.today} />
      ) : (
        <section aria-label={t('Renewal reviews')} className="surface overflow-x-auto rounded-xl">
          {rows.length === 0 ? (
            <p className="p-6 text-center text-sm text-slate-500 dark:text-slate-400">
              {filter.kind === 'all' ? fill(t('No contract ends in the next {days} days.'), { days: radar.days }) : t('Nothing in this view.')}
            </p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-slate-200 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">
                  <th className="w-10 p-3 text-start">
                    {canManage && (
                      <input type="checkbox" aria-label={t('Select every review that can renew on current terms')}
                        checked={allEligibleSelected} disabled={eligibleInView.length === 0}
                        onChange={() => onSelected(toggleAll(selected, rows))} />
                    )}
                  </th>
                  <th className="p-3 text-start">{t('Employee')}</th>
                  <th className="p-3 text-start">{t('Contract ends')}</th>
                  <th className="p-3 text-start">{t('Stage')}</th>
                  <th className="p-3 text-start">{t('What is due')}</th>
                  <th className="p-3 text-start"><span className="sr-only">{t('Actions')}</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {rows.map((item) => {
                  const line = nextLine(item, t, f, radar.today);
                  const busy = props.busyCaseId === item.caseId;
                  return (
                    <tr key={item.caseId} className="align-top hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                      <td className="p-3">
                        {canManage && (
                          <input type="checkbox" checked={selected.has(item.caseId)} disabled={!item.fastLaneEligible}
                            aria-label={fill(t('Select {name} for renewal on current terms'), { name: item.employee.name })}
                            title={item.fastLaneEligible ? undefined : t('Only reviews that can renew on current terms, with nothing blocking them, can be selected.')}
                            onChange={() => onSelected(toggleSelection(selected, item))} />
                        )}
                      </td>
                      <td className="p-3">
                        <span className="font-medium text-slate-800 dark:text-slate-100">
                          {personName(item.employee, locale)}
                        </span>
                        <span className="block text-xs text-slate-500 dark:text-slate-400">
                          {item.employee.code} · {companyName(item, locale)}
                        </span>
                        <div className="mt-1.5 flex flex-wrap gap-1">
                          {item.badges.map((b) => <StatusChip key={b.code} label={badgeText(b, t)} tone={badgeTone(b.code)} />)}
                        </div>
                      </td>
                      <td className="p-3 whitespace-nowrap text-slate-700 dark:text-slate-200">
                        {formatDay(item.expiringEndDate, f, radar.today)}
                        <span className="block text-xs text-slate-500 dark:text-slate-400">
                          {item.daysLeft >= 0 ? fill(t('{n} days left'), { n: item.daysLeft }) : fill(t('{n} days ago'), { n: -item.daysLeft })}
                        </span>
                      </td>
                      <td className="p-3">
                        <StatusChip label={t(stageKeys[item.stage] ?? item.stage)} tone={item.state === 'OnHold' ? 'amber' : 'slate'} />
                        {item.allowedActions.length > 0 && (
                          <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">
                            {item.allowedActions.map((a) => t(actionKeys[a] ?? a)).join(' · ')}
                          </span>
                        )}
                      </td>
                      <td className={`p-3 text-sm ${item.next?.overdue ? 'font-medium text-rose-700 dark:text-rose-300' : 'text-slate-700 dark:text-slate-200'}`}>
                        {line}
                        {item.blockReasons.map((r) => (
                          <span key={r.code} className="mt-1 block text-xs text-slate-500 dark:text-slate-400">
                            {blockText(r, locale).why}
                          </span>
                        ))}
                      </td>
                      <td className="p-3">
                        <div className="flex flex-wrap justify-end gap-1.5">
                          <button type="button" onClick={() => props.onOpenChain(item.contractId)}
                            className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-white/10 dark:text-slate-200 dark:hover:bg-white/5">
                            {t('Contract history')}
                          </button>
                          {canManage && item.state === 'OnHold' && (
                            <button type="button" disabled={busy} onClick={() => props.onRelease(item)}
                              className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-700 disabled:opacity-50 dark:border-white/10 dark:text-slate-200">
                              {t('Release hold')}
                            </button>
                          )}
                          {canManage && item.state !== 'OnHold' && item.stage !== 'Done' && (
                            <button type="button" disabled={busy} onClick={() => props.onHold(item)}
                              className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-700 disabled:opacity-50 dark:border-white/10 dark:text-slate-200">
                              {t('Put on hold')}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </section>
      )}
    </div>
  );
}

/** A count inside a sentence that lists exactly the records it counts (AGENTS.md: every number drills down). */
function DrillLink({ count, label, active, onClick }: { count: number; label: string; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} aria-pressed={active} aria-label={`${label} (${count})`}
      className={`font-semibold underline decoration-dotted underline-offset-2 ${active ? 'text-sapphire' : 'text-slate-800 dark:text-slate-100'}`}>
      {count}
    </button>
  );
}

/** Fills {placeholders} of a translated whole sentence with nodes, so a link can sit inside it in either language. */
function fillTemplate(template: string, parts: Record<string, ReactNode>): ReactNode[] {
  return template.split(/(\{\w+\})/).map((piece, i) => {
    const m = /^\{(\w+)\}$/.exec(piece);
    return <Fragment key={i}>{m && m[1] in parts ? parts[m[1]] : piece}</Fragment>;
  });
}

/** Contracts without an open review (due, or opening later): who, when it ends, and why. */
function ContractList({ rows, label, empty, reason, detail, onOpenChain, today }: {
  rows: RenewalUnopened[]; label: string; empty: string; reason: (u: RenewalUnopened) => string;
  detail?: (u: RenewalUnopened) => string | null; onOpenChain: (contractId: string) => void; today: string;
}) {
  const { t, locale } = useLocale();
  const f = useFormat();
  return (
    <section aria-label={label} className="surface overflow-x-auto rounded-xl">
      {rows.length === 0 ? (
        <p className="p-6 text-center text-sm text-slate-500 dark:text-slate-400">{empty}</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-200 text-start text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">
              <th className="p-3 text-start">{t('Employee')}</th>
              <th className="p-3 text-start">{t('Contract ends')}</th>
              <th className="p-3 text-start">{t('Why there is no review')}</th>
              <th className="p-3 text-start"><span className="sr-only">{t('Actions')}</span></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {rows.map((u) => (
              <tr key={u.contractId}>
                <td className="p-3">
                  <span className="font-medium text-slate-800 dark:text-slate-100">{u.employee ? personName(u.employee, locale) : u.contractNumber}</span>
                  <span className="block text-xs text-slate-500">{u.employee?.code} · {u.contractNumber}</span>
                </td>
                <td className="p-3 text-slate-600 dark:text-slate-300">{formatDay(u.endDate, f, today)}</td>
                <td className="p-3 text-slate-600 dark:text-slate-300">
                  {reason(u)}
                  {detail?.(u) && <span className="block text-xs text-slate-500">{detail(u)}</span>}
                </td>
                <td className="p-3 text-end">
                  <button type="button" onClick={() => onOpenChain(u.contractId)}
                    className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-white/10 dark:text-slate-200 dark:hover:bg-white/5">
                    {t('Contract history')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

/** The bar shown while rows are selected. R5 passes its batch action in `children` (fast lane). */
export function SelectionBar({ count, onClear, children }: { count: number; onClear: () => void; children?: ReactNode }) {
  const { t } = useLocale();
  if (count === 0) return null;
  return (
    <div role="region" aria-label={t('Selected reviews')}
      className="sticky bottom-3 z-10 flex flex-wrap items-center gap-3 rounded-xl bg-slate-900 px-4 py-2.5 text-sm text-white shadow-lg dark:bg-slate-800">
      <span className="font-medium">{fill(t('{n} selected to renew on current terms'), { n: count })}</span>
      {children}
      <button type="button" onClick={onClear} className="ms-auto rounded-lg px-2.5 py-1 text-xs font-medium text-slate-200 hover:bg-white/10">
        {t('Clear selection')}
      </button>
    </div>
  );
}
