'use client';

import Link from 'next/link';
import {
  Activity,
  BriefcaseBusiness,
  ChevronDown,
  FileCheck2,
  LayoutDashboard,
  Pencil,
  ShieldCheck,
  UserRound,
  WalletCards,
} from 'lucide-react';
import type { EmployeeDetail, EmployeeReadiness } from '../../api/employees';
import { employeeWorkspaceHref, type EmployeeSection } from '../../lib/peopleRoutes';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { EmployeeOverview } from './EmployeeOverview';
import { EmployeePersonalSection } from './EmployeePersonalSection';
import { EmployeeEmploymentSection } from './EmployeeEmploymentSection';
import { EmployeePayBenefitsSection } from './EmployeePayBenefitsSection';
import { EmployeeComplianceDocumentsSection } from './EmployeeComplianceDocumentsSection';
import { EmployeeAccessOnboardingSection } from './EmployeeAccessOnboardingSection';
import { EmployeeActivitySection } from './EmployeeActivitySection';

interface ReadinessState {
  data: EmployeeReadiness | null;
  loading: boolean;
  error: unknown;
}

interface EmployeeWorkspaceShellProps {
  employee: EmployeeDetail;
  section: EmployeeSection;
  panel?: string;
  mode: 'view' | 'edit';
  returnTo: string;
  readiness: ReadinessState;
  onRetryReadiness(): void;
}

const destinations: Array<{ id: EmployeeSection; label: string; icon: typeof LayoutDashboard }> = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard },
  { id: 'personal', label: 'Personal', icon: UserRound },
  { id: 'employment', label: 'Employment', icon: BriefcaseBusiness },
  { id: 'pay-benefits', label: 'Pay & Benefits', icon: WalletCards },
  { id: 'compliance-documents', label: 'Compliance & Documents', icon: FileCheck2 },
  { id: 'access-onboarding', label: 'Access & Onboarding', icon: ShieldCheck },
  { id: 'activity', label: 'Activity', icon: Activity },
];

function initials(name: string) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join('') || 'E';
}

