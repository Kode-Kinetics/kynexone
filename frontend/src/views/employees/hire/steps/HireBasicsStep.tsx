'use client';

import { useLocale } from '@/src/contexts/LocaleContext';
import { FieldShell, inputClass } from '../fields/FieldShell';
import { DateField } from '../fields/DateField';
import type { StepProps } from '../types';
import { TemplatePicker, type AppliedTemplate } from '../TemplatePicker';
import { useHireTemplate } from '../useHireTemplate';
import type { HireTemplate } from '@/src/api/onboardingTemplates';
import { CostCentreSelect } from '@/src/components/employees/CostCentreSelect';
import { Lock } from 'lucide-react';

export function HireBasicsStep({ form, set, errorFor, blur, lookups, singleCompany, costCentre, onNameBlur, onCompanyChange, applied, onApplyTemplate, onUndoTemplate }: StepProps & {
  onNameBlur: () => void;
  onCompanyChange: (companyId: string) => void;
  applied?: AppliedTemplate | null;
  onApplyTemplate?: (template: HireTemplate) => void;
  onUndoTemplate?: () => void;
}) {
  const { t, locale } = useLocale();
  const choices = useHireTemplate(form.companyId, form.designationId, form.gradeId, form.branchId);
  const ar = locale === 'ar';
  const inCompany = <T extends { companyId?: string | null }>(rows: T[]) => rows.filter(r => !r.companyId || (!!form.companyId && r.companyId === form.companyId));
  const designations = inCompany(lookups.designations.filter(d => d.isActive));
  const departments = inCompany(lookups.departments.filter(d => d.isActive));
  const branches = lookups.branches.filter(b => b.isActive && !!form.companyId && b.companyId === form.companyId);
  // OD8: any active grade of the employing company (or a shared one) can be chosen, whatever the designation's own grade is.
  const grades = inCompany(lookups.grades.filter(g => g.isActive));
  const designationGrade = lookups.designations.find(d => d.id === form.designationId)?.gradeId;
  const gradeDiffers = !!designationGrade && !!form.gradeId && form.gradeId !== designationGrade;
  const select = 'input w-full min-w-0 aria-[invalid=true]:border-red-500';

  const onDesignation = (id: string) => {
    set('designationId', id);
    blur('designationId');
    const grade = lookups.designations.find(d => d.id === id)?.gradeId;
    if (grade && !form.gradeId) set('gradeId', grade);
  };

  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <FieldShell field="englishName" label={t('English full name')} required error={errorFor('englishName')} className="sm:col-span-2">
        {p => <input {...p} dir="ltr" value={form.englishName} autoComplete="off" onChange={e => set('englishName', e.target.value)} onBlur={() => { blur('englishName'); onNameBlur(); }} className={`${inputClass} text-start`} />}
      </FieldShell>
      <FieldShell field="companyId" label={t('Company')} required error={errorFor('companyId')} hint={singleCompany ? t('Your account has one company, so it is already chosen.') : undefined}>
        {p => (
          <select {...p} value={form.companyId} disabled={singleCompany} onChange={e => onCompanyChange(e.target.value)} onBlur={() => blur('companyId')} className={select}>
            {!singleCompany && <option value="">{t('Choose a company')}</option>}
            {lookups.companies.filter(c => c.isActive).map(c => <option key={c.id} value={c.id}>{ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn}</option>)}
          </select>
        )}
      </FieldShell>
      {onApplyTemplate && onUndoTemplate && <TemplatePicker choices={choices} applied={applied ?? null} onApply={onApplyTemplate} onUndo={onUndoTemplate} />}
      <DateField field="startDate" label={t('Start date')} hint={form.startDate ? undefined : t('Needed before activation')} value={form.startDate} onChange={v => set('startDate', v)} onBlur={() => blur('startDate')} error={errorFor('startDate')} />
      <FieldShell field="designationId" label={t('Designation')} error={errorFor('designationId')} hint={form.designationId || form.gradeId ? t('A designation brings its own grade.') : t('Needed before activation: choose a designation or a grade.')}>
        {p => (
          <select {...p} value={form.designationId} onChange={e => onDesignation(e.target.value)} onBlur={() => blur('designationId')} className={select}>
            <option value="">{t('Not chosen')}</option>
            {designations.map(d => <option key={d.id} value={d.id}>{ar && d.titleAr ? d.titleAr : d.titleEn}</option>)}
          </select>
        )}
      </FieldShell>
      <FieldShell field="gradeId" label={t('Grade')} error={errorFor('gradeId')} hint={gradeDiffers ? t('This grade differs from the designation’s grade. You will be asked for a reason.') : undefined}>
        {p => (
          <select {...p} value={form.gradeId} onChange={e => { set('gradeId', e.target.value); blur('gradeId'); }} onBlur={() => blur('gradeId')} className={select}>
            <option value="">{t('Not chosen')}</option>
            {grades.map(g => <option key={g.id} value={g.id}>{`${g.code} - ${ar && g.nameAr ? g.nameAr : g.name}`}</option>)}
          </select>
        )}
      </FieldShell>
      <FieldShell field="branchId" label={t('Branch')}>
        {p => (
          <select {...p} value={form.branchId} onChange={e => set('branchId', e.target.value)} className={select}>
            <option value="">{t('Not chosen')}</option>
            {branches.map(b => <option key={b.id} value={b.id}>{ar && b.nameAr ? b.nameAr : b.nameEn}</option>)}
          </select>
        )}
      </FieldShell>
      <FieldShell field="departmentId" label={t('Department')}>
        {p => (
          <select {...p} value={form.departmentId} onChange={e => { set('departmentId', e.target.value); set('costCenterId', ''); set('costCenterReason', ''); }} className={select}>
            <option value="">{t('Not chosen')}</option>
            {departments.map(d => <option key={d.id} value={d.id}>{ar && d.nameAr ? d.nameAr : d.nameEn}</option>)}
          </select>
        )}
      </FieldShell>
      {costCentre && (costCentre.canManage ? (
        <>
          <FieldShell field="costCenterId" label={t('Cost center')} error={errorFor('costCenterId')}>
            {p => (
              <CostCentreSelect
                id={p.id}
                testId="hire-cost-centre"
                value={form.costCenterId}
                onChange={v => { set('costCenterId', v); if (!v) set('costCenterReason', ''); blur('costCenterId'); }}
                options={costCentre.options}
                followPending={costCentre.followPending}
                describedBy={p['aria-describedby']}
                invalid={!!p['aria-invalid']}
                className={select}
              />
            )}
          </FieldShell>
          {costCentre.reasonDue && (
            <FieldShell field="costCenterReason" label={t('Reason')} required error={errorFor('costCenterReason')} hint={form.costCenterId ? t('Why this person has a different cost centre. Recorded in the audit log.') : t('Why this person goes back to the department’s cost centre. Recorded in the audit log.')}>
              {p => <input {...p} value={form.costCenterReason} maxLength={500} onChange={e => set('costCenterReason', e.target.value)} onBlur={() => blur('costCenterReason')} className={inputClass} />}
            </FieldShell>
          )}
          {costCentre.loadFailed && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400 sm:col-span-2">{t('The cost centres could not be loaded.')} <button type="button" onClick={costCentre.reload} className="font-semibold underline">{t('Retry')}</button></p>}
        </>
      ) : (
        <div className="min-w-0" data-testid="hire-cost-centre-readonly">
          <p className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Cost center')}</p>
          <p className="mt-1.5 rounded-lg bg-slate-50 px-3 py-2.5 text-sm text-slate-700 dark:bg-white/5 dark:text-slate-300"><bdi dir="auto">{costCentre.label}</bdi></p>
          <p role="note" className="mt-1 flex items-start gap-1.5 text-xs text-slate-500"><Lock className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />{t('Only HR managers and admins can change this.')}</p>
        </div>
      ))}
      <FieldShell field="managerId" label={t('Line manager')} hint={t('Only active employees you can assign are listed.')}>
        {p => (
          <select {...p} value={form.managerId} onChange={e => set('managerId', e.target.value)} className={select}>
            <option value="">{t('Not chosen')}</option>
            {lookups.managers.map(m => <option key={m.id} value={String(m.id)}>{`${m.fullName} (${m.employeeCode})`}</option>)}
          </select>
        )}
      </FieldShell>
      <div className="min-w-0">
        <FieldShell field="employeeCode" label={t('Employee code')} hint={form.manualCode ? undefined : t('Assigned automatically')} error={errorFor('employeeCode')}>
          {p => <input {...p} dir="ltr" readOnly={!form.manualCode} value={form.manualCode ? form.employeeCode : ''} onChange={e => set('employeeCode', e.target.value)} className={`${inputClass} text-start read-only:bg-slate-100 dark:read-only:bg-white/5`} />}
        </FieldShell>
        {!form.manualCode && <button type="button" className="mt-1 text-xs font-medium text-sapphire underline" onClick={() => set('manualCode', true)}>{t('Set manually')}</button>}
      </div>
      <FieldShell field="workEmail" label={t('Work email')} error={errorFor('workEmail')}
        hint={t('The employee signs in to Self-Service with this address. Enter the full address IT issued, for example name@company.com.')}>
        {p => <input {...p} type="email" inputMode="email" dir="ltr" autoComplete="off" value={form.workEmail} onChange={e => set('workEmail', e.target.value)} onBlur={() => blur('workEmail')} className={`${inputClass} text-start`} />}
      </FieldShell>
    </div>
  );
}
