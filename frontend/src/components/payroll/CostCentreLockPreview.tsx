'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, ExternalLink, Eye, Lock, RotateCcw, Search } from 'lucide-react';
import { payrollApi, type PayrollGLJournal, type PayrollRun } from '../../api/payroll';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { textDir } from '../../lib/glCostAccounting';
import {
  blockerCards,
  classifyLockError,
  groupJournal,
  readOnlyLockNote,
  type BlockerRow,
  type GroupMode,
  type JournalGroup,
  type LockBlocker,
  type LockGroup,
  type LockOutcome,
} from '../../lib/payrollCostLock';
import { Modal } from '../Modal';
import { Badge, WarningNote } from '../gl/glUi';

/**
 * Screen 4 (cost accounting v1): the payroll journal as Lock will post it, by cost centre. It shows what blocks the lock,
 * with a way to fix each blocked employee in a new tab, and it owns the Lock action so that every refusal Lock can answer
 * (blockers, sent back after approval, separation of duties) is answered on the screen the user is looking at.
 */

const LARGE_RUN = 2000;
const PAGE = 200;
const DEFAULT_HREF = '/setup?tab=costAccounting';

/** "2026-06" in the viewer's language ("Jun 2026" / "يونيو 2026"). */
export function periodLabel(period: string, locale: string): string {
  const [y, m] = period.split('-').map(Number);
  if (!y || !m) return period;
  return new Intl.DateTimeFormat(locale === 'ar' ? 'ar' : 'en', { month: locale === 'ar' ? 'long' : 'short', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(y, m - 1, 1)));
}

const kindLabel = (kind: string): string => {
  switch (kind) {
    case 'employee': return 'Fix employee';
    case 'department': return 'Open department';
    case 'parentDepartment': return 'Open parent department';
    default: return 'Open position';
  }
};

function reasonText(t: ReturnType<typeof useT>, reason: BlockerRow['reason']): string {
  switch (reason) {
    case 'none': return t('Nothing set at any level');
    case 'reserved': return t("The cost centre's functional class is reserved and cannot be used for posting yet");
    case 'employee': return t("The employee's cost centre is inactive, deleted or in another company");
    case 'position': return t("The position's cost centre is inactive, deleted or in another company");
    case 'department': return t("The department's cost centre is inactive, deleted or in another company");
    case 'parentDepartment': return t("The parent department's cost centre is inactive, deleted or in another company");
    default: return t("The company default's cost centre is inactive, deleted or in another company");
  }
}

interface Props {
  journal: PayrollGLJournal;
  run: PayrollRun | undefined;
  companyName: string;
  mayLock: boolean;
  /** Reads the journal (and the run list) again. */
  onReload: () => Promise<void> | void;
  /** Lock succeeded: the run as the server now has it. */
  onLocked: (run: PayrollRun) => void;
  onGoToApprovals: () => void;
}

