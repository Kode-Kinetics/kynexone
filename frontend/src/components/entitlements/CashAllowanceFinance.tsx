'use client';

import Link from 'next/link';
import { useEffect, useMemo, useRef, useState, type FormEvent, type RefObject } from 'react';
import { entitlementsApi, type CorrectAllowanceResult, type MatrixComponent } from '../../api/entitlements';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  EOSB_CATEGORIES, EOSB_REASON_MAX, cashGlChoices, confirmProblems, confirmRequest, correctProblems, correctRequest, correctionMonths,
  financeServerProblem, settingsFromInfo,
  type AllowanceSettingsForm, type CorrectField, type CorrectForm, type CorrectProblems, type EosbChoice, type FinanceProblem, type GlChoice,
} from '../../lib/cashAllowance';
import { fillTemplate } from '../../lib/gradeLoanLimits';
import { monthStarts } from '../../lib/entitlementMatrix';

/** The GL accounts a cash allowance may post to, read once they are first needed. */
export type GlState = { status: 'idle' | 'loading' } | { status: 'ready'; choices: GlChoice[] } | { status: 'error'; forbidden: boolean };

/** Reads the cash GL list while `enabled`; `retry` reads it again after a failure. */
export function useCashGlChoices(enabled: boolean): { gl: GlState; retry: () => void } {
  const { locale } = useLocale();
  const [gl, setGl] = useState<GlState>({ status: 'idle' });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    setGl({ status: 'loading' });
    entitlementsApi.cashGlDrivers().then(rows => {
      if (!live) return;
      setGl(Array.isArray(rows) ? { status: 'ready', choices: cashGlChoices(rows, locale) } : { status: 'error', forbidden: false });
    }, (err: unknown) => {
      if (!live) return;
      setGl({ status: 'error', forbidden: (err as { response?: { status?: number } })?.response?.status === 403 });
    });
    return () => { live = false; };
  }, [enabled, attempt, locale]);
  return { gl, retry: () => setAttempt(n => n + 1) };
}

/**
 * The GL account select, with what it says while the list loads, fails or is empty. Ids are `${idPrefix}-gl`, `-gl-help` and `-gl-error`.
 * `forbiddenKey` is what is said to a reader who may not list the accounts (the account then stays as it is).
 */
export function GlAccountField({ idPrefix, gl, value, onChange, error, selectRef, onRetry, forbiddenKey, currentKey }: {
  idPrefix: string; gl: GlState; value: string; onChange: (key: string) => void; error: string; selectRef: RefObject<HTMLSelectElement | null>;
  onRetry: () => void; forbiddenKey: string;
  /** The account in effect, kept as an option even when the server's list no longer offers it. */
  currentKey?: string | null;
}) {
  const { t } = useLocale();
  const choices = useMemo(() => {
    if (gl.status !== 'ready') return [];
    return currentKey && !gl.choices.some(c => c.key === currentKey) ? [{ key: currentKey, label: currentKey, usable: true, reasonKey: null }, ...gl.choices] : gl.choices;
  }, [gl, currentKey]);
  const usable = choices.filter(c => c.usable);
  const choiceLabel = (c: GlChoice) => (c.usable ? c.label : fillTemplate(t(c.reasonKey ?? '{account} — no usable account'), { account: c.label }));
  const id = `${idPrefix}-gl`;
  return <div className="text-sm">
    <label htmlFor={id}>{t('GL account for this allowance (required)')}</label>
    {gl.status === 'ready' && usable.length > 0 && <select id={id} ref={selectRef} className="select mt-1 w-full" required aria-required="true" value={value}
      aria-invalid={!!error} aria-describedby={`${id}-help${error ? ` ${id}-error` : ''}`} onChange={e => onChange(e.target.value)}>
      <option value="">{t('Choose an account')}</option>
      {choices.map(c => <option key={c.key} value={c.key} disabled={!c.usable}>{choiceLabel(c)}</option>)}
    </select>}
    <p id={`${id}-help`} className="mt-1 text-xs text-slate-500 dark:text-slate-400" aria-live="polite">
      {gl.status === 'idle' || gl.status === 'loading' ? t('Loading the GL accounts…')
        : gl.status === 'error' ? <>{gl.forbidden ? t(forbiddenKey) : t('The GL accounts could not be loaded.')}{' '}
          {!gl.forbidden && <button type="button" className="text-sapphire underline" onClick={onRetry}>{t('Try again')}</button>}</>
        : usable.length === 0 ? <>{t('No GL account is set up for earnings yet. Set one up in Payroll accounting, then come back to add this allowance.')}{' '}
          <Link href="/setup?tab=glMapping" className="font-semibold text-sapphire underline">{t('Open Payroll accounting')}</Link></>
        : t('The allowance’s expense is posted to this account in the payroll journal.')}
    </p>
    {error && <p id={`${id}-error`} role="alert" className="mt-1 text-sm text-red-600">{error}</p>}
  </div>;
}

