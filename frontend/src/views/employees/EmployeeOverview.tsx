'use client';

import Link from 'next/link';
import { AlertTriangle, ArrowRight, BadgeCheck, CircleDollarSign } from 'lucide-react';
import type { EmployeeDetail, EmployeeReadiness, ReadinessItem } from '../../api/employees';
import { canActOnReadinessFix, readinessFixHref, readinessFixTarget } from '../../lib/readinessFixTarget';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { readinessItemText, readinessReasonText } from '../../lib/readinessOverviewText';
import { RecordField, RecordFieldGrid } from '../../components/employee/RecordField';
import { RecordSection } from '../../components/employee/RecordSection';

function Requirement({ item, tone }: { item: ReadinessItem; tone: 'activation' | 'pay' | 'recommendation' }) {
  const { t } = useLocale();
  const label = tone === 'activation' ? t('Blocks activation') : tone === 'pay' ? t('Required before payroll') : t('Recommended');
  return (
    <li className="flex items-start gap-3 rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.03]">
      {tone === 'pay' ? <CircleDollarSign className="mt-0.5 h-4 w-4 text-amber-600" /> : <AlertTriangle className={`mt-0.5 h-4 w-4 ${tone === 'recommendation' ? 'text-blue-500' : 'text-rose-600'}`} />}
      <div>
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{readinessItemText(t, item)}</p>
        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{readinessReasonText(t, item.reason) ?? t('Complete this requirement to keep the employee record ready.')}</p>
        <span className="mt-1.5 inline-flex rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-200">{label}</span>
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
  const fields = (
    <>
      <RecordField label={t('Department')} value={employee.department} />
      <RecordField label={t('Designation')} value={employee.designation} />
      <RecordField label={t('Work location')} value={employee.workLocation} />
      <RecordField label={t('Profile completeness')} value={`${employee.profileCompletenessScore}%`} />
    </>
  );

  // Nothing to do: one card, one line. The record summary sits under it instead of two mostly empty cards.
  if (!first) {
    return (
      <RecordSection title={t('Employee overview')}>
        <div className="flex items-center gap-2 text-emerald-800 dark:text-emerald-200">
          <BadgeCheck className="h-5 w-5 shrink-0" aria-hidden="true" /><p className="text-sm font-semibold">{t('No readiness blockers.')}</p>
        </div>
        <div className="mt-4 border-t border-slate-100 pt-4 dark:border-white/10"><RecordFieldGrid columns="four">{fields}</RecordFieldGrid></div>
      </RecordSection>
    );
  }

  return (
    <div className="space-y-4">
      <RecordSection title={t('Recommended next action')} description={t('Guidance is based on the employee’s current readiness and access state.')}>
        <div className="flex flex-col justify-between gap-3 rounded-lg border border-blue-200 bg-blue-50/70 p-3 dark:border-blue-500/30 dark:bg-blue-500/10 sm:flex-row sm:items-center">
          <div className="min-w-0">
            <p className="text-sm font-semibold text-slate-950 dark:text-white">{t('Complete {item}', { item: readinessItemText(t, first) })}</p>
            <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-300">{readinessReasonText(t, first.reason) ?? t('This is the highest-priority open requirement.')}</p>
          </div>
          {canAct
            ? <Link href={readinessFixHref(employee.id, first, returnTo)} className="btn-primary shrink-0">{t('Open section')} <ArrowRight className="h-4 w-4 rtl:rotate-180" /></Link>
            : <div className="shrink-0 sm:max-w-xs sm:text-end"><button type="button" disabled aria-describedby="employee-next-action-reason" className="btn-primary opacity-50">{t('Open section')} <ArrowRight className="h-4 w-4 rtl:rotate-180" /></button><p id="employee-next-action-reason" className="mt-1.5 text-xs text-slate-600 dark:text-slate-300">{t('You can see this requirement, but completing it needs edit access to these details. Ask an HR colleague who has it.')}</p></div>}
        </div>
      </RecordSection>

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.4fr)_minmax(280px,0.6fr)]">
        <RecordSection title={t('Open requirements')}>
          <ul className="space-y-2">{requirements.map(({ item, tone }) => <Requirement key={`${tone}-${item.key}`} item={item} tone={tone} />)}</ul>
        </RecordSection>
        <RecordSection title={t('Record summary')} className="self-start"><RecordFieldGrid columns="one">{fields}</RecordFieldGrid></RecordSection>
      </div>
    </div>
  );
}
