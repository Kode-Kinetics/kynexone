'use client';

import { useLocale } from '@/src/contexts/LocaleContext';
import { FieldShell, inputClass } from '../fields/FieldShell';
import { DateField } from '../fields/DateField';
import type { StepProps } from '../types';

export function HireBasicsStep({ form, set, errorFor, blur, company, lookups, singleCompany, onNameBlur }: StepProps & { onNameBlur: () => void }) {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const inCompany = <T extends { companyId?: string | null }>(rows: T[]) => rows.filter(r => !r.companyId || !form.companyId || r.companyId === form.companyId);
  const designations = inCompany(lookups.designations.filter(d => d.isActive));
  const departments = lookups.departments.filter(d => d.isActive && (!d.companyId || !form.companyId || d.companyId === form.companyId));
  const branches = lookups.branches.filter(b => b.isActive && (!form.companyId || b.companyId === form.companyId));
  const grades = lookups.grades.filter(g => g.isActive);
  const domain = company?.emailDomain?.trim();
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
          <select {...p} value={form.companyId} disabled={singleCompany} onChange={e => set('companyId', e.target.value)} onBlur={() => blur('companyId')} className={select}>
            {!singleCompany && <option value="">{t('Choose a company')}</option>}
            {lookups.companies.filter(c => c.isActive).map(c => <option key={c.id} value={c.id}>{ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn}</option>)}
          </select>
        )}
      </FieldShell>
      <DateField field="startDate" label={t('Start date')} hint={form.startDate ? undefined : t('Needed before activation')} value={form.startDate} onChange={v => set('startDate', v)} onBlur={() => blur('startDate')} error={errorFor('startDate')} />
      <FieldShell field="designationId" label={t('Designation')} error={errorFor('designationId')} hint={form.designationId || form.gradeId ? t('A designation brings its own grade.') : t('Needed before activation: choose a designation or a grade.')}>
        {p => (
          <select {...p} value={form.designationId} onChange={e => onDesignation(e.target.value)} onBlur={() => blur('designationId')} className={select}>
            <option value="">{t('Not chosen')}</option>
            {designations.map(d => <option key={d.id} value={d.id}>{ar && d.titleAr ? d.titleAr : d.titleEn}</option>)}
          </select>
        )}
      </FieldShell>
      <FieldShell field="gradeId" label={t('Grade')} error={errorFor('gradeId')}>
        {p => (
          <select {...p} value={form.gradeId} onChange={e => { set('gradeId', e.target.value); blur('gradeId'); }} onBlur={() => blur('gradeId')} className={select}>
            <option value="">{t('Not chosen')}</option>
            {grades.map(g => <option key={g.id} value={g.id}>{`${g.code} - ${g.name}`}</option>)}
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
          <select {...p} value={form.departmentId} onChange={e => set('departmentId', e.target.value)} className={select}>
            <option value="">{t('Not chosen')}</option>
            {departments.map(d => <option key={d.id} value={d.id}>{ar && d.nameAr ? d.nameAr : d.nameEn}</option>)}
          </select>
        )}
      </FieldShell>
      <FieldShell field="managerId" label={t('Line manager')} hint={t('Only active employees of the same company are listed.')}>
        {p => (
          <select {...p} value={form.managerId} onChange={e => set('managerId', e.target.value)} className={select}>
            <option value="">{t('Not chosen')}</option>
            {lookups.managers.filter(m => { const c = (m as { companyId?: string }).companyId; return !c || !form.companyId || c === form.companyId; }).map(m => <option key={m.id} value={String(m.id)}>{`${m.fullName} (${m.employeeCode})`}</option>)}
          </select>
        )}
      </FieldShell>
      <div className="min-w-0">
        <FieldShell field="employeeCode" label={t('Employee code')} hint={form.manualCode ? undefined : t('Assigned automatically')} error={errorFor('employeeCode')}>
          {p => <input {...p} dir="ltr" readOnly={!form.manualCode} value={form.manualCode ? form.employeeCode : ''} onChange={e => set('employeeCode', e.target.value)} className={`${inputClass} text-start read-only:bg-slate-100 dark:read-only:bg-white/5`} />}
        </FieldShell>
        {!form.manualCode && <button type="button" className="mt-1 text-xs font-medium text-sapphire underline" onClick={() => set('manualCode', true)}>{t('Set manually')}</button>}
      </div>
      <FieldShell field="workEmailLocal" label={t('Work email')} error={errorFor('workEmailLocal')}
        hint={domain ? t('The company domain @{domain} is added. KynexOne never creates the mailbox.', { domain }) : t('The company has no email domain yet.')}>
        {p => (
          <div className="flex items-center gap-1" dir="ltr">
            <input {...p} value={form.workEmailLocal} onChange={e => set('workEmailLocal', e.target.value)} onBlur={() => blur('workEmailLocal')} className={`${inputClass} text-start`} />
            {domain && <span className="shrink-0 text-sm text-slate-500">@{domain}</span>}
          </div>
        )}
      </FieldShell>
    </div>
  );
}
