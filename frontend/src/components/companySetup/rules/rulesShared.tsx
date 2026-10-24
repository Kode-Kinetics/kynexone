'use client';

import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useAuth } from '../../../contexts/AuthContext';
import { useCompany } from '../../../contexts/CompanyContext';
import { useLocale } from '../../../contexts/LocaleContext';
import type { CompanySetupPlan, PlanArea } from '../../../api/companySetup';

export type RulesPartId = 'workRules' | 'leave' | 'endOfService' | 'moreSettings';
export const RULES_PARTS: readonly RulesPartId[] = ['workRules', 'leave', 'endOfService', 'moreSettings'];

/**
 * Who may use each embedded editor here: the same gates as their own screens (SetupPage), so the wizard never
 * offers an editor whose saves the server would refuse. Shared (workspace-wide) lists are not offered to a
 * company-scoped group administrator.
 */
export function useRulesAccess() {
  const { hasPermission, hasRole } = useAuth();
  const { accountType, isGroupScope } = useCompany();
  const companyScoped = accountType === 'Group' && !isGroupScope;
  return {
    companyScoped,
    canEditLeave: !companyScoped && (hasRole('Admin') || hasRole('HR Manager')) && (hasPermission('leave.read') || hasPermission('leave.write')),
    canEditCountryRules: !companyScoped && (hasPermission('organization.write') || hasPermission('organization.establishment.write')),
    canReadStatutoryRules: !companyScoped && hasPermission('payroll.rates.read') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('Auditor')),
  };
}

/**
 * Embedded editors report an open, unsaved form (their onDirtyChange). Step 5 collects the reports so it can warn
 * before the reader leaves with one open, and tell the wizard (onEditorDirtyChange).
 */
const DirtyContext = createContext<((key: string) => (dirty: boolean) => void) | null>(null);
const noop = () => undefined;

export function useDirtyReporter(key: string): (dirty: boolean) => void {
  const reporterFor = useContext(DirtyContext);
  return reporterFor ? reporterFor(key) : noop;
}

/** Collects the open-form reports by editor name; `onChange` gets the names of the editors with unsaved forms. */
export function DirtyRegistry({ onChange, children }: { onChange: (dirtyEditors: string[]) => void; children: ReactNode }) {
  const [dirtyKeys, setDirtyKeys] = useState<ReadonlySet<string>>(() => new Set());
  const reporters = useRef(new Map<string, (dirty: boolean) => void>());
  const reporterFor = useCallback((key: string) => {
    let reporter = reporters.current.get(key);
    if (!reporter) {
      reporter = (dirty: boolean) => setDirtyKeys(previous => {
        if (previous.has(key) === dirty) return previous;
        const next = new Set(previous);
        if (dirty) next.add(key); else next.delete(key);
        return next;
      });
      reporters.current.set(key, reporter);
    }
    return reporter;
  }, []);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  useEffect(() => { onChangeRef.current([...dirtyKeys]); }, [dirtyKeys]);
  return <DirtyContext.Provider value={reporterFor}>{children}</DirtyContext.Provider>;
}

/**
 * Whether the last thing the reader did was on the keyboard. Keyboard-initiated navigation is never animated
 * (motion spec M2); pointer-initiated gets the short entrance.
 */
let lastInputKeyboard = false;
let listening = false;
export function useKeyboardInitiated(): () => boolean {
  useEffect(() => {
    if (listening) return;
    listening = true;
    document.addEventListener('keydown', event => { if (!event.isComposing) lastInputKeyboard = true; }, true);
    document.addEventListener('pointerdown', () => { lastInputKeyboard = false; }, true);
  }, []);
  return useCallback(() => lastInputKeyboard, []);
}

/** What the commit will do for one area, from the same planner as the review step. */
export function PlanLines({ preview, previewState, area, created, reused }: {
  preview: CompanySetupPlan | null;
  previewState: 'idle' | 'loading' | 'error';
  area: PlanArea;
  created: string;
  reused: string;
}) {
  const { t, locale } = useLocale();
  if (previewState === 'loading') return <p className="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Checking what already exists…')}</p>;
  const rows = preview?.items.filter(i => i.area === area) ?? [];
  if (previewState === 'error' || rows.length === 0) return <p className="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Checked again on the review step.')}</p>;
  return (
    <ul className="mt-1 space-y-0.5">
      {rows.map(i => <li key={`${i.area}-${i.code}`} className="text-xs leading-5 text-slate-600 dark:text-slate-400"><bdi>{locale === 'ar' ? i.labelAr || i.label : i.label}</bdi>: {
        i.action === 'reuse' || i.action === 'unchanged' ? t(reused)
          : i.action === 'create' ? t(created)
          : i.action === 'update' ? t('Will be updated')
          : t('Conflict — see the review step')}</li>)}
    </ul>
  );
}

/** A part's heading (h3, with its save chip) or a block inside a part (h4): below the step's h2, never repeating it. */
export function PartHeading({ id, title, description, aside, level = 4 }: { id?: string; title: string; description?: ReactNode; aside?: ReactNode; level?: 3 | 4 }) {
  const Heading = level === 3 ? 'h3' : 'h4';
  return (
    <div className="mb-3 flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
      <div className="min-w-0">
        <Heading id={id} tabIndex={level === 3 ? -1 : undefined} className={`${level === 3 ? 'text-base' : 'text-sm'} font-semibold leading-6 text-slate-900 outline-none dark:text-white`}>{title}</Heading>
        {description && <p className="mt-0.5 max-w-2xl text-sm leading-5 text-slate-600 dark:text-slate-400">{description}</p>}
      </div>
      {aside}
    </div>
  );
}
