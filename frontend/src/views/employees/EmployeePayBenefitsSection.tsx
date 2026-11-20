'use client';

import Link from 'next/link';
import { useMemo, type ReactNode } from 'react';
import type { EmployeeDetail } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { useLocale } from '../../contexts/LocaleContext';
import { useReleaseA } from '../../lib/releaseA';
import { payBenefitsPanels } from '../../lib/payBenefitsPanels';
import { money } from '../../components/entitlements/packageFormat';
import { EmployeePackagePanel } from '../../components/entitlements/EmployeePackagePanel';
import { EmployeeBenefitsPanel } from '../../components/benefits/EmployeeBenefitsPanel';
import { EmployeeDeductionsPanel } from '../../components/deductions/EmployeeDeductionsPanel';
import { SalaryAllowanceList } from '../../components/payroll/SalaryAllowanceViews';
import { useCashAllowanceCatalog } from '../../hooks/useCashAllowanceCatalog';
import type { SalaryAllowanceView } from '../../lib/salaryAllowances';

function MoneyField({ label, value }: { label: string; value?: string | number }) {
  const { t } = useLocale();
  return <div className="min-w-0 rounded-xl border border-slate-200 p-4 dark:border-white/10"><dt className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</dt><dd className="mt-1 break-all font-semibold text-slate-900 dark:text-white">{value || t('Not provided')}</dd></div>;
}

/** The sections of Pay & Benefits as links, so the package, benefits and deductions panels can be reached from the record. */
function PanelNav({ employeeId, current, panels, returnTo }: { employeeId: number; current: string; panels: ReturnType<typeof payBenefitsPanels>; returnTo: string }) {
  const { t } = useLocale();
  return <nav aria-label={t('Pay & Benefits sections')} className="flex flex-wrap gap-2 border-b border-slate-200 px-5 py-3 dark:border-white/10">
    {panels.map(p => <Link key={p.id} href={employeeWorkspaceHref(employeeId, { section: 'pay-benefits', panel: p.id, returnTo })} aria-current={current === p.id ? 'page' : undefined}
      className={current === p.id ? 'rounded-lg bg-sapphire px-3 py-1.5 text-sm font-semibold text-white' : 'rounded-lg px-3 py-1.5 text-sm font-semibold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}>{t(p.label)}</Link>)}
  </nav>;
}

export function EmployeePayBenefitsSection({ employee, panel, canWrite, returnTo }: { employee: EmployeeDetail; panel?: string; canWrite: boolean; returnTo: string }) {
  const { hasPermission } = useAuth();
  const { t, locale } = useLocale();
  const releaseA = useReleaseA();
  const canSeeSensitive = hasPermission('employees.sensitive');
  const canReadBenefits = hasPermission('employees.write');
  // The monthly cash allowances on the salary in force. The server names each line, so no catalogue access is needed; Benefits by grade is read
  // (when this reader may) only for a line that came without a name, and the code stands in last.
  const allowanceLines: SalaryAllowanceView[] = employee.salaryBreakdown?.allowances ?? [];
  const unnamed = allowanceLines.some(l => !l.nameEn?.trim() && !l.nameAr?.trim());
  const { catalog } = useCashAllowanceCatalog({ enabled: canSeeSensitive && unnamed });
  const allowanceNames = useMemo(() => new Map(catalog.status === 'ready'
    ? catalog.matrix.components.map(c => [c.code.toUpperCase(), { nameEn: c.nameEn, nameAr: c.nameAr }] as const) : []), [catalog]);
  const knownPanels = new Set(['payroll', 'salary', 'benefits', 'package', 'deductions']);
  const current = panel === 'salary' || !panel ? 'payroll' : panel;
  const panels = payBenefitsPanels({ releaseA, canReadBenefits });
  const nav = panels.length > 1 ? <PanelNav employeeId={employee.id} current={current} panels={panels} returnTo={returnTo} /> : null;
  const frame = (panelView: ReactNode) => <div className="space-y-4">{nav && <div className="surface overflow-hidden">{nav}</div>}{panelView}</div>;
  if (panel && !knownPanels.has(panel)) return frame(<section className="surface p-5"><h2 className="text-2xl font-semibold">{t('Pay & Benefits')}</h2><p className="mt-3 text-sm text-slate-500">{t('This panel is not available. The pay and benefits overview is shown instead.')}</p></section>);

  if (panel === 'benefits' && !releaseA && canReadBenefits) return frame(<EmployeeBenefitsPanel key={employee.id} employeeId={employee.id} employeeName={employee.fullName} companyId={employee.companyId ?? null} gradeId={employee.gradeId} />);
  if (panel === 'package' && releaseA) return frame(<EmployeePackagePanel key={employee.id} employee={employee} />);
  if (panel === 'deductions' && releaseA) return frame(<EmployeeDeductionsPanel key={employee.id} employee={employee} />);
  if (panel && panel !== 'payroll' && panel !== 'salary') return frame(<section className="surface p-5"><h2 className="text-2xl font-semibold">{t('Pay & Benefits')}</h2><p className="mt-3 text-sm text-slate-500">{t('This panel is not available for your current access.')}</p><Link href={employeeWorkspaceHref(employee.id, { section: 'pay-benefits', returnTo })} className="mt-4 inline-flex font-semibold text-sapphire hover:underline">{t('View pay and benefits overview')}</Link></section>);

  const currency = employee.salaryBreakdown?.currency || employee.payrollProfile?.salaryCurrency || '';
  const salary = employee.salary == null ? undefined : money(employee.salary, { locale, currency: currency || 'SAR' });
  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Pay & Benefits')}</h2><p className="mt-1 text-sm text-slate-500">{t('Payroll identity, banking, salary package, allowances, deductions, and benefits.')}</p></div>{canWrite && canSeeSensitive && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'pay-benefits', panel: panel || 'payroll', returnTo })} className="btn-secondary">{t('Edit pay details')}</Link>}</header>
    {nav}
    {!canSeeSensitive ? <div className="m-5 rounded-xl border border-amber-200 bg-amber-50 p-5 text-sm font-semibold text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">{t('Sensitive payroll details are restricted.')}</div> : <dl className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-3"><MoneyField label={t('Payroll profile')} value={employee.payrollProfileCode} /><MoneyField label={t('Bank')} value={employee.bankName || employee.payrollProfile?.bankName} /><MoneyField label={t('IBAN')} value={employee.bankIban || employee.payrollProfile?.iban} /><MoneyField label={t('Salary')} value={salary} /><MoneyField label={t('Currency')} value={currency} /><MoneyField label={t('Payroll group')} value={employee.payrollProfile?.payrollGroup} /></dl>}
    {canSeeSensitive && <SalaryAllowanceList lines={allowanceLines} names={allowanceNames} currency={currency} basic={employee.salaryBreakdown?.basicSalary} pending={employee.salaryApprovalState === 'Pending'} />}
  </section>;
}
