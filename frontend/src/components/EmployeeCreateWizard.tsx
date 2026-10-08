'use client';

import { Check, Pencil } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { useLocale } from '../contexts/LocaleContext';
import { msg } from '../i18n/translations';
import styles from './EmployeeCreateWizard.module.css';

export const EMPLOYEE_CREATE_STEPS = [
  { label: msg('Profile'), title: msg('Start with the person'), description: msg('Add their name and contact details. Fields marked with an asterisk are required.') },
  { label: msg('Employment'), title: msg('Place them in the organization'), description: msg('Choose their company, role and reporting line, then confirm their joining details.') },
  { label: msg('Payroll'), title: msg('Set up payroll'), description: msg('Add payment and bank details so their payroll profile is ready.') },
  { label: msg('Salary'), title: msg('Build the salary package'), description: msg('Enter the salary and allowances. The package total updates as you go.') },
  { label: msg('Identity'), title: msg('Add identity documents'), description: msg('Add identity records and expiry dates for the employing company.') },
  { label: msg('Review'), title: msg('Review employee details'), description: msg('Check the information below. You can edit any section before creating the employee.') },
] as const;

export function EmployeeCreateProgress({ step, onStepChange, busy = false }: {
  step: number;
  onStepChange: (step: number) => void;
  busy?: boolean;
}) {
  const { t } = useLocale();
  return (
    <nav className={styles.progress} aria-label={t('Employee setup progress')}>
      <p className={styles.mobileProgress} aria-live="polite" aria-atomic="true">
        <span>{t('Step {step} of {total}', { step: step + 1, total: EMPLOYEE_CREATE_STEPS.length })}</span>
        <strong>{t(EMPLOYEE_CREATE_STEPS[step].label)}</strong>
      </p>
      <ol className={styles.steps}>
        {EMPLOYEE_CREATE_STEPS.map((item, index) => (
          <li key={item.label} className={styles.stepItem} data-state={index < step ? 'complete' : index === step ? 'current' : 'upcoming'}>
            <button
              type="button"
              className={styles.stepButton}
              aria-current={index === step ? 'step' : undefined}
              aria-label={index < step
                ? t('Step {step}: {label}, completed', { step: index + 1, label: t(item.label) })
                : t('Step {step}: {label}', { step: index + 1, label: t(item.label) })}
              disabled={busy || index > step}
              onClick={() => onStepChange(index)}
            >
              <span className={styles.stepNumber} aria-hidden="true">
                {index < step ? <Check size={15} strokeWidth={2.5} /> : index + 1}
              </span>
              <span className={styles.stepLabel}>{t(item.label)}</span>
            </button>
          </li>
        ))}
      </ol>
    </nav>
  );
}

export function EmployeeCreatePanel({ step, activeStep, children }: {
  step: number;
  activeStep: number;
  children: ReactNode;
}) {
  const { t } = useLocale();
  const headingId = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const panelRef = useRef<HTMLElement>(null);
  const active = step === activeStep;
  const item = EMPLOYEE_CREATE_STEPS[step];

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
  rows: Array<[string, string | number | undefined | null]>;
};

export function EmployeeCreateReview({ sections, onEdit, busy = false }: {
  sections: EmployeeCreateReviewSection[];
  onEdit: (step: number) => void;
  busy?: boolean;
}) {
  const { t } = useLocale();
  return (
    <div className={styles.review}>
      {sections.map((section, index) => (
        <section key={section.title} className={styles.reviewSection}>
          <div className={styles.reviewHeading}>
            <h4>{t(section.title)}</h4>
            <button type="button" className={styles.editButton} disabled={busy} onClick={() => onEdit(index)} aria-label={t('Edit {section}', { section: t(section.title) })}>
              <Pencil size={13} aria-hidden="true" />
              {t('Edit')}
            </button>
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