/**
 * "Does it count toward end of service pay?": Yes, or No with the reason as a category and a note (the note is required for Other).
 * Ids are `${idPrefix}-eosb-category`, `-eosb-note` and their `-error`s.
 */
export function EosbFields({ idPrefix, value, onChange, categoryError, reasonError, categoryRef, reasonRef }: {
  idPrefix: string; value: EosbChoice; onChange: (patch: Partial<EosbChoice>) => void; categoryError: string; reasonError: string;
  categoryRef: RefObject<HTMLSelectElement | null>; reasonRef: RefObject<HTMLTextAreaElement | null>;
}) {
  const { t, locale } = useLocale();
  const categoryId = `${idPrefix}-eosb-category`;
  const noteId = `${idPrefix}-eosb-note`;
  const noteRequired = value.eosbCategory === 'Other';
  return <fieldset className="space-y-2">
    <legend className="text-sm font-medium text-slate-800 dark:text-slate-100">{t('Does it count toward end of service pay?')}</legend>
    <div className="flex flex-wrap gap-4 text-sm">
      <label className="flex items-center gap-1.5"><input type="radio" name={`${idPrefix}-eosb`} checked={value.eosbIncluded} onChange={() => onChange({ eosbIncluded: true })} />{t('Yes, include it')}</label>
      <label className="flex items-center gap-1.5"><input type="radio" name={`${idPrefix}-eosb`} checked={!value.eosbIncluded} onChange={() => onChange({ eosbIncluded: false })} />{t('No, leave it out')}</label>
    </div>
    {!value.eosbIncluded && <div className="grid gap-3 sm:grid-cols-2">
      <div className="text-sm">
        <label htmlFor={categoryId}>{t('Why is it left out? (required)')}</label>
        <select id={categoryId} ref={categoryRef} className="select mt-1 w-full" required aria-required="true" value={value.eosbCategory}
          aria-invalid={!!categoryError} aria-describedby={categoryError ? `${categoryId}-error` : undefined}
          onChange={e => onChange({ eosbCategory: e.target.value as EosbChoice['eosbCategory'] })}>
          <option value="">{t('Choose a reason')}</option>
          {EOSB_CATEGORIES.map(c => <option key={c.value} value={c.value}>{t(c.labelKey)}</option>)}
        </select>
        {categoryError && <p id={`${categoryId}-error`} role="alert" className="mt-1 text-sm text-red-600">{categoryError}</p>}
      </div>
      <div className="text-sm">
        <label htmlFor={noteId}>{noteRequired ? t('Note (required)') : t('Note (optional)')}</label>
        <textarea id={noteId} ref={reasonRef} className="input mt-1 h-20 w-full" maxLength={EOSB_REASON_MAX} required={noteRequired} aria-required={noteRequired}
          value={value.eosbReason} dir={locale === 'ar' ? 'rtl' : 'ltr'} aria-invalid={!!reasonError} aria-describedby={reasonError ? `${noteId}-error` : undefined}
          onChange={e => onChange({ eosbReason: e.target.value })} />
        {reasonError && <p id={`${noteId}-error`} role="alert" className="mt-1 text-sm text-red-600">{reasonError}</p>}
      </div>
    </div>}
  </fieldset>;
}

type PanelField = CorrectField | 'general';

