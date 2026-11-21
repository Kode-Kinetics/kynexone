'use client';

import Link from 'next/link';
import type { EmployeeDetail } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import { useEmployeeReferenceLists } from '../../hooks/useReferenceLists';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { referenceLabel } from '../../lib/referenceLists';
import { RecordField, RecordFieldGrid } from '../../components/employee/RecordField';
import { RecordSection } from '../../components/employee/RecordSection';

export function EmployeePersonalSection({ employee, canWrite, returnTo }: { employee: EmployeeDetail; canWrite: boolean; returnTo: string }) {
  const { t, locale } = useLocale();
  // Stored as the reference-list code (or, on older rows, the English label): shown in the reader's language.
  const { lists } = useEmployeeReferenceLists();
  return <RecordSection
    title={t('Personal details')}
    description={t('Names, contact details, and permitted personal information.')}
    actions={canWrite && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'personal', returnTo })} className="btn-secondary">{t('Edit section')}</Link>}
  >
    <RecordFieldGrid>
      <RecordField label={t('English name')} value={employee.englishName} />
      <RecordField label={t('Arabic name')} value={employee.arabicName} />
      <RecordField label={t('Preferred name')} value={employee.preferredName} />
      <RecordField label={t('Gender')} value={referenceLabel(lists.gender, employee.gender, locale)} />
      <RecordField label={t('Nationality')} value={employee.nationality} />
      <RecordField label={t('Marital status')} value={referenceLabel(lists.maritalStatus, employee.maritalStatus, locale)} />
      <RecordField label={t('Personal email')} value={employee.personalEmail} code />
      <RecordField label={t('Work email')} value={employee.workEmail} code />
      <RecordField label={t('Mobile')} value={employee.phone} code />
    </RecordFieldGrid>
  </RecordSection>;
}
