'use client';

import { Check, Loader2, Pencil } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { msg } from '../i18n/translations';
import styles from './EmployeeCreateWizard.module.css';
import type { GradeBenefitDefault } from '../api/benefits';

export type EmployeeWizardStep = {
  label: string;
  title: string;
  description: string;
};

export const EMPLOYEE_CREATE_STEPS = [
  { label: msg('Person'), title: msg('Start with the person'), description: msg('Start with the English full name. Choose the employing company before saving; other details can follow.') },
  { label: msg('Employment'), title: msg('Place them in the organization'), description: msg('Choose their company, role and reporting line, then confirm their joining details.') },
  { label: msg('Payroll'), title: msg('Set up payroll'), description: msg('Optional for a draft. Payment and bank details are checked before payroll or activation, according to company policy.') },
  { label: msg('Salary'), title: msg('Build the salary package'), description: msg('Optional for a draft. Add the basic salary now, then include any allowances you have confirmed.') },
  { label: msg('Identity'), title: msg('Add identity documents'), description: msg('Optional for a draft. Add the records you have; the profile will show what is needed for activation and payroll.') },
  { label: msg('Review'), title: msg('Review employee details'), description: msg('Check the information below. You can edit any section before creating the employee.') },
] as const;

export const EMPLOYEE_EDIT_STEPS = [
  { label: msg('Profile'), title: msg('Review their profile'), description: msg('Check their name and contact details. Changes that require approval follow the existing approval process.') },
  { label: msg('Employment'), title: msg('Review employment details'), description: msg('Review their company, role and reporting line, then confirm their employment details.') },
  { label: msg('Payroll'), title: msg('Review payroll details'), description: msg('Check payment and bank details before saving your changes.') },
  { label: msg('Salary'), title: msg('Review salary details'), description: msg('Review the recorded salary. Changes that require approval follow the existing approval process.') },
  { label: msg('Identity'), title: msg('Review identity details'), description: msg('Review identity records and expiry dates for the employing company.') },
  { label: msg('Review'), title: msg('Review employee changes'), description: msg('Check each section before saving. Changes that require approval follow the existing approval process.') },
] as const;

export const EMPLOYEE_VIEW_STEPS: readonly EmployeeWizardStep[] = EMPLOYEE_EDIT_STEPS.map((step) => ({
  ...step,
  title: step.label === 'Review' ? msg('Review employee details') : step.title,
  description: msg('Browse the saved information for this section.'),
}));

export function EmployeeCreateProgress({ step, onStepChange, busy = false, steps = EMPLOYEE_CREATE_STEPS, visibleSteps, visitedSteps, allowAll = false }: {
  step: number;
  onStepChange: (step: number) => void;
  busy?: boolean;
  steps?: readonly EmployeeWizardStep[];
  visibleSteps?: readonly number[];
  visitedSteps?: readonly number[];
  allowAll?: boolean;
}) {
  const { t } = useLocale();
  const indexes = visibleSteps ?? steps.map((_, index) => index);
  return (
    <nav className={styles.progress} aria-label={t('Employee setup progress')}>
      <p className={styles.mobileProgress} aria-live="polite" aria-atomic="true">
        <span>{t('Step {step} of {total}', { step: indexes.indexOf(step) + 1, total: indexes.length })}</span>
        <strong>{t(steps[step].label)}</strong>
      </p>
      <ol className={styles.steps} style={{ gridTemplateColumns: `repeat(${indexes.length}, minmax(0, 1fr))` }}>
        {indexes.map((index, position) => {
          const item = steps[index];
          const visited = visitedSteps ? visitedSteps.includes(index) : index < step;
          return <li key={item.label} className={styles.stepItem} data-state={index === step ? 'current' : visited ? 'complete' : 'upcoming'}>
            <button
              type="button"
              className={styles.stepButton}
              aria-current={index === step ? 'step' : undefined}
              aria-label={t('Step {step}: {label}', { step: position + 1, label: t(item.label) })}
              disabled={busy || (!allowAll && index !== step && !visited)}
              onClick={() => onStepChange(index)}
            >
              <span className={styles.stepNumber} aria-hidden="true">
                {visited && index !== step ? <Check size={15} strokeWidth={2.5} /> : position + 1}
              </span>
              <span className={styles.stepLabel}>{t(item.label)}</span>
            </button>
          </li>;
        })}
      </ol>
    </nav>
  );
}

