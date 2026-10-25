'use client';

import { cloneElement, isValidElement, useCallback, useEffect, useId, useRef, useState, type ReactElement, type ReactNode } from 'react';
import { AlertTriangle, RotateCcw } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { describeApiError } from '../../lib/apiError';
import { pickServerMessage } from '../../lib/glCostAccounting';
import { Modal } from '../Modal';

// Small presentational helpers shared across the GL / rates configuration panels.
// Styling reuses the app's global utility classes (input, select, btn-*, surface).

export type Translator = ReturnType<typeof useT>;

/** A label bound to its control: the child gets the generated id unless it already has one. */
export function Field({
  label,
  required,
  hint,
  children,
}: {
  label: string;
  required?: boolean;
  hint?: ReactNode;
  children: ReactNode;
}) {
  const t = useT();
  const generated = useId();
  const hintId = `${generated}-hint`;
  let controlId = generated;
  let control = children;
  if (isValidElement(children)) {
    const el = children as ReactElement<{ id?: string; 'aria-describedby'?: string }>;
    controlId = el.props.id ?? generated;
    control = cloneElement(el, { id: controlId, ...(hint ? { 'aria-describedby': hintId } : {}) });
  }
  return (
    <div>
      <label htmlFor={controlId} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label} {required && <span className="text-rose-500" title={t('Required')}>*</span>}
      </label>
      {control}
      {hint && (
        <p id={hintId} className="mt-1 text-[11px] leading-snug text-slate-500 dark:text-slate-400">
          {hint}
        </p>
      )}
    </div>
  );
}

export function FieldError({ error, testId }: { error: string; testId?: string }) {
  if (!error) return null;
  return (
    <p role="alert" data-testid={testId} className="mb-3 rounded-lg bg-rose-50 px-3 py-2.5 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
      {error}
    </p>
  );
}

export function WarningNote({ children }: { children: ReactNode }) {
  return (
    <div role="note" className="mb-3 flex items-start gap-1.5 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">
      <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
      <div>{children}</div>
    </div>
  );
}

export type Tone = 'slate' | 'sapphire' | 'emerald' | 'amber' | 'rose' | 'violet';

const toneClass: Record<Tone, string> = {
  slate: 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300',
  sapphire: 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent',
  emerald: 'bg-emerald-50 text-emerald-700 dark:bg-emeraldZ/20 dark:text-emerald-300',
  amber: 'bg-amber-100 text-amber-800 dark:bg-amber-500/15 dark:text-amber-300',
  rose: 'bg-rose-100 text-rose-700 dark:bg-rose-500/15 dark:text-rose-300',
  violet: 'bg-violet-100 text-violet-700 dark:bg-violet-500/15 dark:text-violet-300',
};

