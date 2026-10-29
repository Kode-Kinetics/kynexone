'use client';

import { createContext, useContext, type ReactNode } from 'react';
import type { CompanySetupContext, CompanySetupDraftBody, CompanySetupRowState, OrgSection } from '../../api/companySetup';
import { useT } from '../../hooks/useT';

/**
 * What every step reads: the draft body, one way to change it, and the message (if any) for a field.
 * Field keys are dotted paths into the body (`company.registrationNumber`, `branches.0.code`), the same
 * keys validateStep returns and server problems are normalised to.
 */
export interface WizardFormValue {
  body: CompanySetupDraftBody;
  update: (change: (body: CompanySetupDraftBody) => CompanySetupDraftBody) => void;
  /** Translated blocker for this field, shown only once the field was left or Next was tried. */
  errorFor: (field: string) => string | undefined;
  context: CompanySetupContext | null;
  readOnly?: boolean;
  /** Existing mode: what the company had when the wizard opened (the prefill), so edited rows can say "Changed". */
  baseline?: CompanySetupDraftBody | null;
  /** Existing mode: per existing Organisation row (by existingId), on or off and in use or not, as the server read it. */
  rowStates?: Record<string, CompanySetupRowState>;
  /** Turns a turned-off Organisation row back on now (same endpoint as Setup) and updates its state. */
  restoreRow?: (section: OrgSection, id: string) => Promise<void>;
}

const Ctx = createContext<WizardFormValue | null>(null);
export const WizardFormProvider = Ctx.Provider;
export function useWizardForm(): WizardFormValue {
  const value = useContext(Ctx);
  if (!value) throw new Error('useWizardForm outside WizardFormProvider');
  return value;
}

export const fieldId = (field: string) => `ccw-${field.replace(/[^a-zA-Z0-9]+/g, '-')}`;

export interface FieldControlProps {
  id: string;
  'aria-invalid'?: true;
  'aria-describedby'?: string;
  'aria-required'?: true;
  'data-field': string;
}

/**
 * Label above, help under the label, the message directly under the input (form-design). The `*`
 * marks exactly the fields that block Next.
 */
export function WizardField({ field, label, required, help, className = '', children }: {
  field: string;
  label: string;
  required?: boolean;
  help?: ReactNode;
  className?: string;
  children: (control: FieldControlProps) => ReactNode;
}) {
  const t = useT();
  const { errorFor } = useWizardForm();
  const id = fieldId(field);
  const error = errorFor(field);
  const helpId = help ? `${id}-help` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  const describedBy = [helpId, errorId].filter(Boolean).join(' ') || undefined;
  return (
    <div className={`min-w-0 ${className}`}>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label}{required && <span className="text-rose-600 dark:text-rose-400" aria-hidden="true"> *</span>}
        {required && <span className="sr-only"> {t('(required)')}</span>}
      </label>
      {help && <p id={helpId} className="mb-1.5 text-xs leading-5 text-slate-500 dark:text-slate-400">{help}</p>}
      {children({ id, 'data-field': field, ...(error ? { 'aria-invalid': true as const } : {}), ...(describedBy ? { 'aria-describedby': describedBy } : {}), ...(required ? { 'aria-required': true as const } : {}) })}
      {/* One line is always reserved under the field, so a message appearing never moves the form. */}
      <p id={errorId} className="mt-1 min-h-[1.25rem] text-xs font-medium leading-5 text-rose-700 dark:text-rose-300">{error ?? ''}</p>
    </div>
  );
}

/**
 * A small status pill: existing rows are this company's own records (existing mode); changed marks an existing row
 * edited in this draft; saved marks a section written to the company by Save & next (Addendum B2).
 */
export function TagPill({ tag, label }: { tag: 'new' | 'existing' | 'changed' | 'saved'; label?: string }) {
  const t = useT();
  const base = 'inline-flex h-5 shrink-0 items-center rounded-full px-2 text-caption font-semibold leading-none rtl:text-xs';
  if (tag === 'saved') return <span className={`${base} bg-emerald-50 text-emerald-800 ring-1 ring-inset ring-emerald-600/20 dark:bg-emerald-500/15 dark:text-emerald-200`}>{label ?? t('Saved')}</span>;
  if (tag === 'existing') return <span className={`${base} bg-slate-100 text-slate-700 ring-1 ring-inset ring-slate-300 dark:bg-white/10 dark:text-slate-200 dark:ring-white/20`}>{t('Existing')}</span>;
  if (tag === 'changed') return <span className={`${base} bg-sky-50 text-sky-800 dark:bg-sky-500/15 dark:text-sky-200`}>{t('Changed')}</span>;
  return <span className={`${base} bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200`}>{t('New')}</span>;
}

export function SectionHeader({ title, description, actions, id, status }: {
  title: string; description?: string; actions?: ReactNode;
  /** Heading id: a section Save moves focus here ("Save and continue"). */
  id?: string;
  /** The section's saved state, beside its title. */
  status?: ReactNode;
}) {
  return (
    <div className="mb-3 flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        {/* The status sits inline after the heading, so the heading stays the row's first child. */}
        <h3 id={id} tabIndex={id ? -1 : undefined} className="inline scroll-mt-24 text-base font-semibold text-slate-900 outline-none dark:text-white">{title}</h3>
        {status && <span className="ms-2 inline-block align-middle">{status}</span>}
        {description && <p className="mt-0.5 max-w-2xl text-sm leading-5 text-slate-600 dark:text-slate-400">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </div>
  );
}

export function Notice({ tone, children, role }: { tone: 'warning' | 'info' | 'error' | 'success'; children: ReactNode; role?: 'alert' | 'status' }) {
  const toneClass = {
    warning: 'border-amber-300 bg-amber-50 text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100',
    info: 'border-sapphire/20 bg-sapphire/[0.04] text-slate-800 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.05] dark:text-slate-100',
    error: 'border-rose-300 bg-rose-50 text-rose-900 dark:border-rose-400/30 dark:bg-rose-500/10 dark:text-rose-100',
    success: 'border-emerald-300 bg-emerald-50 text-emerald-950 dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-100',
  }[tone];
  return <div role={role} className={`rounded-xl border px-4 py-3 text-sm leading-6 ${toneClass}`}>{children}</div>;
}
