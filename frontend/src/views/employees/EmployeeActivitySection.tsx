'use client';

import Link from 'next/link';
import { Clock3 } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { DevelopmentPlanPanel } from '../../components/succession/DevelopmentPlanPanel';
import { useSuccessionAccess } from '../../components/succession/SuccessionParts';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { SUCCESSION_FLAG } from '../../lib/succession';

export function EmployeeActivitySection({ employee, panel, returnTo }: { employee: EmployeeDetail; panel?: string; returnTo: string }) {
  const access = useSuccessionAccess();
  const { isFeatureEnabled } = useFeatureFlags();
  if (panel === 'development' && isFeatureEnabled(SUCCESSION_FLAG) && access.devplansRead) return <DevelopmentPlanPanel key={employee.id} employeeId={employee.id} access={access} />;
  if (panel === 'development') return <section className="surface p-5"><h2 className="text-2xl font-semibold text-slate-950 dark:text-white">Activity</h2><div className="mt-5 rounded-xl border border-slate-200 bg-slate-50 p-5 dark:border-white/10 dark:bg-white/[0.03]"><p className="font-semibold">This panel is not available for your current access.</p><p className="mt-1 text-sm text-slate-500">Development activity appears only when the feature and its permission are enabled.</p><Link href={employeeWorkspaceHref(employee.id, { section: 'activity', returnTo })} className="mt-4 inline-flex font-semibold text-sapphire hover:underline">View activity overview</Link></div></section>;
  return <section className="surface overflow-hidden"><header className="border-b border-slate-200 p-5 dark:border-white/10"><h2 className="text-2xl font-semibold text-slate-950 dark:text-white">Activity</h2><p className="mt-1 text-sm text-slate-500">Status history, approvals, audit events, and relevant employee changes.</p></header>{employee.history.length ? <ol className="space-y-3 p-5">{employee.history.map(item => <li key={item.id} className="flex gap-3 rounded-xl border border-slate-200 p-4 dark:border-white/10"><Clock3 className="mt-0.5 h-5 w-5 text-blue-600" /><div><p className="font-semibold">{item.eventType}</p><p className="mt-1 text-sm text-slate-500">{item.fieldName}: {item.oldValue || '—'} → {item.newValue || '—'}</p><p className="mt-1 text-xs text-slate-400">{item.createdAtUtc}</p></div></li>)}</ol> : <p className="p-5 text-sm text-slate-500">No activity has been recorded.</p>}</section>;
}
