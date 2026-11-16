'use client';

/** "My records": the standing figures behind the day, each from the dashboard payload and linked to its page. */

import { useId } from 'react';
import Link from 'next/link';
import type { EssDashboard, EssRosterEntry } from '../../../api/ess';
import { StatusChip } from '../../StatusChip';
import { useFormat } from '../../../hooks/useFormat';
import { useT } from '../../../hooks/useT';
import { ESS_LEAVE_PATH } from '../../../lib/essSelfService';
import { leaveBalanceTotal } from '../../../lib/leaveBalanceDisplay';

function RecordCard({ title, link, testId, children }: { title: string; link?: { href: string; label: string }; testId?: string; children: React.ReactNode }) {
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-t`} data-testid={testId} className="wg-card flex min-w-0 flex-col gap-3 p-4">
      <header className="flex min-h-8 items-center justify-between gap-3">
        <h2 id={`${uid}-t`} className="text-body font-semibold text-slate-900 dark:text-white">{title}</h2>
        {link && <Link href={link.href} className="rounded-lg px-1 py-1 text-body-sm font-semibold text-sapphire outline-none hover:underline focus-visible:ring-2 focus-visible:ring-sapphire dark:text-blue-300">{link.label}</Link>}
      </header>
      {children}
    </section>
  );
}

const Quiet = ({ children }: { children: React.ReactNode }) => <p className="text-body-sm text-slate-600 dark:text-slate-300" data-empty-state>{children}</p>;

function Kv({ k, v }: { k: string; v: React.ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-t border-[color:var(--wg-line)] py-1.5 text-body-sm first:border-t-0">
      <dt className="text-slate-600 dark:text-slate-300">{k}</dt>
      <dd className="font-semibold tabular-nums text-slate-900 dark:text-white">{v}</dd>
    </div>
  );
}

export function MyDayRecords({ dashboard, leaveName, roster, rosterFailed, onRetryRoster, loansHref, canWrite }: {
  dashboard: EssDashboard; leaveName: (id: string | null, stored: string) => string;
  roster: EssRosterEntry[] | null; rosterFailed: boolean; onRetryRoster: () => void; loansHref: string | null; canWrite: boolean;
}) {
  const t = useT();
  const fx = useFormat();
  const days = (n: number) => (Number.isInteger(n) ? fx.number(n) : fx.number(n, { minimumFractionDigits: 1, maximumFractionDigits: 1 }));
  const perf = dashboard.performanceSnapshot;
  const next = dashboard.nextApprovedLeave;
  const loanGroups = dashboard.loanSummaries ?? (dashboard.loansSummary ? [dashboard.loansSummary] : []);
  const whole = (n: number, c: string) => fx.money(n, c, { decimals: 0 });

  return (
    <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
      <RecordCard title={t('Leave balances')} testId="ess-home-leave" link={{ href: ESS_LEAVE_PATH, label: canWrite ? t('Request leave') : t('Open my leave') }}>
        {dashboard.leaveBalances.length === 0 ? (
          <Quiet>{t('No leave balances have been set up for you this year.')}</Quiet>
        ) : (
          <ul className="flex flex-col gap-3">
            {dashboard.leaveBalances.map((b) => {
              const name = leaveName(b.leaveTypeId, b.leaveTypeName);
              if (b.statutoryEntitlementDays != null) {
                // Saudi statutory event leave is granted per event, not drawn from a balance.
                return (
                  <li key={b.leaveTypeId}>
                    <p className="text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{name}</bdi></p>
                    <p className="text-meta text-slate-600 dark:text-slate-300">{t('Statutory entitlement: {days} days per event', { days: fx.number(b.statutoryEntitlementDays) })}</p>
                  </li>
                );
              }
              const total = leaveBalanceTotal({ available: b.available, entitled: b.entitled ?? b.available }) ?? 0;
              const used = Math.max(0, Math.min(b.used, total));
              const pending = Math.max(0, Math.min(b.pending, total - used));
              const avail = Math.max(0, Math.min(b.available, total - used - pending));
              const pct = (n: number) => `${total ? (n / total) * 100 : 0}%`;
              return (
                <li key={b.leaveTypeId}>
                  <div className="flex items-baseline justify-between gap-3">
                    <p className="min-w-0 text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{name}</bdi></p>
                    <p className="shrink-0 text-meta tabular-nums text-slate-700 dark:text-slate-200">
                      {total ? t('{available} of {entitled} days available', { available: days(b.available), entitled: days(total) }) : t('{available} days', { available: days(b.available) })}
                    </p>
                  </div>
                  {total > 0 && <div role="img" aria-label={t('{used} used, {pending} pending, {available} available', { used: days(b.used), pending: days(b.pending), available: days(b.available) })}
                    className="mt-1.5 flex h-2 overflow-hidden rounded-full border border-[color:var(--wg-line)] bg-slate-100 dark:bg-white/10">
                    <span className="h-full bg-slate-500" style={{ width: pct(used) }} />
                    <span className="h-full bg-amber-600" style={{ width: pct(pending) }} />
                    <span className="h-full bg-sapphire dark:bg-blue-400" style={{ width: pct(avail) }} />
                  </div>}
                </li>
              );
            })}
          </ul>
        )}
        <p className="flex gap-3 text-meta text-slate-600 dark:text-slate-300" aria-hidden="true">
          <span className="inline-flex items-center gap-1.5"><span className="h-2 w-2 rounded bg-slate-500" />{t('Used')}</span>
          <span className="inline-flex items-center gap-1.5"><span className="h-2 w-2 rounded bg-amber-600" />{t('Pending')}</span>
          <span className="inline-flex items-center gap-1.5"><span className="h-2 w-2 rounded bg-sapphire dark:bg-blue-400" />{t('Available')}</span>
        </p>
        {next && (
          <dl><Kv k={t('Next approved leave')} v={<bdi>{t('{start} to {end}, {count, plural, one {# day} other {# days}}', { start: fx.date(next.startDate, 'dayMonth'), end: fx.date(next.endDate, 'dayMonth'), count: next.days })}</bdi>} /></dl>
        )}
      </RecordCard>

      <RecordCard title={t('My Upcoming Shifts')}>
        {rosterFailed ? (
          <p role="alert" className="flex flex-wrap items-center gap-2 text-body-sm text-amber-900 dark:text-amber-200">
            {t('Your roster could not be loaded.')}
            <button type="button" onClick={onRetryRoster} className="rounded font-semibold underline outline-none focus-visible:ring-2 focus-visible:ring-sapphire">{t('Retry')}</button>
          </p>
        ) : roster === null ? (
          <Quiet>{t('Loading…')}</Quiet>
        ) : roster.length === 0 ? (
          <Quiet>{t('No shifts scheduled. Your roster will appear here once published.')}</Quiet>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {roster.slice(0, 7).map((s) => (
              <li key={s.id} className="flex items-center gap-3 rounded-xl border border-[color:var(--wg-line)] px-3 py-1.5">
                <span aria-hidden="true" className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: s.shiftColor || 'var(--wg-hero-light)' }} />
                <span className="min-w-0 text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{s.shiftName}</bdi></span>
                <span className="ms-auto shrink-0 text-meta text-slate-600 dark:text-slate-300">{fx.date(s.date, 'weekdayDate')}</span>
              </li>
            ))}
          </ul>
        )}
        {roster && roster.length > 7 && <p className="text-meta text-slate-600 dark:text-slate-300">{t('+{count} more', { count: roster.length - 7 })}</p>}
        <p className="text-meta text-slate-600 dark:text-slate-300">{t('Next 2 weeks')}</p>
      </RecordCard>

      <RecordCard title={t('Performance and goals')}>
        {perf ? (
          <>
            <div className="flex items-baseline gap-3">
              <p className="text-kpi font-semibold tabular-nums text-slate-900 dark:text-white">{t('{done} of {total}', { done: perf.goalsCompleted, total: perf.goalsTotal })}</p>
              <p className="text-body-sm text-slate-600 dark:text-slate-300">{t('Goals complete')}</p>
            </div>
            <div role="img" aria-label={t('{done} of {total}', { done: perf.goalsCompleted, total: perf.goalsTotal })} className="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10">
              <span className="block h-full rounded-full bg-sapphire dark:bg-blue-400" style={{ width: `${perf.goalsTotal > 0 ? Math.min(100, Math.round((perf.goalsCompleted / perf.goalsTotal) * 100)) : 0}%` }} />
            </div>
            <dl>
              <Kv k={t('Current cycle')} v={<bdi>{perf.cycleName}</bdi>} />
              {perf.lastRating !== null && <Kv k={t('Last rating')} v={fx.number(Number(perf.lastRating), { minimumFractionDigits: 1, maximumFractionDigits: 1 })} />}
            </dl>
          </>
        ) : (
          <Quiet>{t('Your goals appear here when your manager opens a review cycle.')}</Quiet>
        )}
      </RecordCard>

      <RecordCard title={t('Announcements')}>
        {dashboard.announcements.length === 0 ? (
          <Quiet>{t('No active announcements.')}</Quiet>
        ) : (
          <ul className="flex flex-col gap-3">
            {dashboard.announcements.map((a) => (
              <li key={a.id} className="border-s-4 border-sapphire ps-3 dark:border-blue-400">
                <p className="text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{a.title}</bdi></p>
                <p className="line-clamp-3 text-meta text-slate-700 dark:text-slate-200"><bdi>{a.body}</bdi></p>
                <p className="text-meta text-slate-600 dark:text-slate-300">{t('Published {date}', { date: fx.date(a.publishedAtUtc, 'dayMonth') })}</p>
              </li>
            ))}
          </ul>
        )}
      </RecordCard>

      <RecordCard title={t('My Loans')} link={loansHref ? { href: loansHref, label: t('Open my loans') } : undefined}>
        {loanGroups.length === 0 ? (
          <Quiet>{t('No active loans')}</Quiet>
        ) : (
          <>
            <p className="text-kpi font-semibold tabular-nums text-slate-900 dark:text-white"><bdi>{loanGroups.map((g) => whole(g.totalOutstanding, g.currency)).join(' + ')}</bdi></p>
            <p className="text-body-sm text-slate-600 dark:text-slate-300">{t('{count, plural, one {Outstanding on # active loan} other {Outstanding on # active loans}}', { count: loanGroups.reduce((s, g) => s + g.activeLoanCount, 0) })}</p>
            <dl>
              {loanGroups.filter((g) => g.nextInstallmentDate).map((g) => (
                <Kv key={g.currency} k={t('Next installment')}
                  v={<bdi>{g.nextInstallmentAmount != null ? `${whole(g.nextInstallmentAmount, g.currency)}, ` : ''}{fx.date(g.nextInstallmentDate!, 'dayMonth')}</bdi>} />
              ))}
            </dl>
          </>
        )}
      </RecordCard>

      <RecordCard title={t('My documents')} link={{ href: '/ess/onboarding', label: t('Open my details') }}>
        {dashboard.documentAlerts.length === 0 ? (
          <Quiet>{t('No document is expiring in the next 60 days.')}</Quiet>
        ) : (
          <ul className="flex flex-col gap-2">
            {dashboard.documentAlerts.map((d) => (
              <li key={d.id} className="flex items-center justify-between gap-3">
                <span className="min-w-0 text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{t(d.documentType)}</bdi></span>
                <StatusChip tone="amber" dot label={d.expiryDate ? t('Expires on {date}', { date: fx.date(d.expiryDate, 'medium') }) : t('Expiry date not set')} />
              </li>
            ))}
          </ul>
        )}
        <dl><Kv k={t('Profile complete')} v={fx.percent(dashboard.profile.profileCompletenessScore)} /></dl>
      </RecordCard>
    </div>
  );
}
