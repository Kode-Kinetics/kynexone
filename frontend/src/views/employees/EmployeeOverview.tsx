'use client';

import Link from 'next/link';
import { AlertTriangle, ArrowRight, BadgeCheck, CircleDollarSign, ClipboardCheck } from 'lucide-react';
import type { EmployeeDetail, EmployeeReadiness, ReadinessItem } from '../../api/employees';
import { canActOnReadinessFix, readinessFixHref, readinessFixTarget } from '../../lib/readinessFixTarget';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { readinessItemText, readinessReasonText } from '../../lib/readinessOverviewText';

function Requirement({ item, tone }: { item: ReadinessItem; tone: 'activation' | 'pay' | 'recommendation' }) {
  const { t } = useLocale();
  const label = tone === 'activation' ? t('Blocks activation') : tone === 'pay' ? t('Required before payroll') : t('Recommended');
  return (
    <li className="flex items-start gap-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/[0.03]">
      {tone === 'pay' ? <CircleDollarSign className="mt-0.5 h-5 w-5 text-amber-600" /> : <AlertTriangle className={`mt-0.5 h-5 w-5 ${tone === 'recommendation' ? 'text-blue-500' : 'text-rose-600'}`} />}
      <div>
        <p className="font-semibold text-slate-900 dark:text-white">{readinessItemText(t, item)}</p>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{readinessReasonText(t, item.reason) ?? t('Complete this requirement to keep the employee record ready.')}</p>
        <span className="mt-2 inline-flex rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-200">{label}</span>
      </div>
    </li>
  );
}

export function EmployeeOverview({ employee, readiness, returnTo }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string }) {
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const first = readiness?.blocking[0] ?? readiness?.payBlocking[0] ?? readiness?.recommended[0];
  // A user who cannot make the fix is not sent to a permission wall: the action is shown disabled, with why.
  const canAct = first ? canActOnReadinessFix(readinessFixTarget(first), hasPermission) : false;
  const requirements = [
    ...(readiness?.blocking.map(item => ({ item, tone: 'activation' as const })) ?? []),
    ...(readiness?.payBlocking.map(item => ({ item, tone: 'pay' as const })) ?? []),
    ...(readiness?.recommended.map(item => ({ item, tone: 'recommendation' as const })) ?? []),
  ];

  return (
    <div className="space-y-5">
      <section className="surface overflow-hidden">
        <div className="border-b border-slate-200 p-5 dark:border-white/10">
          <p className="text-xs font-semibold uppercase tracking-[0.16em] text-blue-600">{t('Employee overview')}</p>
          <h2 className="mt-1 text-2xl font-semibold text-slate-950 dark:text-white">{t('Recommended next action')}</h2>
          <p className="mt-1 text-sm text-slate-500">{t('Guidance is based on the employee’s current readiness and access state.')}</p>
        </div>
        <div className="p-5">
          {first ? (
            <div className="flex flex-col justify-between gap-4 rounded-xl border border-blue-200 bg-blue-50/70 p-5 dark:border-blue-500/30 dark:bg-blue-500/10 sm:flex-row sm:items-center">
              <div>
                <p className="font-semibold text-slate-950 dark:text-white">{t('Complete {item}', { item: readinessItemText(t, first) })}</p>
                <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{readinessReasonText(t, first.reason) ?? t('This is the highest-priority open requirement.')}</p>
              </div>
              {canAct
                ? <Link href={readinessFixHref(employee.id, first, returnTo)} className="btn-primary shrink-0">{t('Open section')} <ArrowRight className="h-4 w-4 rtl:rotate-180" /></Link>
                : <div className="shrink-0 sm:max-w-xs sm:text-end"><button type="button" disabled aria-describedby="employee-next-action-reason" className="btn-primary opacity-50">{t('Open section')} <ArrowRight className="h-4 w-4 rtl:rotate-180" /></button><p id="employee-next-action-reason" className="mt-1.5 text-xs text-slate-600 dark:text-slate-300">{t('You can see this requirement, but completing it needs edit access to these details. Ask an HR colleague who has it.')}</p></div>}
            </div>
          ) : (
            <div className="flex items-center gap-3 rounded-xl bg-emerald-50 p-5 text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200">
              <BadgeCheck className="h-6 w-6" /><p className="font-semibold">{t('No readiness blockers are currently reported.')}</p>
            </div>
          )}
        </div>
      </section>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.4fr)_minmax(280px,0.6fr)]">
        <section className="surface p-5">
          <h2 className="text-lg font-semibold text-slate-950 dark:text-white">{t('Open requirements')}</h2>
          {requirements.length > 0 ? <ul className="mt-4 space-y-3">{requirements.map(({ item, tone }) => <Requirement key={`${tone}-${item.key}`} item={item} tone={tone} />)}</ul> : <p className="mt-4 text-sm text-slate-500">{t('Nothing requires attention.')}</p>}
        </section>
        <section className="surface p-5">
          <div className="flex items-center gap-3"><ClipboardCheck className="h-5 w-5 text-emerald-600" /><h2 className="text-lg font-semibold text-slate-950 dark:text-white">{t('Record summary')}</h2></div>
          <dl className="mt-4 space-y-3 text-sm">
            <div><dt className="text-slate-500">{t('Department')}</dt><dd className="font-semibold text-slate-900 dark:text-white">{employee.department || t('Not provided')}</dd></div>
            <div><dt className="text-slate-500">{t('Designation')}</dt><dd className="font-semibold text-slate-900 dark:text-white">{employee.designation || t('Not provided')}</dd></div>
            <div><dt className="text-slate-500">{t('Work location')}</dt><dd className="font-semibold text-slate-900 dark:text-white">{employee.workLocation || t('Not provided')}</dd></div>
            <div><dt className="text-slate-500">{t('Profile completeness')}</dt><dd className="font-semibold text-slate-900 dark:text-white">{employee.profileCompletenessScore}%</dd></div>
          </dl>
        </section>
      </div>
    </div>
  );
}
