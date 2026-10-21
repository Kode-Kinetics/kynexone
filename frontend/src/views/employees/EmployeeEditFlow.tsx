'use client';

import Link from 'next/link';
import { ArrowLeft, ArrowRight, Check, Loader2, RotateCcw } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { employeesApi, type EmployeeDetail, type EmployeeListItem } from '../../api/employees';
import {
  companiesApi,
  costCentersApi,
  DEFAULT_WORK_EMAIL_PATTERN,
  departmentsApi,
  designationsApi,
  type CompanyDto,
  type CostCenterDto,
  type DepartmentDto,
  type DesignationDto,
} from '../../api/organization';
import { CONTRACT_TYPE_OPTIONS, EMPLOYMENT_TYPE_OPTIONS, GENDER_OPTIONS, MARITAL_STATUS_OPTIONS } from '../../api/employeeFieldCatalog';
import { EMPLOYEE_REFERENCE_TYPES, useEmployeeReferenceLists, type EmployeeReferenceKey } from '../../hooks/useReferenceLists';
import { referenceCode, referenceLabel, referenceOptions } from '../../lib/referenceLists';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { employeeWorkspaceHref, type EmployeeSection } from '../../lib/peopleRoutes';
import { EMPLOYEE_EDIT_STEPS, EmployeeCreatePanel, EmployeeCreateProgress, EmployeeCreateReview } from '../../components/EmployeeCreateWizard';
import { WorkEmailField } from '../../components/employeeAccess/WorkEmailField';
import { SalaryStructureSelect } from '../../components/SalaryStructureSelect';
import { employeeEditStep, useEmployeeEditDraft } from './useEmployeeEditDraft';
import { useLiveGrades } from '../../hooks/useLiveGrades';

function stepForSection(section: EmployeeSection, panel?: string) {
  if (section === 'employment' || section === 'access-onboarding') return 1;
  if (section === 'pay-benefits') return panel === 'salary' || panel === 'package' ? 3 : 2;
  if (section === 'compliance-documents') return 4;
  return 0;
}

/** Shown when the tenant's reference list cannot be read; the stored value is then the label, as before. */
const REFERENCE_FALLBACK: Record<EmployeeReferenceKey, string[]> = {
  gender: GENDER_OPTIONS,
  maritalStatus: MARITAL_STATUS_OPTIONS,
  employmentType: EMPLOYMENT_TYPE_OPTIONS,
  contractType: CONTRACT_TYPE_OPTIONS,
};

function isReferenceKey(key: string): key is EmployeeReferenceKey {
  return Object.prototype.hasOwnProperty.call(EMPLOYEE_REFERENCE_TYPES, key);
}

function retainCurrentOption(options: string[], current?: string) {
  const value = current?.trim();
  return value && !options.includes(value) ? [value, ...options] : options;
}

