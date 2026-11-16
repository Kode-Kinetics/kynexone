'use client';

/**
 * Stage layout: everything that needs the viewer, in ONE card. It folds the old attention chip
 * strip and the task list together (they repeated each other), grouped Critical then To review,
 * each row one link with a short verb. Nothing is invented: rows come from the same builders as
 * before (buildTasks, buildAttention), and an empty card says plainly that nothing needs attention.
 */

import Link from 'next/link';
import { AlertTriangle, ArrowRight, CheckCircle2, CircleAlert } from 'lucide-react';
import { attentionSeverityLabel, type AttentionItem } from './dashboardModel';
import type { DashboardTask } from './taskList';
import { useT } from '../../hooks/useT';

interface Row {
  key: string;
  critical: boolean;
  title: string;
  /** The longer explanation, in the tooltip and the accessible name. */
  detail: string;
  severity: string;
  to: string;
  /** Full action wording for assistive tech; the row shows its first word as the verb. */
  cta: string;
  attr: { 'data-task'?: string; 'data-attention'?: string };
}

const MAX_ROWS = 6;

export function NeedsYouCard({ tasks, attention, payrollUnavailable, findingsUnavailable }: {
  tasks: DashboardTask[]; attention: AttentionItem[]; payrollUnavailable?: boolean; findingsUnavailable?: boolean;
}) {
  const t = useT();
  const rows: Row[] = [
    ...tasks.map((x): Row => ({
      key: `task-${x.id}`, critical: x.tone === 'critical', title: t(x.title, { count: x.count }), detail: t(x.detail),
      severity: x.tone === 'critical' ? t('Critical') : t('To review'), to: x.to, cta: t(x.cta), attr: { 'data-task': x.id },
    })),
    // The same fact is in both lists for documents; the task row wins so it is shown and counted once.
    ...attention.filter((x) => !tasks.some((k) => k.id === x.id)).map((x): Row => ({
      key: `att-${x.id}`, critical: x.severity === 'critical', title: x.title, detail: `${x.detail} (${x.source})`,
      severity: attentionSeverityLabel(t, x.severity), to: x.to, cta: x.cta, attr: { 'data-attention': x.id },
    })),
  ];
  // Critical rows are never the ones left behind the "+N more" line.
  const ordered = [...rows.filter((r) => r.critical), ...rows.filter((r) => !r.critical)];
  const shown = ordered.slice(0, Math.max(MAX_ROWS, ordered.filter((r) => r.critical).length));
  const groups = [
    { id: 'critical', label: t('Critical'), items: shown.filter((r) => r.critical) },
    { id: 'review', label: t('To review'), items: shown.filter((r) => !r.critical) },
  ].filter((g) => g.items.length > 0);

  return (
    <section aria-labelledby="your-tasks-heading" data-testid="your-tasks" className="wg-card flex shrink-0 flex-col gap-2 p-4">
      <header className="flex items-center justify-between gap-2">
        <h2 id="your-tasks-heading" className="text-body font-semibold text-slate-900 dark:text-white">
          {t('Needs your attention')}
          {rows.length > 0 && <span className="ms-2 rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold tabular-nums text-slate-700 dark:bg-white/10 dark:text-slate-200">{rows.length}</span>}
        </h2>
        {findingsUnavailable && <span className="text-xs text-slate-600 dark:text-slate-400">({t('Rules check unavailable')})</span>}
      </header>

      {rows.length === 0 && (
        <p className="flex items-center gap-2 text-body-sm text-emerald-800 dark:text-emerald-300">
          <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden />
          {t('Nothing needs attention: no expired records, missing documents, pending corrections or open rule findings.')}
        </p>
      )}

      {groups.map((g) => (
        <div key={g.id}>
          <p className={`mb-0.5 text-caption font-semibold uppercase tracking-[0.06em] rtl:text-meta rtl:normal-case rtl:tracking-normal ${g.id === 'critical' ? 'text-rose-700 dark:text-rose-300' : 'text-amber-800 dark:text-amber-300'}`}>{g.label}</p>
          <ul className="divide-y divide-[color:var(--wg-line)]">
            {g.items.map((r) => (
              <li key={r.key}>
                <Link href={r.to} title={r.detail} {...r.attr}
                  className="group flex items-center gap-3 py-1.5 outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
                  {r.critical
                    ? <AlertTriangle className="h-4 w-4 shrink-0 text-rose-600 dark:text-rose-300" aria-hidden />
                    : <CircleAlert className="h-4 w-4 shrink-0 text-amber-600 dark:text-amber-300" aria-hidden />}
                  <span className="sr-only">{r.severity}: </span>
                  <span className="min-w-0 flex-1 text-body-sm font-medium text-slate-900 dark:text-white">{r.title}</span>
                  <span className="sr-only">. {r.detail}. {r.cta}</span>
                  <span aria-hidden className="inline-flex shrink-0 items-center gap-1 text-body-sm font-semibold text-sapphire group-hover:underline dark:text-blue-300">
                    {r.cta.split(' ')[0]} <ArrowRight className="h-3.5 w-3.5 rtl:-scale-x-100" />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </div>
      ))}
      {rows.length > shown.length && <p className="text-xs text-slate-600 dark:text-slate-400">{t('+{count} more', { count: rows.length - shown.length })}</p>}
      {payrollUnavailable && <p role="status" className="text-xs text-slate-600 dark:text-slate-400">{t('Payroll validation could not be checked just now, so its errors are not listed.')}</p>}
    </section>
  );
}