export function CostCentreLockPreview({ journal, run, companyName, mayLock, onReload, onLocked, onGoToApprovals }: Props) {
  const t = useT();
  const { locale } = useLocale();
  const fmt = useFormat();
  const costing = journal.costing!;
  const posted = !!journal.isPosted || run?.status === 'Locked';
  const money = (n: number) => fmt.money(n, journal.currency);

  const [mode, setMode] = useState<GroupMode>('cc');
  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(PAGE);
  const [outcome, setOutcome] = useState<LockOutcome | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [locking, setLocking] = useState(false);
  const [rechecking, setRechecking] = useState(false);
  const lockingRef = useRef(false);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const focusBanner = useRef(false);
  const radios = useRef<Record<GroupMode, HTMLButtonElement | null>>({ cc: null, account: null });

  // What blocks: the refusal Lock just answered (it is the newer truth), else the preview.
  const refusal = outcome?.kind === 'blocked' ? outcome : null;
  const blockers: LockBlocker[] = refusal ? refusal.blockers : costing.blockers;
  const serverGroups: LockGroup[] = refusal ? refusal.groups : costing.groups;
  const roundingBlocked = costing.roundingBlocked || refusal?.error === 'cost_centre_rounding_required';
  const cards = useMemo(() => blockerCards(serverGroups, blockers, locale), [serverGroups, blockers, locale]);
  const problemCount = cards.length + (roundingBlocked ? 1 : 0);
  const blocked = !posted && (problemCount > 0 || !journal.isBalanced);

  const blockedAccounts = useMemo(() => new Set(blockers.map((b) => b.accountCode).filter((c): c is string => !!c)), [blockers]);
  const entries = journal.entries;
  const groups = useMemo(() => groupJournal(entries, mode, blockedAccounts, posted), [entries, mode, blockedAccounts, posted]);
  const needle = query.trim().toLowerCase();
  const shown = useMemo(() => {
    if (!needle) return groups;
    return groups
      .map((g) => ({ ...g, lines: g.lines.filter((l) => `${l.glAccount} ${l.glAccountName} ${l.componentName} ${l.costCentreCode ?? ''} ${l.costCentreName ?? ''}`.toLowerCase().includes(needle)) }))
      .filter((g) => g.lines.length > 0);
  }, [groups, needle]);
  const large = entries.length > LARGE_RUN;

  const recheck = useCallback(async (explicit = true) => {
    setRechecking(true);
    focusBanner.current = explicit;
    try {
      await onReload();
      setOutcome(null);
    } finally {
      setRechecking(false);
    }
  }, [onReload]);

  // Back from a fix tab: read the journal again so the banner shows what is still wrong.
  useEffect(() => {
    if (posted || problemCount === 0) return undefined;
    const onFocus = () => { void recheck(false); };
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [posted, problemCount, recheck]);

  // After a failed Lock or a Re-check, focus moves once to the banner heading.
  useEffect(() => {
    if (focusBanner.current && problemCount > 0) {
      headingRef.current?.focus();
      focusBanner.current = false;
    }
  });

  const lock = async () => {
    if (!run || lockingRef.current) return;
    lockingRef.current = true;
    setLocking(true);
    setConfirming(false);
    setOutcome(null);
    try {
      const updated = await payrollApi.lockRun(run.id);
      onLocked(updated);
    } catch (e) {
      const result = classifyLockError(e, locale);
      setOutcome(result);
      if (result.kind === 'blocked') focusBanner.current = true;
      if (result.kind === 'sent_back') void onReload();
    } finally {
      lockingRef.current = false;
      setLocking(false);
    }
  };

  const onRadioKey = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(e.key)) return;
    e.preventDefault();
    const next: GroupMode = mode === 'cc' ? 'account' : 'cc';
    setMode(next);
    radios.current[next]?.focus();
  };

  const toggle = (key: string) => setCollapsed((s) => { const n = new Set(s); if (n.has(key)) n.delete(key); else n.add(key); return n; });
  const allCollapsed = shown.length > 0 && shown.every((g) => collapsed.has(g.key));

  const period = periodLabel(journal.period, locale);
  const lockable = !!run && run.status === 'Approved' && !posted && outcome?.kind !== 'sent_back';
  const cols = mode === 'account' ? 5 : 4;

  const groupTitle = (g: JournalGroup) => {
    if (g.kind === 'missing') return t('Missing cost centre');
    if (g.kind === 'none') return t('Not costed (balance-sheet lines)');
    if (g.kind === 'legacy') return t('Not recorded (run locked before cost centres)');
    return (
      <>
        <bdi dir="ltr" className="font-mono">{g.code}</bdi>
        {g.name ? <> · <bdi dir={textDir(g.name)}>{g.name}</bdi></> : null}
      </>
    );
  };

  return (
    <section data-testid="cc-lock-preview" aria-labelledby="cc-lock-title" className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="cc-lock-title" className="text-base font-semibold text-slate-900 dark:text-white">
          {t('Review and lock')} — <bdi>{period}</bdi> · <bdi dir={textDir(companyName)}>{companyName}</bdi>
        </h2>
        {posted
          ? <span data-testid="cc-lock-status-chip"><Badge tone="emerald"><Lock className="h-3 w-3" aria-hidden /> {t('Locked')}</Badge></span>
          : <span data-testid="cc-lock-status-chip"><Badge tone="amber"><Eye className="h-3 w-3" aria-hidden /> {t('Preview — not posted yet')}</Badge></span>}
      </header>

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="surface p-4" data-testid="cc-lock-kpi-debits">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Total debits')}</p>
          <p className="text-lg font-bold tabular-nums text-sapphire dark:text-cyanAccent"><bdi dir="ltr">{money(journal.totalDebits)}</bdi></p>
        </div>
        <div className="surface p-4" data-testid="cc-lock-kpi-credits">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Total credits')}</p>
          <p className="text-lg font-bold tabular-nums text-sapphire dark:text-cyanAccent"><bdi dir="ltr">{money(journal.totalCredits)}</bdi></p>
        </div>
        <div className="surface p-4" data-testid="cc-lock-kpi-uncosted">
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Uncosted employees')}</p>
          <p className="text-lg font-bold tabular-nums text-slate-900 dark:text-white"><bdi dir="ltr">{fmt.integer(costing.uncostedEmployees ?? 0)}</bdi></p>
        </div>
      </div>

      {journal.isBalanced ? (
        <div role="status" className="flex items-center gap-2 rounded-lg bg-emerald-50 px-4 py-2.5 dark:bg-emerald-500/10">
          <CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden />
          <p className="text-sm font-medium text-emerald-800 dark:text-emerald-300">{t('Journal is balanced — Debits = Credits')}</p>
        </div>
      ) : (
        <div role="alert" className="flex items-center gap-2 rounded-lg bg-rose-50 px-4 py-2.5 dark:bg-rose-500/10">
          <AlertTriangle className="h-4 w-4 text-rose-700" aria-hidden />
          <p className="text-sm font-medium text-rose-700 dark:text-rose-300">{t('Journal is out of balance — investigate before posting')}</p>
        </div>
      )}

      {!posted && problemCount > 0 && (
        <div data-testid="cc-lock-blockers" role="alert" className="space-y-3 rounded-xl border border-rose-200 bg-rose-50 p-4 text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 ref={headingRef} tabIndex={-1} className="flex items-center gap-2 text-sm font-bold outline-none focus-visible:ring-2 focus-visible:ring-rose-500">
              <AlertTriangle className="h-4 w-4" aria-hidden />
              {t('Lock is blocked: {count, plural, one {# problem} other {# problems}}', { count: problemCount })}
            </h3>
            <button type="button" data-testid="cc-lock-recheck" onClick={() => void recheck(true)} disabled={rechecking} className="btn-secondary h-8 px-3 text-xs">
              <RotateCcw className="h-3.5 w-3.5" aria-hidden /> {t('Re-check')}
            </button>
          </div>
          {cards.map((card) => (
            <article key={card.key} data-testid={`cc-lock-blocker-${card.error}`} className="rounded-lg border border-rose-200 bg-white/70 p-3 dark:border-rose-500/20 dark:bg-black/20">
              <p className="text-sm text-rose-800 dark:text-rose-100" dir="auto">{card.message}</p>
              {card.rows.length > 0 && (
                <div className="mt-2 overflow-x-auto" tabIndex={0} role="group" aria-label={t('Blocked employees')}>
                  <table className="w-full min-w-[520px] text-xs">
                    <thead>
                      <tr className="text-slate-600 dark:text-slate-300">
                        <th scope="col" className="px-2 py-1 text-start font-medium">{t('Code')}</th>
                        <th scope="col" className="px-2 py-1 text-start font-medium">{t('Why')}</th>
                        <th scope="col" className="px-2 py-1 text-start font-medium">{t('Fix')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {card.rows.map((row) => (
                        <tr key={row.employeeId} data-testid={`cc-lock-blocker-row-${row.code}`} className="border-t border-rose-100 align-top dark:border-rose-500/10">
                          <th scope="row" className="px-2 py-1.5 text-start font-medium"><bdi dir="ltr" className="font-mono">{row.code}</bdi></th>
                          <td className="px-2 py-1.5 text-slate-700 dark:text-slate-200">{reasonText(t, row.reason)}</td>
                          <td className="px-2 py-1.5">
                            <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
                              {row.links.map((l) => (
                                <Link
                                  key={l.kind}
                                  href={l.href}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  data-testid={`cc-lock-fix-${l.kind.toLowerCase()}-${row.code}`}
                                  aria-label={t('{action} {code}. Opens in a new tab', { action: t(kindLabel(l.kind)), code: row.code })}
                                  className="inline-flex min-h-6 items-center gap-1 font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent"
                                >
                                  {t(kindLabel(l.kind))}
                                  <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden />
                                </Link>
                              ))}
                            </span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
                {card.hidden > 0 && (
                  <Link href={DEFAULT_HREF} target="_blank" rel="noopener noreferrer" data-testid="cc-lock-more-link" className="inline-flex min-h-6 items-center font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">
                    {t('…and {n} more. See all in Cost accounting readiness.', { n: card.hidden })}
                  </Link>
                )}
                {card.needsDefault && (
                  <Link href={DEFAULT_HREF} target="_blank" rel="noopener noreferrer" data-testid="cc-lock-set-default" aria-label={t('{action}. Opens in a new tab', { action: t('Set company default') })} className="inline-flex min-h-6 items-center gap-1 font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">
                    {t('Set company default')}
                    <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden />
                  </Link>
                )}
              </div>
            </article>
          ))}
          {roundingBlocked && (
            <article data-testid="cc-lock-blocker-cost_centre_rounding_required" className="rounded-lg border border-rose-200 bg-white/70 p-3 dark:border-rose-500/20 dark:bg-black/20">
              <p className="text-sm text-rose-800 dark:text-rose-100" dir="auto">
                {refusal?.error === 'cost_centre_rounding_required' && refusal.message
                  ? refusal.message
                  : t('A rounding difference must be posted to an account that requires a cost centre. Set a default cost centre for the company, then lock again.')}
              </p>
              <Link href={DEFAULT_HREF} target="_blank" rel="noopener noreferrer" data-testid="cc-lock-set-default" aria-label={t('{action}. Opens in a new tab', { action: t('Set company default') })} className="mt-2 inline-flex min-h-6 items-center gap-1 text-xs font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">
                {t('Set company default')}
                <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden />
              </Link>
            </article>
          )}
        </div>
      )}

      {!posted && (costing.uncostedEmployees ?? 0) > 0 && (
        <div data-testid="cc-lock-warning-W1">
          <WarningNote>
            {t('{count, plural, one {# employee has no cost centre; their Optional-account lines post uncosted.} other {# employees have no cost centre; their Optional-account lines post uncosted.}}', { count: costing.uncostedEmployees ?? 0 })}
          </WarningNote>
        </div>
      )}
      {!posted && (costing.fallbackEmployees ?? 0) > 0 && (
        <div data-testid="cc-lock-warning-W2">
          <WarningNote>
            {t('{count, plural, one {# employee falls back to the company default cost centre ({amount}).} other {# employees fall back to the company default cost centre ({amount}).}}', { count: costing.fallbackEmployees ?? 0, amount: money(costing.fallbackAmount ?? 0) })}
          </WarningNote>
        </div>
      )}

      {posted && groups[0]?.kind === 'legacy' && (
        <div data-testid="cc-lock-legacy-note" role="note" className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/5 dark:text-slate-300">
          {t('Not recorded (run locked before cost centres)')}
        </div>
      )}
      {large && (
        <div data-testid="cc-lock-large-note" role="note" className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">
          {t('This run has {n} lines. Showing up to {m} per group; search or group to narrow.', { n: entries.length, m: Math.min(limit, entries.length) })}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <span id="cc-lock-groupby-label" className="text-xs font-medium text-slate-600 dark:text-slate-300">{t('Group by')}</span>
          <div role="radiogroup" aria-labelledby="cc-lock-groupby-label" aria-controls="cc-lock-table" className="inline-flex overflow-hidden rounded-lg border border-slate-200 dark:border-white/10">
            {([['cc', 'Cost centre'], ['account', 'Account']] as const).map(([value, label]) => (
              <button
                key={value}
                ref={(el) => { radios.current[value] = el; }}
                type="button"
                role="radio"
                aria-checked={mode === value}
                tabIndex={mode === value ? 0 : -1}
                data-testid={`cc-lock-groupby-${value}`}
                onClick={() => setMode(value)}
                onKeyDown={onRadioKey}
                className={`min-h-8 px-3 text-xs font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire ${mode === value ? 'bg-sapphire text-white' : 'bg-white text-slate-700 dark:bg-white/5 dark:text-slate-200'}`}
              >
                {t(label)}
              </button>
            ))}
          </div>
        </div>
        <label className="relative">
          <span className="sr-only">{t('Search lines')}</span>
          <Search className="pointer-events-none absolute start-2 top-2 h-4 w-4 text-slate-400" aria-hidden />
          <input data-testid="cc-lock-search" type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('Search lines')} className="input h-8 ps-8 text-xs" />
        </label>
        <button type="button" onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(shown.map((g) => g.key)))} className="btn-secondary h-8 px-3 text-xs">
          {allCollapsed ? t('Expand all') : t('Collapse all')}
        </button>
      </div>

      {entries.length === 0 ? (
        <p role="status" className="surface p-4 text-sm text-slate-600 dark:text-slate-300">{t('This run has no journal lines.')}</p>
      ) : shown.length === 0 ? (
        <p role="status" className="surface p-4 text-sm text-slate-600 dark:text-slate-300">{t('No journal lines match.')}</p>
      ) : (
        <div className="surface overflow-x-auto" tabIndex={0} role="group" aria-labelledby="cc-lock-title">
          <table id="cc-lock-table" className="w-full min-w-[640px] text-sm">
            <caption className="sr-only">{mode === 'cc' ? t('Payroll journal grouped by cost centre') : t('Payroll journal grouped by account')}</caption>
            <thead>
              <tr className="border-b border-slate-100 text-xs text-slate-500 dark:border-white/5 dark:text-slate-400">
                <th scope="col" className="px-3 py-2 text-start font-medium">{t('Account')}</th>
                <th scope="col" className="px-3 py-2 text-start font-medium">{t('Description')}</th>
                {mode === 'account' && <th scope="col" className="px-3 py-2 text-start font-medium">{t('Cost centre')}</th>}
                <th scope="col" className="px-3 py-2 text-end font-medium">{t('Debit')}</th>
                <th scope="col" className="px-3 py-2 text-end font-medium">{t('Credit')}</th>
              </tr>
            </thead>
            {shown.map((g) => {
              const open = !collapsed.has(g.key);
              const lines = large ? g.lines.slice(0, limit) : g.lines;
              const tone = g.kind === 'missing' ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300' : g.kind === 'none' || g.kind === 'legacy' ? 'bg-slate-50 text-slate-700 dark:bg-white/5 dark:text-slate-300' : 'bg-sapphire/5 text-slate-900 dark:bg-white/[0.04] dark:text-white';
              return (
                <tbody key={g.key} data-testid={`cc-lock-group-${g.key}`}>
                  <tr className={tone}>
                    <th scope="rowgroup" colSpan={cols - 2} className="px-3 py-2 text-start font-semibold">
                      <button type="button" aria-expanded={open} onClick={() => toggle(g.key)} className="inline-flex min-h-6 items-center gap-1.5 text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
                        {open ? <ChevronDown className="h-4 w-4" aria-hidden /> : <ChevronRight className="h-4 w-4" aria-hidden />}
                        {g.kind === 'missing' && <AlertTriangle className="h-4 w-4" aria-hidden />}
                        <span>{groupTitle(g)}</span>
                      </button>
                      <span className="ms-2 text-xs font-normal">{t('Net {amount}', { amount: fmt.money(g.net, null) })}</span>
                    </th>
                    <td className="px-3 py-2 text-end font-semibold tabular-nums"><bdi dir="ltr">{fmt.money(g.debit, null)}</bdi></td>
                    <td className="px-3 py-2 text-end font-semibold tabular-nums"><bdi dir="ltr">{fmt.money(g.credit, null)}</bdi></td>
                  </tr>
                  {open && lines.map((l, i) => {
                    const n = entries.indexOf(l) + 1;
                    const [code, ...rest] = l.glAccount.split(' — ');
                    return (
                      <tr key={`${n}-${i}`} data-testid={`cc-lock-line-${n}`} className="border-t border-slate-100 dark:border-white/5">
                        <td className="px-3 py-1.5"><bdi dir="ltr" className="font-mono text-xs">{code}</bdi>{rest.length ? <> <bdi dir={textDir(l.glAccountName)}>{l.glAccountName}</bdi></> : null}</td>
                        <td className="px-3 py-1.5 text-slate-700 dark:text-slate-300"><bdi dir="auto">{l.componentName}</bdi></td>
                        {mode === 'account' && (
                          <td className="px-3 py-1.5">
                            {l.costCentreCode
                              ? <bdi dir="ltr" className="font-mono text-xs">{l.costCentreCode}</bdi>
                              : <><span aria-hidden>—</span><span className="sr-only">{t('Not costed')}</span></>}
                          </td>
                        )}
                        <td className="px-3 py-1.5 text-end tabular-nums"><bdi dir="ltr">{l.entryType === 'DR' ? fmt.money(l.amount, null) : ''}</bdi></td>
                        <td className="px-3 py-1.5 text-end tabular-nums"><bdi dir="ltr">{l.entryType === 'CR' ? fmt.money(l.amount, null) : ''}</bdi></td>
                      </tr>
                    );
                  })}
                </tbody>
              );
            })}
          </table>
          {large && entries.length > limit && (
            <div className="border-t border-slate-100 p-3 text-center dark:border-white/5">
              <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setLimit((n) => n + PAGE)}>{t('Show more')}</button>
            </div>
          )}
        </div>
      )}

      {outcome?.kind === 'sent_back' && (
        <div data-testid="cc-lock-sent-back-banner" role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <p className="flex items-start gap-2"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden /> <span dir="auto">{outcome.message}</span></p>
          <button type="button" onClick={onGoToApprovals} className="btn-secondary h-8 px-3 text-xs">{t('Go to approvals')}</button>
        </div>
      )}
      {(outcome?.kind === 'sod' || outcome?.kind === 'locked_other' || outcome?.kind === 'other') && (
        <p role="alert" data-testid="cc-lock-error" className="flex items-start gap-2 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span dir="auto">{outcome.message || t('Failed to lock the payroll run. Resolve any blocking validation errors first.')}</span>
        </p>
      )}

      {run && run.status === 'Approved' && !posted && readOnlyLockNote(mayLock) && (
        <p data-testid="cc-lock-readonly-note" role="note" className="text-sm text-slate-600 dark:text-slate-300">{t('You do not have permission to lock payroll runs.')}</p>
      )}
      {lockable && mayLock && (
        <div className="flex flex-wrap items-center justify-end gap-3">
          <span id="cc-lock-reason" className="sr-only">{blocked ? t('Fix the problems above to lock.') : ''}</span>
          <button
            type="button"
            data-testid="cc-lock-submit"
            aria-disabled={blocked || locking}
            aria-describedby={blocked ? 'cc-lock-reason' : undefined}
            onClick={() => { if (!blocked && !locking) setConfirming(true); }}
            className={`btn-primary ${blocked || locking ? 'cursor-not-allowed opacity-60' : ''}`}
          >
            <Lock className="h-4 w-4" aria-hidden /> {locking ? t('Locking…') : t('Lock run')}
          </button>
        </div>
      )}

      <Modal
        isOpen={confirming}
        title={t('Lock run')}
        onClose={() => setConfirming(false)}
        size="sm"
        closeLabel={t('Close')}
        footer={(
          <>
            <button type="button" className="btn-secondary" onClick={() => setConfirming(false)}>{t('Cancel')}</button>
            <button type="button" data-testid="cc-lock-confirm" className="btn-primary" onClick={() => void lock()}>{t('Lock run')}</button>
          </>
        )}
      >
        <p className="text-sm text-slate-600 dark:text-slate-300">
          {t('Lock {period} for {company}? This posts {n} journal lines. A locked run cannot be edited; correct it by voiding.', { period, company: companyName, n: entries.length })}
        </p>
      </Modal>
    </section>
  );
}

/** Costing off: the journal is the one the tab always showed, plus one line that says lines post without cost centres. */
export function CostingOffNote() {
  const t = useT();
  return (
    <div data-testid="cc-lock-costing-off-note" role="note" className="flex flex-wrap items-center gap-2 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/5 dark:text-slate-300">
      <span>{t('Cost accounting is off for this company. Lines post without cost centres.')}</span>
      <Link href={DEFAULT_HREF} className="inline-flex min-h-6 items-center font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">{t('Open Cost accounting')}</Link>
    </div>
  );
}
