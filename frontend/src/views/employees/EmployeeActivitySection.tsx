'use client';

import Link from 'next/link';
import { Clock3 } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { DevelopmentPlanPanel } from '../../components/succession/DevelopmentPlanPanel';
import { useSuccessionAccess } from '../../components/succession/SuccessionParts';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { SUCCESSION_FLAG } from '../../lib/succession';
import { describeChange, humaniseEventName, recordDateTime } from '../../lib/recordFormat';
import { RecordSection } from '../../components/employee/RecordSection';

export function EmployeeActivitySection({ employee, panel, returnTo }: { employee: EmployeeDetail; panel?: string; returnTo: string }) {
  const access = useSuccessionAccess();
  const { isFeatureEnabled } = useFeatureFlags();
  const { t } = useLocale();
  const fx = useFormat();
  if (panel === 'development' && isFeatureEnabled(SUCCESSION_FLAG) && access.devplansRead) return <DevelopmentPlanPanel key={employee.id} employeeId={employee.id} access={access} />;
  if (panel === 'development') return <RecordSection title={t('Activity')}><div className="rounded-lg border border-slate-200 bg-slate-50 p-3 dark:border-white/10 dark:bg-white/[0.03]"><p className="text-sm font-semibold">{t('This panel is not available for your current access.')}</p><p className="mt-1 text-xs text-slate-500">{t('Development activity appears only when the feature and its permission are enabled.')}</p><Link href={employeeWorkspaceHref(employee.id, { section: 'activity', returnTo })} className="mt-3 inline-flex text-sm font-semibold text-sapphire hover:underline">{t('View activity overview')}</Link></div></RecordSection>;
  return <RecordSection bare title={t('Activity')} description={t('Status history, approvals, audit events, and relevant employee changes.')}>
    {employee.history.length ? <ol className="divide-y divide-slate-100 px-4 dark:divide-white/10">{employee.history.map(item => {
      const title = humaniseEventName(t, item.eventType);
      const change = describeChange(t, fx, item);
      const detail = [change.field, change.detail].filter(Boolean).join(' · ');
      // One line per event; the long audit text (the reason) is in the tooltip.
      return <li key={item.id} data-testid="activity-event" title={[title, detail, item.reason].filter(Boolean).join('\n')} className="flex items-center gap-3 py-2">
        <Clock3 className="h-4 w-4 shrink-0 text-blue-600" aria-hidden="true" />
        <p className="min-w-0 flex-1 overflow-hidden text-ellipsis whitespace-nowrap text-sm" dir="auto"><span className="font-semibold text-slate-900 dark:text-white">{title}</span>{detail && <span className="text-slate-500 dark:text-slate-400"> · {detail}</span>}</p>
        <time dateTime={item.createdAtUtc} className="shrink-0 text-xs tabular-nums text-slate-500 dark:text-slate-400">{recordDateTime(fx, item.createdAtUtc)}</time>
      </li>;
    })}</ol> : <p className="p-4 text-sm text-slate-500">{t('No activity has been recorded.')}</p>}
  </RecordSection>;
}
