'use client';

import Link from 'next/link';
import { Info } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useEmployeeReferenceLists } from '../../hooks/useReferenceLists';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { referenceLabel } from '../../lib/referenceLists';
import { useFormat } from '../../hooks/useFormat';
import { recordDate } from '../../lib/recordFormat';
import { RecordField, RecordFieldGrid } from '../../components/employee/RecordField';
import { RecordCallout, RecordSection } from '../../components/employee/RecordSection';
import { EmployeeCostCentreField } from '../../components/employees/EmployeeCostCentreField';
import { EmployeeTransfersPanel } from './EmployeeTransfersPanel';

export function EmployeeEmploymentSection({ employee, panel, canWrite, returnTo, onEmployeeChanged }: { employee: EmployeeDetail; panel?: string; canWrite: boolean; returnTo: string; onEmployeeChanged?(): void }) {
  const { companies } = useCompany();
  const { t, locale } = useLocale();
  const fx = useFormat();
  // Stored as the reference-list code (or, on older rows, the English label): shown in the reader's language.
  const { lists } = useEmployeeReferenceLists();
  const companyName = companies.find(company => company.id === employee.companyId)?.name ?? employee.companyId;
  if (panel === 'transfers') return <EmployeeTransfersPanel employeeId={employee.id} onEmployeeChanged={onEmployeeChanged} />;
  return <RecordSection
    title={t('Employment details')}
    description={t('The employee’s company, role, reporting line, and contract information.')}
    actions={canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'employment', returnTo })} className="btn-secondary">{t('Edit section')}</Link>}
  >
    <RecordFieldGrid>
      <RecordField label={t('Company')} value={companyName} />
      <RecordField label={t('Branch')} value={employee.branch} />
      <RecordField label={t('Department')} value={employee.department} />
      <RecordField label={t('Designation')} value={employee.designation} />
      <RecordField label={t('Grade')} value={employee.grade} />
      <RecordField label={t('Line manager')} value={employee.managerEmployeeId ? `${t('Employee')} #${employee.managerEmployeeId}` : undefined} />
      <RecordField label={t('Joining date')} value={employee.joiningDate ? recordDate(fx, employee.joiningDate) : undefined} />
      <RecordField label={t('Work location')} value={employee.workLocation} />
      <RecordField label={t('Employment type')} value={referenceLabel(lists.employmentType, employee.employmentType, locale)} />
      <RecordField label={t('Contract type')} value={referenceLabel(lists.contractType, employee.contractType, locale)} />
    </RecordFieldGrid>
    <div className="mt-4"><EmployeeCostCentreField employee={employee} onChanged={onEmployeeChanged} /></div>
    <div className="mt-4 grid gap-3 md:grid-cols-2">
      <RecordCallout title={t('What happens next?')} icon={<Info className="h-4 w-4 text-blue-600" aria-hidden="true" />}>{t('Changes that require approval are sent to the Approval Center. Other reviewed changes apply to the employee record after saving.')}</RecordCallout>
      <RecordCallout tone="emerald" title={t('{grade} salary band', { grade: employee.grade || t('Grade') })}>{t('Configured by compensation policy. Salary ranges remain authoritative in the grade and compensation setup.')}</RecordCallout>
    </div>
  </RecordSection>;
}
