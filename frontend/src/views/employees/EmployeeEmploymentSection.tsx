'use client';

import Link from 'next/link';
import { Info } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useEmployeeReferenceLists } from '../../hooks/useReferenceLists';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { referenceLabel } from '../../lib/referenceLists';

function ReadField({ label, value, help }: { label: string; value?: string; help: string }) {
  return <div><dt className="mb-1.5 text-sm font-bold text-slate-800 dark:text-slate-100">{label}</dt><dd><span className="block min-h-11 rounded-lg border border-slate-200 bg-white px-3 py-2.5 font-medium text-slate-900 dark:border-white/10 dark:bg-white/[0.03] dark:text-white">{value || 'Not provided'}</span><span className="mt-1 block text-xs font-normal text-slate-500">{help}</span></dd></div>;
}

export function EmployeeEmploymentSection({ employee, panel, canWrite, returnTo }: { employee: EmployeeDetail; panel?: string; canWrite: boolean; returnTo: string }) {
  const { companies } = useCompany();
  const { locale } = useLocale();
  // Stored as the reference-list code (or, on older rows, the English label): shown in the reader's language.
  const { lists } = useEmployeeReferenceLists();
  const companyName = companies.find(company => company.id === employee.companyId)?.name ?? employee.companyId;
  if (panel === 'transfers') return <section className="surface p-5"><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Employment transfers</h2>{employee.transfers.length ? <ul className="mt-4 space-y-3">{employee.transfers.map(item => <li key={item.id} className="rounded-xl border border-slate-200 p-4 dark:border-white/10"><p className="font-bold">{item.newDepartment || item.newBranch || 'Transfer request'}</p><p className="text-sm text-slate-500">{item.status} · {item.effectiveDate}</p></li>)}</ul> : <p className="mt-4 text-sm text-slate-500">No transfer requests are recorded.</p>}</section>;
  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Employment details</h2><p className="mt-1 text-sm text-slate-500">The employee’s company, role, reporting line, and contract information.</p></div>{canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'employment', returnTo })} className="btn-secondary">Edit section</Link>}</header>
    <div className="grid gap-6 p-5 xl:grid-cols-[minmax(0,1fr)_320px]">
      <dl className="grid gap-x-6 gap-y-5 sm:grid-cols-2">
        <ReadField label="Company" value={companyName} help="The legal entity employing this person." />
        <ReadField label="Branch" value={employee.branch} help="Employee’s primary work location branch." />
        <ReadField label="Department" value={employee.department} help="Department responsible for this employee." />
        <ReadField label="Designation" value={employee.designation} help="Job designation in the organization structure." />
        <ReadField label="Grade" value={employee.grade} help="Position grade defining salary band and level." />
        <ReadField label="Line manager" value={employee.managerEmployeeId ? `Employee #${employee.managerEmployeeId}` : undefined} help="Employee’s direct reporting manager." />
        <ReadField label="Joining date" value={employee.joiningDate} help="The date the employee joined the company." />
        <ReadField label="Work location" value={employee.workLocation} help="Primary place of work for this employee." />
        <ReadField label="Employment type" value={referenceLabel(lists.employmentType, employee.employmentType, locale)} help="Nature of employment as per company policy." />
        <ReadField label="Contract type" value={referenceLabel(lists.contractType, employee.contractType, locale)} help="Type of employment contract." />
        <ReadField label="Cost center" value={employee.costCenter} help="Financial cost center assigned through the department." />
      </dl>
      <aside className="space-y-4">
        <div className="rounded-xl border border-blue-200 bg-blue-50 p-5 dark:border-blue-500/30 dark:bg-blue-500/10"><div className="flex items-center gap-3"><Info className="h-5 w-5 text-blue-600" /><h3 className="font-extrabold text-slate-950 dark:text-white">What happens next?</h3></div><p className="mt-3 text-sm leading-6 text-slate-600 dark:text-slate-300">Changes that require approval are sent to the Approval Center. Other reviewed changes apply to the employee record after saving.</p></div>
        <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-5 dark:border-emerald-500/30 dark:bg-emerald-500/10"><p className="text-sm font-bold text-slate-800 dark:text-slate-100">{employee.grade || 'Grade'} salary band</p><p className="mt-2 text-xl font-extrabold text-blue-700 dark:text-blue-300">Configured by compensation policy</p><p className="mt-2 text-sm text-slate-600 dark:text-slate-300">Salary ranges remain authoritative in the grade and compensation setup.</p></div>
      </aside>
    </div>
  </section>;
}