export function Badge({
  tone = 'slate',
  children,
  title,
  testId,
  label,
}: {
  tone?: Tone;
  children: ReactNode;
  title?: string;
  testId?: string;
  /** A full accessible name for a badge whose text alone is not enough ("Cost centre rule: Required"). */
  label?: string;
}) {
  return (
    <span
      title={title}
      data-testid={testId}
      {...(label ? { role: 'img' as const, 'aria-label': label } : {})}
      className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-semibold ${toneClass[tone]}`}
    >
      {children}
    </span>
  );
}

export function InheritedBadge() {
  const t = useT();
  return (
    <Badge tone="slate" title={t('Set at group level and not overridden for this company.')}>
      {t('Inherited from group')}
    </Badge>
  );
}

export function SystemBadge() {
  const t = useT();
  return (
    <Badge tone="sapphire" title={t('Built-in driver. Its routing rules are locked and it cannot be deleted.')}>
      {t('System')}
    </Badge>
  );
}

export function ActiveBadge({ active }: { active: boolean }) {
  const t = useT();
  return <Badge tone={active ? 'emerald' : 'slate'}>{active ? t('Active') : t('Inactive')}</Badge>;
}

export function StatusBadge({ status }: { status: string }) {
  const t = useT();
  const tone: Tone = status === 'Active' ? 'emerald' : status === 'PendingApproval' ? 'amber' : 'slate';
  return <Badge tone={tone}>{statusLabel(t, status)}</Badge>;
}

// ── Enum labels (plain words, translated) ───────────────────────────────────

export function statusLabel(t: Translator, status: string): string {
  switch (status) {
    case 'Active': return t('Active');
    case 'Inactive': return t('Inactive');
    case 'PendingApproval': return t('Pending approval');
    case 'Superseded': return t('Superseded');
    case 'Archived': return t('Archived');
    default: return status;
  }
}

export function accountTypeLabel(t: Translator, type: string): string {
  switch (type) {
    case 'Asset': return t('Asset');
    case 'Liability': return t('Liability');
    case 'Equity': return t('Equity');
    case 'Income':
    case 'Revenue': return t('Income');
    case 'Expense': return t('Expense');
    default: return type;
  }
}

export function sideLabel(t: Translator, side: string | null | undefined): string {
  const s = (side ?? '').toUpperCase();
  if (s === 'DR' || s === 'DEBIT') return t('Debit');
  if (s === 'CR' || s === 'CREDIT') return t('Credit');
  return side || '—';
}

export function driverCategoryLabel(t: Translator, category: string): string {
  switch (category) {
    case 'Earning': return t('Earning');
    case 'Deduction': return t('Deduction');
    case 'Balancing': return t('Balancing');
    default: return category;
  }
}

export function rateCategoryLabel(t: Translator, category: string): string {
  switch (category) {
    case 'Allowance': return t('Allowance');
    case 'Deduction': return t('Deduction');
    case 'EOSB': return t('End-of-service benefit');
    case 'PayParameter': return t('Pay parameter');
    default: return category;
  }
}

// ── Effective routing (GET /gl/resolve) ─────────────────────────────────────

/** The DOM id of a driver's row in the "Where does it post?" table, for banner links. */
export const routeRowId = (driverKey: string) => `gl-route-${driverKey.replace(/[^A-Za-z0-9_-]/g, '_')}`;

/** Lower sorts first: problems that stop a line posting at all come before softer ones. */
export function problemRank(problem: string | null | undefined): number {
  switch (problem) {
    case 'NotInChart': return 0;
    case 'InactiveAccount': return 1;
    case 'Unmapped': return 2;
    case 'InactiveDriver': return 3;
    case null:
    case undefined:
    case '': return 9;
    default: return 4;
  }
}

export function problemLabel(t: Translator, problem: string | null | undefined): string {
  switch (problem) {
    case 'Unmapped': return t('No account is mapped, so it posts to the built-in default.');
    case 'InactiveAccount': return t('It posts to an account that is inactive.');
    case 'NotInChart': return t('It posts to an account code that is not in the chart of accounts.');
    case 'InactiveDriver': return t('The driver is inactive, so matching pay lines do not route through it.');
    default: return problem ?? '';
  }
}

export function routeSourceLabel(t: Translator, source: string): { label: string; tone: Tone; hint: string } {
  switch (source) {
    case 'CompanyMapping':
      return { label: t('Company override'), tone: 'violet', hint: t('Mapped for this company only.') };
    case 'GroupMapping':
      return { label: t('Inherited from group'), tone: 'slate', hint: t('Mapped at group level and used by every company without an override.') };
    case 'CompanyDriverDefault':
    case 'GroupDriverDefault':
      return { label: t('Driver default'), tone: 'amber', hint: t('No mapping; the driver\'s own fallback account is used.') };
    case 'BuiltIn':
      return { label: t('Built-in'), tone: 'amber', hint: t('No mapping and no fallback; the platform\'s built-in account code is used.') };
    default:
      return { label: source, tone: 'slate', hint: '' };
  }
}

// ── States ──────────────────────────────────────────────────────────────────

export function Spinner() {
  const t = useT();
  return (
    <div className="grid place-items-center py-12" role="status" aria-live="polite">
      <div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" aria-hidden />
      <span className="sr-only">{t('Loading…')}</span>
    </div>
  );
}

export function LoadError({ message, onRetry }: { message: string; onRetry: () => void }) {
  const t = useT();
  return (
    <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
      <div>
        <p className="font-medium">{t('This could not be loaded.')}</p>
        <p className="text-xs">{message}</p>
      </div>
      <button type="button" onClick={onRetry} className="btn-secondary h-8 px-3 text-xs">
        <RotateCcw className="h-3.5 w-3.5" aria-hidden /> {t('Retry')}
      </button>
    </div>
  );
}

/**
 * Loading, error and empty are three different states. A failed load shows the reason and a Retry,
 * never the empty-state text (which would tell the user to seed data that may already exist).
 */
export function PanelState({
  loading,
  error,
  onRetry,
  empty,
  emptyLabel,
  children,
}: {
  loading: boolean;
  error?: string | null;
  onRetry?: () => void;
  empty: boolean;
  emptyLabel: ReactNode;
  children: ReactNode;
}) {
  if (error) return <LoadError message={error} onRetry={onRetry ?? (() => undefined)} />;
  if (loading) return <Spinner />;
  if (empty) return <p className="py-10 text-center text-sm text-slate-500 dark:text-slate-400">{emptyLabel}</p>;
  return <>{children}</>;
}

/**
 * A scoped read with request sequencing: a response for a scope the user has already left is
 * dropped, so a slow company-A reply can never paint over company B. `ready` is true only while
 * the data on screen belongs to the selected scope; writes are gated on it.
 */
export function useScopedResource<T>(key: string, fetcher: () => Promise<T>, enabled = true) {
  const t = useT();
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(enabled);
  const [error, setError] = useState<string | null>(null);
  const [loadedKey, setLoadedKey] = useState<string | null>(null);
  const seq = useRef(0);
  const fetcherRef = useRef(fetcher);
  const tRef = useRef(t);
  const keyRef = useRef(key);
  useEffect(() => {
    fetcherRef.current = fetcher;
    tRef.current = t;
  });

  const run = useCallback(async (k: string) => {
    const mine = ++seq.current;
    setLoading(true);
    setError(null);
    try {
      const result = await fetcherRef.current();
      if (mine !== seq.current) return;
      setData(result);
      setLoadedKey(k);
    } catch (err) {
      if (mine !== seq.current) return;
      setError(describeApiError(err, tRef.current));
    } finally {
      if (mine === seq.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetcherRef.current = fetcher;
    if (keyRef.current !== key) {
      // A different scope: never show the previous scope's rows while the new ones load.
      setData(null);
      setLoadedKey(null);
      keyRef.current = key;
    }
    if (!enabled) {
      seq.current++;
      setLoading(false);
      return;
    }
    void run(key);
    // `fetcher` is read through the ref; the key is what identifies a distinct request.
  }, [key, enabled, run]);

  const reload = useCallback(() => run(keyRef.current), [run]);
  // The render that follows a scope change happens before the effect above clears `data`, so the
  // previous scope's rows would be drawn once under the new scope (duplicated group rows, wrong
  // labels). Only hand out data that was loaded for THIS key, and say it is loading meanwhile.
  const current = loadedKey === key;
  return {
    data: current ? data : null,
    loading: loading || (enabled && !current && !error),
    error,
    loadedKey,
    ready: current && !loading && !error,
    reload,
  };
}

/** The page's language, set on <html> by the locale provider; handlers that have no hook read it from here. */
function pageLocale(): string {
  return typeof document === 'undefined' ? 'en' : document.documentElement.lang || 'en';
}

/**
 * The server's own refusal sentence (shown verbatim) in the viewer's language (`messageAr` for Arabic, `message`
 * otherwise), else a translated description.
 */
export function serverMessage(err: unknown, t: Translator, locale: string = pageLocale()): string {
  return pickServerMessage((err as { response?: { data?: unknown } })?.response?.data, locale) ?? describeApiError(err, t);
}

export function errorStatus(err: unknown): number | undefined {
  return (err as { response?: { status?: number } })?.response?.status;
}

// ── Confirmation ────────────────────────────────────────────────────────────

export interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  tone?: 'danger' | 'primary';
}

export function ConfirmDialog({
  open,
  options,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  options: ConfirmOptions | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const t = useT();
  return (
    <Modal
      isOpen={open && !!options}
      title={options?.title ?? ''}
      onClose={onCancel}
      size="sm"
      footer={
        <>
          <button type="button" onClick={onCancel} className="btn-secondary">
            {t('Cancel')}
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className={
              options?.tone === 'danger'
                ? 'inline-flex h-9 items-center gap-2 rounded-lg bg-rose-700 px-4 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-rose-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500/50'
                : 'btn-primary'
            }
          >
            {options?.confirmLabel}
          </button>
        </>
      }
    >
      <div className="text-sm text-slate-600 dark:text-slate-300">{options?.message}</div>
    </Modal>
  );
}

/** `const ok = await confirm({...})` — the translated, accessible replacement for window.confirm. */
export function useConfirm() {
  const [state, setState] = useState<{ options: ConfirmOptions; resolve: (ok: boolean) => void } | null>(null);
  const confirm = useCallback(
    (options: ConfirmOptions) => new Promise<boolean>((resolve) => setState({ options, resolve })),
    [],
  );
  const close = (ok: boolean) => {
    state?.resolve(ok);
    setState(null);
  };
  const dialog = (
    <ConfirmDialog open={!!state} options={state?.options ?? null} onConfirm={() => close(true)} onCancel={() => close(false)} />
  );
  return { confirm, dialog };
}

/** Today as yyyy-MM-dd (local calendar day), for date-input defaults and effective-date checks. */
export function todayIso(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export const thClass = 'px-3 py-2 text-start font-medium';
export const theadClass = 'bg-slate-50 text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400';
