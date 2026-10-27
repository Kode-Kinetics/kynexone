'use client';

import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Search } from 'lucide-react';
import { successionApi, type GapCode, type PersonRef } from '../../api/succession';
import { useAuth } from '../../contexts/AuthContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useLocale } from '../../contexts/LocaleContext';
import { SUC, SUCCESSION_FLAG, gapExplanation, gapLabel, gapTone, personName, successionError } from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';

export { EmptyBlock, ErrorBlock, Field, LoadingRows, ProgressBar } from '../learning/LearningParts';

/**
 * Renders a succession screen only for tenants with the `succession` opt-in flag on. A direct link from a tenant
 * without it gets one plain sentence. The API refuses the same tenant (403 feature_not_enabled); this is the courtesy layer.
 */
export function SuccessionGate({ children }: { children: ReactNode }) {
  const { t } = useLocale();
  const { isLoading, isFeatureEnabled } = useFeatureFlags();
  if (isLoading) return <div className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  if (!isFeatureEnabled(SUCCESSION_FLAG)) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300" data-testid="suc-disabled">
        {t('suc.disabled')}
      </div>
    );
  }
  return <>{children}</>;
}

/** What the signed-in person may do in succession, from their token. The server re-checks every call. */
export function useSuccessionAccess() {
  const { hasPermission } = useAuth();
  const has = useCallback((key: string) => hasPermission(key), [hasPermission]);
  return {
    read: has(SUC.read),
    positionsManage: has(SUC.positionsManage),
    poolsManage: has(SUC.poolsManage),
    nominate: has(SUC.nominate),
    assess: has(SUC.assess),
    calibrate: has(SUC.calibrate),
    slatesRead: has(SUC.slatesRead),
    slatesManage: has(SUC.slatesManage),
    devplansRead: has(SUC.devplansRead),
    devplansManage: has(SUC.devplansManage),
    reportsRead: has(SUC.reportsRead),
    self: has(SUC.self),
  };
}

export type SuccessionAccess = ReturnType<typeof useSuccessionAccess>;

/**
 * The server's raw value for a test or a screen reader-free check, next to the translated label people read.
 * Visually hidden and hidden from assistive technology (the visible chip already says it in words).
 */
export function RawCode({ testId, value }: { testId: string; value: string }) {
  return <span data-testid={testId} className="sr-only" aria-hidden="true">{value}</span>;
}

/** Gap codes as plain-language chips; each chip's title says what is missing. */
export function GapChips({ gaps, emptyLabel }: { gaps: GapCode[]; emptyLabel?: string }) {
  const { t } = useLocale();
  if (gaps.length === 0) return emptyLabel ? <StatusChip label={emptyLabel} tone="emerald" /> : null;
  return (
    <span className="flex flex-wrap gap-1">
      {gaps.map((g) => (
        <span key={g} title={t(gapExplanation[g])} data-gap={g}>
          <StatusChip label={t(gapLabel[g])} tone={gapTone(g)} />
        </span>
      ))}
    </span>
  );
}

/** A person's name in the viewer's language with their employee code, and "has left" when they have. */
export function PersonName({ person, fallback }: { person: PersonRef | null | undefined; fallback?: string }) {
  const { t, locale } = useLocale();
  if (!person) return <span className="text-slate-400">{fallback ?? '—'}</span>;
  return (
    <span>
      <span className="font-medium text-slate-800 dark:text-slate-100">{personName(person, locale)}</span>
      <span className="ms-1 text-xs text-slate-400" dir="ltr">{person.employeeCode}</span>
      {person.hasLeft && <span className="ms-1 text-xs font-semibold text-rose-600 dark:text-rose-400">{t('suc.person.hasLeft')}</span>}
    </span>
  );
}

/**
 * The one person picker every succession form uses. Typing searches /api/succession/people (the caller's data scope:
 * HR their companies, a manager their reporting line); each match is a button. The chosen person is shown under the box.
 */
