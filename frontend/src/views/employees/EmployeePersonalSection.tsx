'use client';

import Link from 'next/link';
import type { EmployeeDetail } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import { useEmployeeReferenceLists } from '../../hooks/useReferenceLists';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { referenceLabel } from '../../lib/referenceLists';

function Field({ label, value }: { label: string; value?: string }) {
  return <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10"><dt className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</dt><dd className="mt-1 font-semibold text-slate-900 dark:text-white">{value || 'Not provided'}</dd></div>;
}

export function EmployeePersonalSection({ employee, canWrite, returnTo }: { employee: EmployeeDetail; canWrite: boolean; returnTo: string }) {
  const { locale } = useLocale();
  // Stored as the reference-list code (or, on older rows, the English label): shown in the reader's language.
  const { lists } = useEmployeeReferenceLists();
  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-semibold text-slate-950 dark:text-white">Personal details</h2><p className="mt-1 text-sm text-slate-500">Names, contact details, and permitted personal information.</p></div>{canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'personal', returnTo })} className="btn-secondary">Edit section</Link>}</header>
    <dl className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-3"><Field label="English name" value={employee.englishName} /><Field label="Arabic name" value={employee.arabicName} /><Field label="Preferred name" value={employee.preferredName} /><Field label="Gender" value={referenceLabel(lists.gender, employee.gender, locale)} /><Field label="Nationality" value={employee.nationality} /><Field label="Marital status" value={referenceLabel(lists.maritalStatus, employee.maritalStatus, locale)} /><Field label="Personal email" value={employee.personalEmail} /><Field label="Work email" value={employee.workEmail} /><Field label="Mobile" value={employee.phone} /></dl>
  </section>;
}