export function EmployeeEditFlow({
  employee,
  section,
  panel,
  returnTo,
  onSaved,
}: {
  employee: EmployeeDetail;
  section: EmployeeSection;
  panel?: string;
  returnTo: string;
  onSaved(status: number): Promise<void> | void;
}) {
  const { hasPermission } = useAuth();
  const { t, locale } = useLocale();
  const canWrite = hasPermission('employees.write');
  const canSeeSensitive = hasPermission('employees.sensitive');
  const [company, setCompany] = useState<CompanyDto | null>(null);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [designations, setDesignations] = useState<DesignationDto[]>([]);
  // Live, not a one-shot fetch: a grade added or retired in Setup (any tab) reaches this dropdown without a reload.
  const { grades: allGrades } = useLiveGrades();
  const grades = useMemo(() => allGrades.filter((item) => item.isActive), [allGrades]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  const { lists: referenceLists } = useEmployeeReferenceLists();
  // The employee's current structure is retired / superseded / another company's: a salary change on it would be
  // refused (422), so it is blocked here with the reason. Edits that do not touch the salary package still save.
  const [structureInvalid, setStructureInvalid] = useState(false);
  const formatValue = useMemo(
    () => (key: string, value: string) => (isReferenceKey(key) ? referenceLabel(referenceLists[key], value, locale) : value),
    [referenceLists, locale],
  );
  const draft = useEmployeeEditDraft({ employee, canWrite, canSeeSensitive, initialStep: stepForSection(section, panel), onSaved, formatValue });
  const salaryBlockedByStructure = structureInvalid && draft.salaryPackageChanged;
  const viewHref = employeeWorkspaceHref(employee.id, { section, panel, returnTo });

  useEffect(() => {
    let cancelled = false;
    void Promise.allSettled([
      companiesApi.listAll(),
      departmentsApi.listAll(),
      designationsApi.listAll(),
      costCentersApi.listAll(employee.companyId),
      employeesApi.listAll(),
    ]).then((results) => {
      if (cancelled) return;
      const [companyResult, departmentResult, designationResult, costCenterResult, employeeResult] = results;
      setCompany(companyResult.status === 'fulfilled' ? companyResult.value.find((item) => item.id === employee.companyId) ?? null : null);
      setDepartments(departmentResult.status === 'fulfilled' ? departmentResult.value.filter((item) => item.isActive) : []);
      setDesignations(designationResult.status === 'fulfilled' ? designationResult.value.filter((item) => item.isActive) : []);
      setCostCenters(costCenterResult.status === 'fulfilled' ? costCenterResult.value.filter((item) => item.isActive) : []);
      setEmployees(employeeResult.status === 'fulfilled' ? employeeResult.value : []);
    });
    return () => { cancelled = true; };
  }, [employee.companyId]);

  const selectedDepartment = useMemo(() => departments.find((item) => item.nameEn === draft.form.department || item.code === draft.form.department), [departments, draft.form.department]);
  const departmentDesignations = useMemo(
    () => selectedDepartment ? designations.filter((item) => item.departmentId === selectedDepartment.id) : designations,
    [designations, selectedDepartment],
  );
  const selectedDesignation = useMemo(
    () => departmentDesignations.find((item) => item.titleEn === draft.form.designation || item.code === draft.form.designation),
    [departmentDesignations, draft.form.designation],
  );
  const availableGrades = useMemo(() => {
    if (!selectedDesignation?.gradeId) return grades;
    const grade = grades.find((item) => item.id === selectedDesignation.gradeId);
    return grade ? [grade] : grades;
  }, [grades, selectedDesignation]);
  const managerCandidates = useMemo(
    () => employees.filter((item) => item.id !== employee.id && item.status === 'Active' && (!selectedDepartment || item.department === selectedDepartment.nameEn)),
    [employee.id, employees, selectedDepartment],
  );

  const changeDepartment = (value: string) => {
    draft.setField('department', value);
    const department = departments.find((item) => item.nameEn === value || item.code === value);
    const designation = department ? designations.find((item) => item.departmentId === department.id) : undefined;
    const grade = designation?.gradeId ? grades.find((item) => item.id === designation.gradeId) : undefined;
    const costCenter = department?.costCenterId ? costCenters.find((item) => item.id === department.costCenterId) : undefined;
    draft.setField('designation', designation?.titleEn ?? '');
    draft.setField('grade', grade?.code ?? designation?.jobGrade ?? '');
    draft.setField('costCenter', costCenter?.code ?? '');
    draft.setField('managerEmployeeId', department?.managerEmployeeId?.toString() ?? '');
  };

  const changeDesignation = (value: string) => {
    draft.setField('designation', value);
    const designation = designations.find((item) => item.titleEn === value || item.code === value);
    const grade = designation?.gradeId ? grades.find((item) => item.id === designation.gradeId) : undefined;
    draft.setField('grade', grade?.code ?? designation?.jobGrade ?? '');
  };

  return (
    <main data-testid="employee-edit-flow" className="employee-workspace min-h-full bg-slate-50/80 px-4 py-5 dark:bg-slate-950/30 lg:px-6" aria-labelledby="employee-edit-heading">
      <div className="mx-auto max-w-[1400px] space-y-4">
        <header className="surface overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-4 border-b border-slate-200 p-5 dark:border-white/10">
            <div>
              <Link href={viewHref} className="inline-flex min-h-9 items-center text-sm font-semibold text-sapphire hover:underline"><ArrowLeft className="me-2 h-4 w-4 rtl:rotate-180" />{t('Open full profile')}</Link>
              <h1 id="employee-edit-heading" className="mt-2 text-2xl font-extrabold text-slate-950 dark:text-white">{t('Edit Employee — {name}', { name: employee.fullName })}</h1>
              <p className="mt-1 text-sm text-slate-500">{employee.employeeCode} · {t('Every change is reviewed before it is saved.')}</p>
            </div>
            <button type="button" onClick={draft.discard} disabled={draft.saving || draft.dirtyKeys.length === 0} className="btn-secondary disabled:opacity-40"><RotateCcw className="h-4 w-4" />{t('Revert changes')}</button>
          </div>
          <div className="p-4"><EmployeeCreateProgress steps={EMPLOYEE_EDIT_STEPS} step={draft.step} onStepChange={draft.setStep} busy={draft.saving} allowAll /></div>
        </header>

        <section className="surface p-5" data-employee-workspace-scroll>
          {!canWrite && <p className="mb-4 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600 dark:bg-white/5 dark:text-slate-300">{t('You have view-only access to this employee record.')}</p>}
          {draft.unavailableKeys.length > 0 && <p role="alert" className="mb-4 rounded-lg bg-amber-50 px-3 py-2.5 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Some edited fields are no longer available. Revert the changes or reopen the employee record.')}</p>}
          {draft.notice && <p role="alert" className="mb-4 rounded-lg bg-red-50 px-3 py-2.5 text-sm text-red-700 dark:bg-red-500/10 dark:text-red-300">{t(draft.notice)}</p>}
          <form noValidate aria-busy={draft.saving} onSubmit={event => { event.preventDefault(); if (draft.step < 5) draft.setStep(draft.step + 1); else if (!salaryBlockedByStructure) void draft.save(); }}>
            {EMPLOYEE_EDIT_STEPS.slice(0, 5).map((stepLabel, index) => (
              <EmployeeCreatePanel key={stepLabel.label} steps={EMPLOYEE_EDIT_STEPS} step={index} activeStep={draft.step}>
                {draft.fields.some(field => employeeEditStep(field) === index && field.sensitive) && <p className="mb-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Fields marked Approval are submitted for authorization before they take effect.')}</p>}
                <fieldset disabled={!canWrite || draft.saving} className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
                  {draft.fields.filter(field => !field.hidden && employeeEditStep(field) === index).map(field => {
                    if (field.key === 'workEmail') return <WorkEmailField
                      key={field.key}
                      label={t(field.label)}
                      value={draft.form[field.key] ?? ''}
                      onChange={(value) => draft.setField(field.key, value)}
                      englishName={draft.form.englishName ?? employee.englishName ?? employee.fullName}
                      arabicName={draft.form.arabicName ?? employee.arabicName}
                      companyId={employee.companyId}
                      domain={company?.emailDomain ?? ''}
                      pattern={company?.workEmailPattern || DEFAULT_WORK_EMAIL_PATTERN}
                      excludeEmployeeId={employee.id}
                      originalValue={employee.workEmail ?? ''}
                      suggest={!employee.workEmail}
                    />;
                    if (isReferenceKey(field.key)) {
                      // Stores the reference-list CODE and shows the label in the reader's language. A row saved
                      // with the English label still selects its value; untouched, it is saved unchanged.
                      const values = referenceLists[field.key];
                      const current = draft.form[field.key] ?? '';
                      const options = referenceOptions(values, locale, REFERENCE_FALLBACK[field.key], current);
                      return <label key={field.key} htmlFor={`edit-field-${field.key}`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                        <span className="flex items-center gap-1.5">{t(field.label)}{field.sensitive && <span aria-hidden="true" className="rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">{t('Approval')}</span>}{draft.changedKeys.includes(field.key) && <span aria-hidden="true" className="h-1.5 w-1.5 rounded-full bg-sapphire" title={t('Modified')} />}</span>
                        <select id={`edit-field-${field.key}`} value={referenceCode(values, current)} onChange={event => draft.setField(field.key, event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select>
                      </label>;
                    }
                    if (field.key === 'department') {
                      const options = retainCurrentOption(departments.map((item) => item.nameEn), draft.form.department);
                      return <label key={field.key} htmlFor="edit-field-department" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                        <span>{t('Department')}</span>
                        <select id="edit-field-department" value={draft.form.department ?? ''} onChange={event => changeDepartment(event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{options.map(option => <option key={option} value={option}>{option}</option>)}</select>
                      </label>;
                    }
                    if (field.key === 'designation') {
                      const options = retainCurrentOption(departmentDesignations.map((item) => item.titleEn), draft.form.designation);
                      return <label key={field.key} htmlFor="edit-field-designation" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                        <span>{t('Designation')}</span>
                        <select id="edit-field-designation" value={draft.form.designation ?? ''} onChange={event => changeDesignation(event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{options.map(option => <option key={option} value={option}>{option}</option>)}</select>
                      </label>;
                    }
                    if (field.key === 'grade') {
                      const options = retainCurrentOption(availableGrades.map((item) => item.code), draft.form.grade);
                      return <label key={field.key} htmlFor="edit-field-grade" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                        <span>{t('Grade')}</span>
                        <select id="edit-field-grade" value={draft.form.grade ?? ''} onChange={event => draft.setField('grade', event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{options.map(option => <option key={option} value={option}>{option}</option>)}</select>
                      </label>;
                    }
                    if (field.key === 'salaryBreakdown.salaryStructureCode') return <SalaryStructureSelect
                      key={field.key}
                      id="edit-field-salaryBreakdown.salaryStructureCode"
                      companyId={employee.companyId}
                      value={draft.form[field.key] ?? ''}
                      currentCode={employee.salaryBreakdown?.salaryStructureCode}
                      onChange={(value) => draft.setField(field.key, value)}
                      onInvalidSelectionChange={setStructureInvalid}
                      labelSuffix={<>{field.sensitive && <span aria-hidden="true" className="rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">{t('Approval')}</span>}{draft.changedKeys.includes(field.key) && <span aria-hidden="true" className="h-1.5 w-1.5 rounded-full bg-sapphire" title={t('Modified')} />}</>}
                    />;
                    if (field.key === 'costCenter') return <label key={field.key} htmlFor="edit-field-costCenter" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                      <span>{t('Cost center')}</span>
                      <input id="edit-field-costCenter" value={draft.form.costCenter ?? ''} readOnly className="input mt-1.5 w-full bg-slate-50 dark:bg-white/5" />
                    </label>;
                    if (field.key === 'managerEmployeeId') {
                      const current = draft.form.managerEmployeeId ?? '';
                      const options = managerCandidates.some((item) => String(item.id) === current)
                        ? managerCandidates
                        : employees.filter((item) => String(item.id) === current).concat(managerCandidates);
                      return <label key={field.key} htmlFor="edit-field-managerEmployeeId" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                        <span>{t('Line manager')}</span>
                        <select id="edit-field-managerEmployeeId" value={current} onChange={event => draft.setField('managerEmployeeId', event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{options.map(option => <option key={option.id} value={option.id}>{option.fullName} ({option.employeeCode})</option>)}</select>
                      </label>;
                    }
                    return <label key={field.key} htmlFor={`edit-field-${field.key}`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                      <span className="flex items-center gap-1.5">{t(field.label)}{field.sensitive && <span aria-hidden="true" className="rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">{t('Approval')}</span>}{draft.changedKeys.includes(field.key) && <span aria-hidden="true" className="h-1.5 w-1.5 rounded-full bg-sapphire" title={t('Modified')} />}</span>
                      {field.type === 'select' && field.options?.length ? (
                        <select id={`edit-field-${field.key}`} value={draft.form[field.key] ?? ''} onChange={event => draft.setField(field.key, event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{field.options.map(option => <option key={option} value={option}>{option}</option>)}</select>
                      ) : (
                        <input id={`edit-field-${field.key}`} type={field.type && field.type !== 'toggle' ? field.type : 'text'} step={field.type === 'number' ? 'any' : undefined} value={draft.form[field.key] ?? ''} onChange={event => draft.setField(field.key, event.target.value)} className="input mt-1.5 w-full" />
                      )}
                    </label>;
                  })}
                </fieldset>
              </EmployeeCreatePanel>
            ))}
            <EmployeeCreatePanel steps={EMPLOYEE_EDIT_STEPS} step={5} activeStep={draft.step}>
              <p className="mb-4 text-sm text-slate-600 dark:text-slate-300">{draft.changedKeys.length ? t('{count} fields changed. Review the before and after values before saving.', { count: draft.changedKeys.length }) : t('No changes yet. You can return to any section to update it.')}</p>
              {salaryBlockedByStructure && <p role="alert" className="mb-4 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{t('This salary structure is no longer valid for this employee. Choose another structure, or the default, before saving salary changes.')}</p>}
              {draft.salaryPackageChanged && <p className="mb-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Salary changes take effect on {date}, after approval.', { date: draft.salaryEffectiveDate })}</p>}
              <EmployeeCreateReview sections={draft.reviewSections} onEdit={draft.setStep} readOnly={!canWrite} busy={draft.saving} />
            </EmployeeCreatePanel>
            <footer className="sticky bottom-0 mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 bg-white/95 pt-4 backdrop-blur dark:border-white/10 dark:bg-slate-900/95">
              <Link href={viewHref} className="btn-secondary">{t('Cancel')}</Link>
              <div className="flex items-center gap-2">
                {draft.step > 0 && <button type="button" onClick={() => draft.setStep(draft.step - 1)} disabled={draft.saving} className="btn-secondary"><ArrowLeft className="h-4 w-4 rtl:rotate-180" />{t('Back')}</button>}
                {draft.step < 5 ? <button type="submit" disabled={draft.saving} className="btn-primary">{t('Next: {step}', { step: t(EMPLOYEE_EDIT_STEPS[draft.step + 1].label) })}<ArrowRight className="h-4 w-4 rtl:rotate-180" /></button>
                  : <button type="submit" disabled={draft.saving || draft.changedKeys.length === 0 || draft.unavailableKeys.length > 0 || salaryBlockedByStructure} className="btn-primary disabled:opacity-50">{draft.saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}{t(draft.saving ? 'Saving...' : 'Save changes')}</button>}
              </div>
            </footer>
          </form>
        </section>
      </div>
    </main>
  );
}