/**
 * Finance's two actions on a cash allowance, as a panel inside the grid area (like Add a benefit):
 *  - `confirm`: an allowance somebody without the Finance permission added. Finance checks the GL account and end of service setting
 *    (and may change them) and confirms; until then no grade can be given a value and no salary can carry it;
 *  - `correct`: a confirmed allowance whose GL account and/or end of service setting changes from a month on. A reason is required.
 * What the server refuses is said next to its field.
 */
export function FinanceAllowancePanel({ mode, component, name, today, onClose, onConfirmed, onCorrected, onMakerBlocked, onReload }: {
  mode: 'confirm' | 'correct'; component: MatrixComponent; name: string; today: string; onClose: () => void;
  onConfirmed: (component: MatrixComponent) => Promise<void> | void; onCorrected: (result: CorrectAllowanceResult) => Promise<void> | void;
  /** The server says the person who added the allowance cannot confirm it: the grid withdraws Confirm for it. */
  onMakerBlocked: (code: string) => void; onReload: () => void;
}) {
  const { t } = useLocale();
  const fmt = useFormat();
  const info = component.cashAllowance;
  const now = useMemo<AllowanceSettingsForm>(() => settingsFromInfo(info), [info]);
  const firstMonth = monthStarts(today, 1)[0];
  const [form, setForm] = useState<CorrectForm>(() => ({ ...now, effectiveFrom: firstMonth, reason: '' }));
  const [problems, setProblems] = useState<CorrectProblems>({});
  const [server, setServer] = useState<FinanceProblem | null>(null);
  const [busy, setBusy] = useState(false);
  const { gl, retry } = useCashGlChoices(true);
  const [focus, setFocus] = useState<{ field: PanelField; n: number } | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const panelRef = useRef<HTMLFormElement>(null);
  const glRef = useRef<HTMLSelectElement>(null);
  const categoryRef = useRef<HTMLSelectElement>(null);
  const noteRef = useRef<HTMLTextAreaElement>(null);
  const monthRef = useRef<HTMLSelectElement>(null);
  const reasonRef = useRef<HTMLTextAreaElement>(null);
  const confirming = mode === 'confirm';
  const months = useMemo(() => correctionMonths(today, 6, 12, server?.earliestEffectiveFrom), [today, server?.earliestEffectiveFrom]);
  const monthName = (iso: string) => fmt.gregorian(iso, 'monthYear');

  // The panel opens in view, with focus on its heading so a keyboard or screen reader user lands in it.
  useEffect(() => { panelRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' }); headingRef.current?.focus(); }, []);
  useEffect(() => {
    if (!focus) return;
    ({ glDriver: glRef, eosbCategory: categoryRef, eosbReason: noteRef, effectiveFrom: monthRef, reason: reasonRef, change: reasonRef, general: headingRef } as
      Record<string, RefObject<HTMLElement | null>>)[focus.field]?.current?.focus();
  }, [focus]);
  const ask = (field: PanelField) => setFocus(f => ({ field, n: (f?.n ?? 0) + 1 }));
  const set = (patch: Partial<CorrectForm>) => { setForm(f => ({ ...f, ...patch })); setServer(null); setProblems({}); };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found: CorrectProblems = confirming ? confirmProblems(form) : correctProblems(form, now);
    setProblems(found); setServer(null);
    const order: PanelField[] = ['effectiveFrom', 'reason', 'glDriver', 'eosbCategory', 'eosbReason', 'change'];
    const first = order.find(f => found[f as keyof CorrectProblems]);
    if (first) { ask(first); return; }
    setBusy(true);
    try {
      if (confirming) await onConfirmed(await entitlementsApi.confirmAllowance(component.code, confirmRequest(form)));
      else await onCorrected(await entitlementsApi.correctAllowance(component.code, correctRequest(form, now)));
    } catch (err) {
      const refused = financeServerProblem(err, t(confirming ? 'Unable to confirm the allowance.' : 'Unable to change the allowance.'));
      setServer(refused); setBusy(false);
      if (refused.makerBlocked) onMakerBlocked(component.code);
      // A committed month: start the list at the earliest month that can still change.
      if (refused.earliestEffectiveFrom) setForm(f => ({ ...f, effectiveFrom: refused.earliestEffectiveFrom! }));
      ask(refused.field === 'general' ? 'general' : refused.field);
    }
  };

  const shown = (field: CorrectField) => (server?.field === field ? (server.key ? t(server.key) : server.message ?? '') : '') || (problems[field] ? t(problems[field]!) : '');
  const generalError = server?.field === 'general' ? (server.key ? t(server.key) : server.message ?? '') : '';
  const monthError = server?.earliestEffectiveFrom && server.field === 'effectiveFrom'
    ? `${shown('effectiveFrom')} ${fillTemplate(t('The earliest month you can change is {month}.'), { month: monthName(server.earliestEffectiveFrom) })}`
    : shown('effectiveFrom');
  const idPrefix = `finance-${mode}`;
  const headingId = `${idPrefix}-heading`;

  return <form ref={panelRef} onSubmit={e => void submit(e)} aria-labelledby={headingId} noValidate className="surface space-y-3 rounded-2xl border border-sapphire/30 p-4" data-testid={`finance-${mode}-panel`}>
    <div>
      <h2 id={headingId} ref={headingRef} tabIndex={-1} className="font-semibold text-slate-800 outline-none dark:text-slate-100">
        {fillTemplate(t(confirming ? 'Confirm {benefit}' : 'Correct {benefit}'), { benefit: name })}
      </h2>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t(confirming
        ? 'Check the GL account and the end of service setting. No grade can be given a value for this allowance, and no salary can carry it, until you confirm.'
        : 'The change starts on the first day of the month you choose. A month a company has already approved or locked cannot be changed.')}</p>
    </div>

    {!confirming && <div className="grid gap-3 sm:grid-cols-2">
      <div className="text-sm">
        <label htmlFor={`${idPrefix}-month`}>{t('Change starts from')}</label>
        <select id={`${idPrefix}-month`} ref={monthRef} className="select mt-1 w-full" required aria-required="true" value={form.effectiveFrom}
          aria-invalid={!!monthError} aria-describedby={monthError ? `${idPrefix}-month-error` : undefined} onChange={e => set({ effectiveFrom: e.target.value })}>
          {months.map(m => <option key={m} value={m}>{monthName(m)}</option>)}
        </select>
        {monthError && <p id={`${idPrefix}-month-error`} role="alert" className="mt-1 text-sm text-red-600">{monthError}</p>}
      </div>
      <div className="text-sm">
        <label htmlFor={`${idPrefix}-reason`}>{t('Reason for the change (required)')}</label>
        <textarea id={`${idPrefix}-reason`} ref={reasonRef} className="input mt-1 h-20 w-full" required aria-required="true" value={form.reason}
          aria-invalid={!!shown('reason')} aria-describedby={shown('reason') ? `${idPrefix}-reason-error` : undefined} onChange={e => set({ reason: e.target.value })} />
        {shown('reason') && <p id={`${idPrefix}-reason-error`} role="alert" className="mt-1 text-sm text-red-600">{shown('reason')}</p>}
      </div>
    </div>}

    <div className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10">
      <GlAccountField idPrefix={idPrefix} gl={gl} value={form.glDriverKey} onChange={key => set({ glDriverKey: key })} error={shown('glDriver')} selectRef={glRef}
        onRetry={retry} forbiddenKey="You don’t have access to the list of GL accounts, so the account stays as it is." currentKey={info?.glDriverKey} />
      <EosbFields idPrefix={idPrefix} value={form} onChange={patch => set(patch)} categoryError={shown('eosbCategory')} reasonError={shown('eosbReason')}
        categoryRef={categoryRef} reasonRef={noteRef} />
    </div>

    {shown('change') && <p role="alert" className="text-sm text-red-600">{shown('change')}</p>}
    {generalError && <div role="alert" className="space-y-1 text-sm text-red-600"><p>{generalError}</p>
      {server?.stale && <button type="button" className="text-sapphire underline" onClick={onReload}>{t('Reload the grid')}</button>}</div>}
    <div className="flex flex-wrap gap-2">
      <button type="submit" className="btn-primary h-8 px-3 text-xs" disabled={busy || server?.makerBlocked === true}>
        {busy ? t('Saving…') : confirming ? t('Confirm') : t('Save the correction')}
      </button>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy} onClick={onClose}>{t('Cancel')}</button>
    </div>
  </form>;
}
