'use client';

import Link from 'next/link';
import { ArrowLeft, ArrowRight, Check, Loader2, RotateCcw } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { employeeWorkspaceHref, type EmployeeSection } from '../../lib/peopleRoutes';
import { EMPLOYEE_EDIT_STEPS, EmployeeCreatePanel, EmployeeCreateProgress, EmployeeCreateReview } from '../../components/EmployeeCreateWizard';
import { employeeEditStep, useEmployeeEditDraft } from './useEmployeeEditDraft';

function stepForSection(section: EmployeeSection, panel?: string) {
  if (section === 'employment' || section === 'access-onboarding') return 1;
  if (section === 'pay-benefits') return panel === 'salary' || panel === 'package' ? 3 : 2;
  if (section === 'compliance-documents') return 4;
  return 0;
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
  const { t } = useLocale();
  const canWrite = hasPermission('employees.write');
  const canSeeSensitive = hasPermission('employees.sensitive');
  const draft = useEmployeeEditDraft({ employee, canWrite, canSeeSensitive, initialStep: stepForSection(section, panel), onSaved });
  const viewHref = employeeWorkspaceHref(employee.id, { section, panel, returnTo });

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
          <form noValidate aria-busy={draft.saving} onSubmit={event => { event.preventDefault(); if (draft.step < 5) draft.setStep(draft.step + 1); else void draft.save(); }}>
            {EMPLOYEE_EDIT_STEPS.slice(0, 5).map((stepLabel, index) => (
              <EmployeeCreatePanel key={stepLabel.label} steps={EMPLOYEE_EDIT_STEPS} step={index} activeStep={draft.step}>
                {draft.fields.some(field => employeeEditStep(field) === index && field.sensitive) && <p className="mb-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Fields marked Approval are submitted for authorization before they take effect.')}</p>}
                <fieldset disabled={!canWrite || draft.saving} className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
                  {draft.fields.filter(field => !field.hidden && employeeEditStep(field) === index).map(field => (
                    <label key={field.key} htmlFor={`edit-field-${field.key}`} className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                      <span className="flex items-center gap-1.5">{t(field.label)}{field.sensitive && <span aria-hidden="true" className="rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">{t('Approval')}</span>}{draft.changedKeys.includes(field.key) && <span aria-hidden="true" className="h-1.5 w-1.5 rounded-full bg-sapphire" title={t('Modified')} />}</span>
                      {field.type === 'select' && field.options?.length ? (
                        <select id={`edit-field-${field.key}`} value={draft.form[field.key] ?? ''} onChange={event => draft.setField(field.key, event.target.value)} className="select mt-1.5 w-full"><option value="">{t('Select')}</option>{field.options.map(option => <option key={option} value={option}>{option}</option>)}</select>
                      ) : (
                        <input id={`edit-field-${field.key}`} type={field.type && field.type !== 'toggle' ? field.type : 'text'} step={field.type === 'number' ? 'any' : undefined} value={draft.form[field.key] ?? ''} onChange={event => draft.setField(field.key, event.target.value)} className="input mt-1.5 w-full" />
                      )}
                    </label>
                  ))}
                </fieldset>
              </EmployeeCreatePanel>
            ))}
            <EmployeeCreatePanel steps={EMPLOYEE_EDIT_STEPS} step={5} activeStep={draft.step}>
              <p className="mb-4 text-sm text-slate-600 dark:text-slate-300">{draft.changedKeys.length ? t('{count} fields changed. Review the before and after values before saving.', { count: draft.changedKeys.length }) : t('No changes yet. You can return to any section to update it.')}</p>
              {draft.salaryPackageChanged && <p className="mb-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Salary changes take effect on {date}, after approval.', { date: draft.salaryEffectiveDate })}</p>}
              <EmployeeCreateReview sections={draft.reviewSections} onEdit={draft.setStep} readOnly={!canWrite} busy={draft.saving} />
            </EmployeeCreatePanel>
            <footer className="sticky bottom-0 mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 bg-white/95 pt-4 backdrop-blur dark:border-white/10 dark:bg-slate-900/95">
              <Link href={viewHref} className="btn-secondary">{t('Cancel')}</Link>
              <div className="flex items-center gap-2">
                {draft.step > 0 && <button type="button" onClick={() => draft.setStep(draft.step - 1)} disabled={draft.saving} className="btn-secondary"><ArrowLeft className="h-4 w-4 rtl:rotate-180" />{t('Back')}</button>}
                {draft.step < 5 ? <button type="submit" disabled={draft.saving} className="btn-primary">{t('Next: {step}', { step: t(EMPLOYEE_EDIT_STEPS[draft.step + 1].label) })}<ArrowRight className="h-4 w-4 rtl:rotate-180" /></button>
                  : <button type="submit" disabled={draft.saving || draft.changedKeys.length === 0 || draft.unavailableKeys.length > 0} className="btn-primary disabled:opacity-50">{draft.saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}{t(draft.saving ? 'Saving...' : 'Save changes')}</button>}
              </div>
            </footer>
          </form>
        </section>
      </div>
    </main>
  );
}
