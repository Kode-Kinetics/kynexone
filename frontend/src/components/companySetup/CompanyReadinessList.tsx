'use client';

import Link from 'next/link';
import { CheckCircle2, Circle, Star } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import type { ReadinessItem } from '../../api/companySetup';

/**
 * What is still to do for the new company, computed by the server. Each row opens the screen that
 * finishes it. Workspace-wide rows appear only while they are unconfigured.
 */
export function CompanyReadinessList({ items }: { items: ReadinessItem[] }) {
  const { t, locale } = useLocale();
  const label = (item: ReadinessItem) => (locale === 'ar' ? item.labelAr || item.label : item.label);
  const statusLabel = (s: ReadinessItem['status']) => s === 'done' ? t('Done') : s === 'recommended' ? t('Recommended') : t('To do');
  const section = (scope: ReadinessItem['scope'], heading: string, description: string) => {
    const rows = items.filter(i => i.scope === scope);
    if (rows.length === 0) return null;
    return (
      <section aria-label={heading} className="space-y-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{heading}</h3>
        <p className="text-xs text-slate-600 dark:text-slate-400">{description}</p>
        <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
          {rows.map(item => (
            <li key={item.key} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3" data-testid={`readiness-${item.key}`} data-status={item.status}>
              <div className="flex min-w-0 items-center gap-3">
                {item.status === 'done'
                  ? <CheckCircle2 className="h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />
                  : item.status === 'recommended' ? <Star className="h-5 w-5 shrink-0 text-amber-500" aria-hidden="true" />
                  : <Circle className="h-5 w-5 shrink-0 text-slate-400" aria-hidden="true" />}
                <div className="min-w-0">
                  <p className="text-sm font-medium text-slate-900 dark:text-white"><bdi>{label(item)}</bdi></p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{statusLabel(item.status)}</p>
                </div>
              </div>
              {item.status !== 'done' && <Link href={item.href} className="btn-secondary">{t('Open')}<span className="sr-only"> {label(item)}</span></Link>}
            </li>
          ))}
        </ul>
      </section>
    );
  };
  return (
    <div className="space-y-5" data-testid="company-readiness">
      {section('company', t('This company'), t('Finish these before the first payroll for this company.'))}
      {section('workspace', t('Applies to all companies'), t('Workspace settings that are not configured yet. They apply to every company in the group.'))}
    </div>
  );
}
