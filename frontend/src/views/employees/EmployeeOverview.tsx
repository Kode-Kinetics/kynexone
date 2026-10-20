'use client';

import Link from 'next/link';
import { AlertTriangle, ArrowRight, BadgeCheck, CircleDollarSign, ClipboardCheck } from 'lucide-react';
import type { EmployeeDetail, EmployeeReadiness, ReadinessItem } from '../../api/employees';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

function Requirement({ item, tone }: { item: ReadinessItem; tone: 'activation' | 'pay' | 'recommendation' }) {
  const label = tone === 'activation' ? 'Blocks activation' : tone === 'pay' ? 'Blocks payroll' : 'Recommended';
  return (
    <li className="flex items-start gap-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/[0.03]">
      {tone === 'pay' ? <CircleDollarSign className="mt-0.5 h-5 w-5 text-amber-600" /> : <AlertTriangle className={`mt-0.5 h-5 w-5 ${tone === 'recommendation' ? 'text-blue-500' : 'text-rose-600'}`} />}
      <div>
        <p className="font-bold text-slate-900 dark:text-white">{item.label}</p>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{item.reason || 'Complete this requirement to keep the employee record ready.'}</p>
        <span className="mt-2 inline-flex rounded-full bg-slate-100 px-2.5 py-1 text-xs font-bold text-slate-600 dark:bg-white/10 dark:text-slate-200">{label}</span>
      </div>
    </li>
  );
}

export function EmployeeOverview({ employee, readiness, returnTo }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string }) {
  const first = readiness?.blocking[0] ?? readiness?.payBlocking[0] ?? readiness?.recommended[0];
  const targetSection = first?.fix?.kind === 'document' ? 'compliance-documents' : first?.category === 'pay' ? 'pay-benefits' : 'employment';
  const requirements = [
    ...(readiness?.blocking.map(item => ({ item, tone: 'activation' as const })) ?? []),
    ...(readiness?.payBlocking.map(item => ({ item, tone: 'pay' as const })) ?? []),
    ...(readiness?.recommended.map(item => ({ item, tone: 'recommendation' as const })) ?? []),
  ];

  return (
    <div className="space-y-5">
      <section className="surface overflow-hidden">
        <div className="border-b border-slate-200 p-5 dark:border-white/10">
          <p className="text-xs font-bold uppercase tracking-[0.16em] text-blue-600">Employee overview</p>
          <h2 className="mt-1 text-2xl font-extrabold text-slate-950 dark:text-white">Recommended next action</h2>
          <p className="mt-1 text-sm text-slate-500">Guidance is based on the employee’s current readiness and access state.</p>
        </div>
        <div className="p-5">
          {first ? (
            <div className="flex flex-col justify-between gap-4 rounded-xl border border-blue-200 bg-blue-50/70 p-5 dark:border-blue-500/30 dark:bg-blue-500/10 sm:flex-row sm:items-center">
              <div>
                <p className="font-bold text-slate-950 dark:text-white">Complete {first.label}</p>
                <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{first.reason || 'This is the highest-priority open requirement.'}</p>
              </div>
              <Link href={employeeWorkspaceHref(employee.id, { section: targetSection, returnTo })} className="btn-primary shrink-0">Open section <ArrowRight className="h-4 w-4 rtl:rotate-180" /></Link>
            </div>
          ) : (
            <div className="flex items-center gap-3 rounded-xl bg-emerald-50 p-5 text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200">
              <BadgeCheck className="h-6 w-6" /><p className="font-bold">No readiness blockers are currently reported.</p>
            </div>
          )}
        </div>
      </section>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.4fr)_minmax(280px,0.6fr)]">
        <section className="surface p-5">
          <h2 className="text-lg font-extrabold text-slate-950 dark:text-white">Open requirements</h2>
          {requirements.length > 0 ? <ul className="mt-4 space-y-3">{requirements.map(({ item, tone }) => <Requirement key={`${tone}-${item.key}`} item={item} tone={tone} />)}</ul> : <p className="mt-4 text-sm text-slate-500">Nothing requires attention.</p>}
        </section>
        <section className="surface p-5">
          <div className="flex items-center gap-3"><ClipboardCheck className="h-5 w-5 text-emerald-600" /><h2 className="text-lg font-extrabold text-slate-950 dark:text-white">Record summary</h2></div>
          <dl className="mt-4 space-y-3 text-sm">
            <div><dt className="text-slate-500">Department</dt><dd className="font-bold text-slate-900 dark:text-white">{employee.department || 'Not provided'}</dd></div>
            <div><dt className="text-slate-500">Designation</dt><dd className="font-bold text-slate-900 dark:text-white">{employee.designation || 'Not provided'}</dd></div>
            <div><dt className="text-slate-500">Work location</dt><dd className="font-bold text-slate-900 dark:text-white">{employee.workLocation || 'Not provided'}</dd></div>
            <div><dt className="text-slate-500">Profile completeness</dt><dd className="font-bold text-slate-900 dark:text-white">{employee.profileCompletenessScore}%</dd></div>
          </dl>
        </section>
      </div>
    </div>
  );
}
