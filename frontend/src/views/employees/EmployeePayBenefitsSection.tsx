'use client';

import Link from 'next/link';
import type { EmployeeDetail } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

function MoneyField({ label, value }: { label: string; value?: string | number }) {
  return <div className="rounded-xl border border-slate-200 p-4 dark:border-white/10"><dt className="text-xs font-bold uppercase tracking-wide text-slate-400">{label}</dt><dd className="mt-1 font-bold text-slate-900 dark:text-white">{value || 'Not provided'}</dd></div>;
}

export function EmployeePayBenefitsSection({ employee, panel, canWrite, returnTo }: { employee: EmployeeDetail; panel?: string; canWrite: boolean; returnTo: string }) {
  const { hasPermission } = useAuth();
  const canSeeSensitive = hasPermission('employees.sensitive');
  const knownPanels = new Set(['payroll', 'benefits', 'package', 'deductions']);
  if (panel && !knownPanels.has(panel)) return <section className="surface p-5"><h2 className="text-2xl font-extrabold">Pay &amp; Benefits</h2><p className="mt-3 text-sm text-slate-500">This panel is not available. The pay and benefits overview is shown instead.</p></section>;

  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Pay &amp; Benefits</h2><p className="mt-1 text-sm text-slate-500">Payroll identity, banking, salary package, allowances, deductions, and benefits.</p></div>{canWrite && canSeeSensitive && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'pay-benefits', panel: panel || 'payroll', returnTo })} className="btn-secondary">Edit pay details</Link>}</header>
    {!canSeeSensitive ? <div className="m-5 rounded-xl border border-amber-200 bg-amber-50 p-5 text-sm font-semibold text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">Sensitive payroll details are restricted.</div> : <dl className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-3"><MoneyField label="Payroll profile" value={employee.payrollProfileCode} /><MoneyField label="Bank" value={employee.bankName || employee.payrollProfile?.bankName} /><MoneyField label="IBAN" value={employee.bankIban || employee.payrollProfile?.iban} /><MoneyField label="Salary" value={employee.salary} /><MoneyField label="Currency" value={employee.salaryBreakdown?.currency || employee.payrollProfile?.salaryCurrency} /><MoneyField label="Payroll group" value={employee.payrollProfile?.payrollGroup} /></dl>}
    <div className="border-t border-slate-200 p-5 dark:border-white/10"><h3 className="font-extrabold text-slate-950 dark:text-white">{panel ? `${panel[0].toUpperCase()}${panel.slice(1)} panel` : 'Pay package overview'}</h3><p className="mt-2 text-sm text-slate-500">Existing benefit, package, and deduction workflows remain governed by their current permissions and approval state.</p></div>
  </section>;
}