export function EmployeeCreatePanel({ step, activeStep, children, steps = EMPLOYEE_CREATE_STEPS }: {
  step: number;
  activeStep: number;
  children: ReactNode;
  steps?: readonly EmployeeWizardStep[];
}) {
  const { t } = useLocale();
  const headingId = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const panelRef = useRef<HTMLElement>(null);
  const active = step === activeStep;
  const item = steps[step];

  useEffect(() => {
    if (!active) return;
    const frame = requestAnimationFrame(() => {
      headingRef.current?.focus({ preventScroll: true });
      // Scroll only the dialog's content region; never move the page underneath it.
      let ancestor = panelRef.current?.parentElement;
      while (ancestor && ancestor.getAttribute('role') !== 'dialog') {
        if (/(auto|scroll)/.test(window.getComputedStyle(ancestor).overflowY)) {
          ancestor.scrollTo({ top: 0, behavior: 'instant' });
          break;
        }
        ancestor = ancestor.parentElement;
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [active]);

  return (
    <section
      ref={panelRef}
      className={styles.panel}
      data-employee-step={step}
      aria-labelledby={headingId}
      hidden={!active}
      inert={!active || undefined}
    >
      <header className={styles.panelHeader}>
        <h3 ref={headingRef} id={headingId} tabIndex={-1} className={styles.title}>{t(item.title)}</h3>
        <p className={styles.description}>{t(item.description)}</p>
      </header>
      <div className={styles.fields}>{children}</div>
    </section>
  );
}

export type EmployeeCreateReviewSection = {
  title: string;
  step?: number;
  rows: Array<[string, string | number | undefined | null]>;
};

export function EmployeeCreateReview({ sections, onEdit, busy = false, readOnly = false }: {
  sections: EmployeeCreateReviewSection[];
  onEdit?: (step: number) => void;
  busy?: boolean;
  readOnly?: boolean;
}) {
  const { t } = useLocale();
  return (
    <div className={styles.review}>
      {sections.map((section, index) => (
        <section key={section.title} className={styles.reviewSection}>
          <div className={styles.reviewHeading}>
            <h4>{t(section.title)}</h4>
            {!readOnly && onEdit && (
              <button type="button" className={styles.editButton} disabled={busy} onClick={() => onEdit(section.step ?? index)} aria-label={t('Edit {section}', { section: t(section.title) })}>
                <Pencil size={13} aria-hidden="true" />
                {t('Edit')}
              </button>
            )}
          </div>
          <dl className={styles.reviewRows}>
            {section.rows.map(([label, value]) => (
              <div key={label} className={styles.reviewRow}>
                <dt>{t(label)}</dt>
                <dd>{value === undefined || value === null || value === '' ? <span className={styles.emptyValue}>{t('Not provided')}</span> : value}</dd>
              </div>
            ))}
          </dl>
        </section>
      ))}
    </div>
  );
}

export function EmployeeAdditionalDetails({ title, children }: { title: string; children: ReactNode }) {
  const { t } = useLocale();
  return <details className={styles.additionalDetails}>
    <summary>{t(title)}</summary>
    <div className={styles.additionalFields}>{children}</div>
  </details>;
}

export function EmployeeSetupChoice({ checked, onChange, disabled = false }: { checked: boolean; onChange: (value: boolean) => void; disabled?: boolean }) {
  const { t } = useLocale();
  return <label className={styles.setupChoice}>
    <input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)} disabled={disabled} />
    <span><strong>{t('Set up payroll and documents now')}</strong><span>{t('Optional. You can save a draft first and finish these sections from the employee record.')}</span></span>
  </label>;
}


export function EmployeeGradeBenefits({ gradeChosen, companyChosen, assumedDate, defaults, loading, error, onRetry }: {
  gradeChosen: boolean;
  companyChosen: boolean;
  assumedDate?: string;
  defaults: GradeBenefitDefault[];
  loading: boolean;
  error: string | null;
  onRetry: () => void;
}) {
  const { t } = useLocale();
  const format = useFormat();
  const ready = gradeChosen && companyChosen;
  return <section data-testid="employee-grade-benefits" aria-label={t('Grade benefits')} aria-live="polite" className="col-span-full rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs dark:border-white/10 dark:bg-white/[0.04]">
    <h4 className="font-semibold text-slate-800 dark:text-white">{t('Grade benefits')}</h4>
    {!ready ? <p className="mt-1 text-slate-500 dark:text-slate-400">{t('Choose a grade and company to see the default benefits.')}</p>
      : loading ? <p className="mt-2 flex items-center gap-2 text-slate-500 dark:text-slate-400"><Loader2 size={14} className="animate-spin" />{t('Loading grade benefits…')}</p>
      : error ? <div className="mt-2 text-rose-700 dark:text-rose-300"><p role="alert">{error}</p><button type="button" className="mt-1 font-semibold underline" onClick={onRetry}>{t('Retry')}</button></div>
      : <>
        <p className="mt-1 text-slate-500 dark:text-slate-400">{t('Eligible benefits are assigned when the employee is created. Authorized HR can record an exception later.')}</p>
        {assumedDate && <p className="mt-1 text-slate-500 dark:text-slate-400">{t('Joining date is not set. This preview uses {date}.', { date: assumedDate })}</p>}
        {defaults.length === 0 ? <p className="mt-2 text-slate-600 dark:text-slate-300">{t('No benefit plans are configured for this grade and joining date.')}</p>
          : <ul className="mt-2 divide-y divide-slate-200 dark:divide-white/10">{defaults.map(item => <li key={item.benefitPlanId} className="flex flex-wrap justify-between gap-2 py-2">
            <div className="min-w-0"><p className="font-semibold text-slate-800 dark:text-slate-100">{item.name}</p>
              <p className="mt-0.5 text-slate-600 dark:text-slate-300">{item.entitlementTier || t('Standard tier')}{item.maximumBenefitAmount !== null ? ` · ${item.currency} ${format.number(item.maximumBenefitAmount)}` : ''}{item.limitPeriod ? ` · ${t(item.limitPeriod.replace(/([A-Z])/g, ' $1').trim())}` : ''}</p>
              <p className="mt-1 text-slate-500 dark:text-slate-400">{t('Starts on {date}', { date: item.effectiveFrom })}{item.effectiveTo ? ` · ${t('Ends on {date}', { date: item.effectiveTo })}` : ''}</p>
              {!item.eligible && <p className="mt-1 text-amber-800 dark:text-amber-300">{item.blockingReason}</p>}
            </div>
            <span className={`self-start rounded-full px-2 py-0.5 font-semibold ${item.eligible ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300' : 'bg-amber-100 text-amber-800 dark:bg-amber-500/10 dark:text-amber-300'}`}>{t(item.eligible ? 'Assigned by default' : 'Not yet eligible')}</span>
          </li>)}</ul>}
      </>}
  </section>;
}
