'use client';

/**
 * Shortcuts and recent activity, carried over from the previous dashboard. The shortcuts are the
 * four jobs an HR admin starts most; the feed is the tenant's audit log (newest first), so every
 * line is something that really happened, with who did it and when. Nothing here is inferred.
 */

import Link from 'next/link';
import { BadgeDollarSign, CalendarPlus, FileCheck2, UserPlus } from 'lucide-react';
import type { ActivityFeedItem } from '../../api/dashboard';
import { useT } from '../../hooks/useT';

const SHORTCUTS = [
  { label: 'Add employee', to: '/people', icon: UserPlus, tone: 'text-blue-700 bg-blue-50 dark:text-blue-200 dark:bg-blue-500/15' },
  { label: 'Run payroll', to: '/payroll', icon: BadgeDollarSign, tone: 'text-emerald-700 bg-emerald-50 dark:text-emerald-200 dark:bg-emerald-500/15' },
  { label: 'Plan shifts', to: '/shifts', icon: CalendarPlus, tone: 'text-violet-700 bg-violet-50 dark:text-violet-200 dark:bg-violet-500/15' },
  { label: 'Check documents', to: '/compliance', icon: FileCheck2, tone: 'text-amber-800 bg-amber-50 dark:text-amber-200 dark:bg-amber-500/15' },
] as const;

const DOT: Record<string, string> = {
  Payroll: 'bg-blue-600', Leave: 'bg-emerald-600', Attendance: 'bg-amber-500', HR: 'bg-violet-600', Employee: 'bg-violet-600',
};

function humanise(action: string): string {
  const s = action.replace(/[._]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function ago(iso: string, now: number): string {
  const mins = Math.floor((now - new Date(iso).getTime()) / 60_000);
  if (mins < 2) return 'just now';
  if (mins < 60) return `${mins} min ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs} h ago`;
  const days = Math.floor(hrs / 24);
  if (days === 1) return 'yesterday';
  if (days < 7) return `${days} days ago`;
  return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
}

export function ActivityPanel({ feed }: { feed: ActivityFeedItem[] }) {
  const t = useT();
  const now = Date.now();
  const items = feed.slice(0, 6);
  return (
    <section aria-labelledby="activity-heading" className="wg-card grid min-w-0 grid-cols-1 gap-x-6 gap-y-3 p-5 xl:grid-cols-12">
      <h2 id="activity-heading" className="sr-only">{t('Shortcuts and recent activity')}</h2>
      <nav aria-label={t('Shortcuts')} className="grid grid-cols-2 gap-2 sm:grid-cols-4 xl:col-span-5 xl:grid-cols-2 xl:self-start">
        {SHORTCUTS.map(({ label, to, icon: Icon, tone }, i) => (
          <Link key={to} href={to}
            className="wg-press wg-tile3d wg-rise group flex flex-col items-start gap-2 rounded-xl border border-[color:var(--wg-line)] bg-[color:var(--wg-surface)] p-3 outline-none focus-visible:ring-2 focus-visible:ring-sapphire"
            style={{ animationDelay: `${i * 60}ms` }}>
            <span className={`flex h-8 w-8 items-center justify-center rounded-lg ${tone}`}><Icon className="h-4 w-4" aria-hidden /></span>
            <span className="text-[13px] font-semibold text-slate-900 group-hover:text-sapphire dark:text-white dark:group-hover:text-blue-300">{t(label)}</span>
          </Link>
        ))}
      </nav>

      <div className="flex min-w-0 flex-col gap-3 border-t border-[color:var(--wg-line)] pt-3 xl:col-span-7 xl:border-s xl:border-t-0 xl:ps-6 xl:pt-0">
      <div className="flex items-center justify-between gap-2">
        <span className="text-[14px] font-semibold text-slate-900 dark:text-white">{t('Recent activity')}</span>
        <span className="inline-flex items-center gap-1.5 text-xs text-slate-600 dark:text-slate-400">
          <span aria-hidden className="relative flex h-2 w-2"><span className="wg-ping-twice absolute inset-0 rounded-full bg-emerald-500" /><span className="relative h-2 w-2 rounded-full bg-emerald-600" /></span>
          {t('From the audit log')}
        </span>
      </div>
      {items.length === 0 ? (
        <p className="text-[13px] text-slate-700 dark:text-slate-300">{t('No recent activity recorded.')}</p>
      ) : (
        <ol className="grid grid-cols-1 gap-x-6 gap-y-2.5 2xl:grid-cols-2">
          {items.map((it, i) => (
            <li key={`${it.occurredAt}-${i}`} className="wg-rise flex min-w-0 items-start gap-3" style={{ animationDelay: `${120 + i * 70}ms` }}>
              <span aria-hidden className={`relative mt-1.5 h-[9px] w-[9px] shrink-0 rounded-full ring-2 ring-[color:var(--wg-surface)] ${DOT[it.module] ?? 'bg-slate-500'}`} />
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="truncate text-[13px] font-medium text-slate-900 dark:text-white">{humanise(it.action)}</span>
                <span className="text-xs text-slate-600 dark:text-slate-400">{it.module} · {it.actor} · <time dateTime={it.occurredAt}>{ago(it.occurredAt, now)}</time></span>
              </span>
            </li>
          ))}
        </ol>
      )}
      </div>
    </section>
  );
}