export function PersonPicker({ id, testId, value, onChange, label, hint, error, optional, exclude }: {
  id: string; testId: string; value: PersonRef | null; onChange: (p: PersonRef | null) => void; label: string; hint?: string;
  error?: string | null; optional?: boolean; exclude?: number[];
}) {
  const { t, locale } = useLocale();
  const [query, setQuery] = useState('');
  const [options, setOptions] = useState<PersonRef[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const seq = useRef(0);
  const listId = useId();

  useEffect(() => {
    const q = query.trim();
    if (!q) { setOptions(null); setSearchError(null); return; }
    const mine = ++seq.current;
    setLoading(true);
    const timer = setTimeout(() => {
      successionApi.people(q)
        .then((rows) => { if (seq.current === mine) { setOptions(rows.filter((r) => !exclude?.includes(r.employeeId))); setSearchError(null); } })
        .catch((e) => { if (seq.current === mine) { setOptions([]); setSearchError(successionError(e, t)); } })
        .finally(() => { if (seq.current === mine) setLoading(false); });
    }, 250);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query]);

  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(' ') || undefined;

  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-xs font-semibold text-slate-700 dark:text-slate-200">
        {label}{optional && <span className="ms-1 font-normal text-slate-400">{t('suc.optional')}</span>}
      </label>
      {hint && <p id={`${id}-hint`} className="text-caption text-slate-500 dark:text-slate-400">{hint}</p>}
      <div className="relative">
        <Search className="pointer-events-none absolute start-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" aria-hidden="true" />
        <input
          id={id}
          data-testid={testId}
          className="input w-full ps-8"
          value={query}
          autoComplete="off"
          placeholder={t('suc.picker.placeholder')}
          aria-describedby={describedBy}
          aria-controls={listId}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>
      {value && (
        <p className="flex flex-wrap items-center gap-2 text-xs text-slate-600 dark:text-slate-300" data-testid={`${testId}-selected`}>
          <span>{t('suc.picker.chosen')}</span>
          <PersonName person={value} />
          <button type="button" className="text-xs font-semibold text-sapphire underline dark:text-cyanAccent" onClick={() => onChange(null)}>
            {t('suc.picker.clear')}
          </button>
        </p>
      )}
      {query.trim() && (
        <div id={listId} className="max-h-48 overflow-y-auto rounded-lg border border-slate-200 bg-white p-1 dark:border-white/10 dark:bg-slate-900" aria-live="polite">
          {loading && <p className="p-2 text-xs text-slate-500">{t('suc.picker.searching')}</p>}
          {!loading && searchError && <p className="p-2 text-xs text-rose-600" role="alert">{searchError}</p>}
          {!loading && !searchError && options?.length === 0 && <p className="p-2 text-xs text-slate-500">{t('suc.picker.none')}</p>}
          {!loading && options?.map((p) => (
            <button
              key={p.employeeId}
              type="button"
              data-testid="suc-person-option"
              data-employee-id={p.employeeId}
              className="flex w-full items-center justify-between gap-2 rounded-lg px-2 py-1.5 text-start text-sm hover:bg-slate-100 dark:hover:bg-white/[0.06]"
              onClick={() => { onChange(p); setQuery(''); setOptions(null); }}
            >
              <span>{personName(p, locale)}</span>
              <span className="text-xs text-slate-400" dir="ltr">{p.employeeCode}</span>
            </button>
          ))}
        </div>
      )}
      {error && <p id={`${id}-error`} role="alert" className="text-caption font-medium text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}

/** An inline refusal under a form, tied to the form by role=alert. */
export function FormError({ message }: { message: string | null }) {
  if (!message) return null;
  return <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{message}</p>;
}

/** A page-level "you cannot see this" in plain words (403 / missing permission). */
export function ForbiddenBlock({ message }: { message?: string }) {
  const { t } = useLocale();
  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300" data-testid="suc-forbidden" role="status">
      {message ?? t('suc.forbidden')}
    </div>
  );
}

export function SectionCard({ title, description, actions, children, testId }: { title: string; description?: string; actions?: ReactNode; children: ReactNode; testId?: string }) {
  return (
    <section className="wg-card space-y-3 rounded-2xl p-4" data-testid={testId} aria-label={title}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{title}</h2>
          {description && <p className="text-xs text-slate-500 dark:text-slate-400">{description}</p>}
        </div>
        {actions}
      </div>
      {children}
    </section>
  );
}

/**
 * One reason-taking confirmation for every destructive or recorded step (decline, hold, cancel, discard, retire).
 * The confirm button stays usable; a too-short reason is explained under the box instead of silently disabling it.
 */
export function ReasonDialog({ title, intro, confirmLabel, minLength, onClose, onConfirm, testId }: {
  title: string; intro?: string; confirmLabel: string; minLength: number; onClose: () => void; onConfirm: (reason: string) => Promise<void>; testId?: string;
}) {
  const { t } = useLocale();
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problem = reason.trim().length >= minLength ? null : t('suc.check.minN', { count: minLength });
  const id = `${testId ?? 'suc-reason'}-text`;
  return (
    <Modal isOpen title={title} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid={testId ? `${testId}-confirm` : undefined} disabled={busy} onClick={async () => {
          setTouched(true);
          if (problem) return;
          setBusy(true);
          setError(null);
          try { await onConfirm(reason.trim()); } catch (e) { setError(successionError(e, t)); } finally { setBusy(false); }
        }}>{busy ? t('suc.saving') : confirmLabel}</button>
      </>
    )}>
      <div className="space-y-3">
        {intro && <p className="text-xs text-slate-600 dark:text-slate-300">{intro}</p>}
        <div className="space-y-1">
          <label htmlFor={id} className="block text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.reason')}</label>
          <textarea id={id} data-testid={testId ? `${testId}-reason` : undefined} className="input min-h-[72px] w-full" value={reason}
            aria-describedby={touched && problem ? `${id}-error` : undefined} onChange={(e) => setReason(e.target.value)} />
          {touched && problem && <p id={`${id}-error`} role="alert" className="text-caption font-medium text-rose-600 dark:text-rose-400">{problem}</p>}
        </div>
        <FormError message={error} />
      </div>
    </Modal>
  );
}