export function EmployeeWorkspaceShell({
  employee,
  section,
  panel,
  mode,
  returnTo,
  readiness,
  onRetryReadiness,
}: EmployeeWorkspaceShellProps) {
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const canWrite = hasPermission('employees.write');
  const completed = readiness.data?.progress.present ?? 0;
  const required = readiness.data?.progress.requiredTotal ?? 0;
  const progress = required > 0 ? Math.min(100, Math.round((completed / required) * 100)) : 0;
  const sectionHref = (target: EmployeeSection) => employeeWorkspaceHref(employee.id, { section: target, returnTo });

  return (
    <main className="employee-workspace min-h-full bg-slate-50/80 dark:bg-slate-950/30" aria-labelledby="employee-workspace-heading">
      <div className="sticky top-0 z-20 border-b border-slate-200/80 bg-white/95 px-4 py-3 shadow-sm backdrop-blur dark:border-white/10 dark:bg-slate-950/95 lg:px-6">
        <Link href={returnTo} className="mb-3 inline-flex min-h-9 items-center text-sm font-semibold text-sapphire hover:underline">
          <span aria-hidden="true" className="me-2 rtl:rotate-180">←</span> {t('Back to People')}
        </Link>
        <div className="flex flex-wrap items-center gap-4">
          <div className="grid h-14 w-14 shrink-0 place-items-center rounded-full bg-gradient-to-br from-amber-500 to-orange-700 text-lg font-bold text-white shadow-sm">
            {initials(employee.fullName)}
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-3">
              <h1 id="employee-workspace-heading" tabIndex={-1} className="truncate text-2xl font-extrabold text-slate-950 outline-none dark:text-white">
                {employee.fullName}
              </h1>
              <span className="rounded-md bg-amber-100 px-2.5 py-1 text-xs font-bold text-amber-800 dark:bg-amber-500/15 dark:text-amber-200">{employee.status}</span>
            </div>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              {employee.employeeCode} <span aria-hidden="true">·</span> {employee.department || 'Department not set'} <span aria-hidden="true">·</span> {employee.designation || employee.jobTitle || 'Designation not set'}
            </p>
          </div>
          <div className="min-w-44 border-slate-200 ps-0 dark:border-white/10 sm:border-s sm:ps-5">
            {readiness.loading ? (
              <p role="status" className="text-sm text-slate-500">{t('Loading readiness…')}</p>
            ) : readiness.error ? (
              <div role="alert" className="space-y-2">
                <p className="text-sm font-semibold text-rose-700 dark:text-rose-300">{t('Readiness could not be loaded.')}</p>
                <button type="button" onClick={onRetryReadiness} className="btn-secondary text-sm">{t('Retry readiness')}</button>
              </div>
            ) : (
              <>
                <p className="text-sm font-bold text-slate-900 dark:text-white">{t('{complete} of {total} complete', { complete: completed, total: required })}</p>
                <div className="mt-2 h-2 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/10" aria-label="Employee readiness progress" role="progressbar" aria-valuenow={completed} aria-valuemin={0} aria-valuemax={Math.max(required, 1)}>
                  <div className="h-full rounded-full bg-blue-600 transition-[width] motion-reduce:transition-none" style={{ width: `${progress}%` }} />
                </div>
              </>
            )}
          </div>
          {canWrite && (
            <Link href={employeeWorkspaceHref(employee.id, { edit: true, mode: 'guided', section, returnTo })} className="btn-secondary min-h-11">
              <Pencil className="h-4 w-4" aria-hidden="true" /> {t('Edit full record')}
            </Link>
          )}
          <details className="group relative">
            <summary className="btn-secondary min-h-11 cursor-pointer list-none" aria-label="More employee actions">
              {t('More actions')} <ChevronDown className="h-4 w-4 transition-transform group-open:rotate-180" aria-hidden="true" />
            </summary>
            <div className="absolute end-0 top-full z-30 mt-2 w-56 rounded-xl border border-slate-200 bg-white p-2 shadow-xl dark:border-white/10 dark:bg-slate-900">
              <Link href={sectionHref('activity')} className="block rounded-lg px-3 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 dark:text-slate-200 dark:hover:bg-white/5">View activity</Link>
              {canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'employment', returnTo })} className="block rounded-lg px-3 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 dark:text-slate-200 dark:hover:bg-white/5">Edit employment</Link>}
            </div>
          </details>
        </div>
      </div>

      <div className="mx-auto grid max-w-[1500px] gap-5 px-4 py-5 lg:grid-cols-[230px_minmax(0,1fr)] lg:px-6">
        <nav aria-label={t('Employee record sections')} className="surface h-fit overflow-hidden p-2 lg:sticky lg:top-40">
          <div className="flex gap-1 overflow-x-auto lg:block lg:space-y-1">
            {destinations.map(destination => {
              const Icon = destination.icon;
              const active = destination.id === section;
              return (
                <Link
                  key={destination.id}
                  href={sectionHref(destination.id)}
                  aria-current={active ? 'page' : undefined}
                  className={`flex min-h-11 shrink-0 items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-semibold transition-colors ${active ? 'bg-blue-50 text-blue-700 shadow-sm dark:bg-blue-500/15 dark:text-blue-200' : 'text-slate-600 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-white/[0.05]'}`}
                >
                  <Icon className="h-4 w-4" aria-hidden="true" /> {t(destination.label)}
                </Link>
              );
            })}
          </div>
        </nav>

        <section data-employee-workspace-scroll className="min-w-0" aria-live="polite">
          {section === 'overview' && <EmployeeOverview employee={employee} readiness={readiness.data} returnTo={returnTo} />}
          {section === 'personal' && <EmployeePersonalSection employee={employee} canWrite={canWrite} returnTo={returnTo} />}
          {section === 'employment' && <EmployeeEmploymentSection employee={employee} panel={panel} canWrite={canWrite} returnTo={returnTo} />}
          {section === 'pay-benefits' && <EmployeePayBenefitsSection employee={employee} panel={panel} canWrite={canWrite} returnTo={returnTo} />}
          {section === 'compliance-documents' && <EmployeeComplianceDocumentsSection employee={employee} panel={panel} returnTo={returnTo} />}
          {section === 'access-onboarding' && <EmployeeAccessOnboardingSection employee={employee} readiness={readiness.data} returnTo={returnTo} />}
          {section === 'activity' && <EmployeeActivitySection employee={employee} panel={panel} returnTo={returnTo} />}
        </section>
      </div>
    </main>
  );
}
