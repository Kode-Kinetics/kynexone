'use client';

/**
 * Needs your attention: a short, ordered task list above the monitoring figures. One row per
 * thing the caller can act on today, each with its count and the one link that resolves it.
 * Hidden when there is nothing to do (the strip above already carries the all-clear line).
 */

import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { Badge } from '../ui/Badge';
import type { DashboardTask } from './taskList';

export function NeedsYourAttention({ tasks, payrollUnavailable }: { tasks: DashboardTask[]; payrollUnavailable?: boolean }) {
  const t = useT();
  if (tasks.length === 0 && !payrollUnavailable) return null;
  return (
    <section aria-labelledby="your-tasks-heading" data-testid="your-tasks" className="surface p-4">
      <h2 id="your-tasks-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Needs your attention')}</h2>
      {tasks.length > 0 && (
        <ul className="mt-2 divide-y divide-slate-200 dark:divide-white/10">
          {tasks.map((task) => (
            <li key={task.id}>
              <Link href={task.to} data-task={task.id}
                className="group flex items-center gap-3 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
                <Badge tone={task.tone === 'critical' ? 'danger' : 'warning'}>{task.tone === 'critical' ? t('Critical') : t('To review')}</Badge>
                <span className="min-w-0 flex-1">
                  <span className="block overflow-hidden text-ellipsis whitespace-nowrap text-sm font-medium text-slate-900 dark:text-white">{t(task.title, { count: task.count })}</span>
                  <span className="block overflow-hidden text-ellipsis whitespace-nowrap text-xs text-slate-600 dark:text-slate-400">{t(task.detail)}</span>
                </span>
                <span className="inline-flex shrink-0 items-center gap-1 text-sm font-semibold text-sapphire group-hover:underline dark:text-blue-300">
                  {t(task.cta)} <ArrowRight className="h-3.5 w-3.5 rtl:-scale-x-100" aria-hidden="true" />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      {payrollUnavailable && <p role="status" className="mt-2 text-xs text-slate-600 dark:text-slate-400">{t('Payroll validation could not be checked just now, so its errors are not listed.')}</p>}
    </section>
  );
}
