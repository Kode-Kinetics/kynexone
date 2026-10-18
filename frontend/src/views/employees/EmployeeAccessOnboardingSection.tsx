'use client';

import Link from 'next/link';
import { KeyRound, Mail, ShieldCheck } from 'lucide-react';
import type { EmployeeDetail, EmployeeReadiness } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

export function EmployeeAccessOnboardingSection({ employee, readiness, returnTo }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string }) {
  const { hasPermission } = useAuth();
  const canWrite = hasPermission('employees.write');
  const canIssue = hasPermission('employees.access.issue');
  return <div className="space-y-5"><section className="surface overflow-hidden"><header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Access &amp; Onboarding</h2><p className="mt-1 text-sm text-slate-500">Work email, sign-in access, employee completion, readiness, and activation.</p></div>{canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'access-onboarding', returnTo })} className="btn-secondary">Edit onboarding</Link>}</header><div className="grid gap-4 p-5 md:grid-cols-2"><div className="rounded-xl border border-slate-200 p-5 dark:border-white/10"><Mail className="h-5 w-5 text-blue-600" /><h3 className="mt-3 font-extrabold">Work email</h3><p className="mt-1 text-sm text-slate-500">{employee.workEmail || 'No work email has been recorded.'}</p></div><div className="rounded-xl border border-slate-200 p-5 dark:border-white/10"><KeyRound className="h-5 w-5 text-violet-600" /><h3 className="mt-3 font-extrabold">Self-service access</h3><p className="mt-1 text-sm text-slate-500">{canIssue ? 'Access issuance is available to authorized operators.' : 'You do not have authority to issue sign-in access.'}</p></div></div></section><section className="surface p-5"><div className="flex items-center gap-3"><ShieldCheck className="h-5 w-5 text-emerald-600" /><h2 className="text-lg font-extrabold">Onboarding readiness</h2></div><p className="mt-3 text-sm text-slate-500">{readiness ? `${readiness.progress.present} of ${readiness.progress.requiredTotal} required items are complete.` : 'Readiness is currently unavailable.'}</p></section></div>;
}
