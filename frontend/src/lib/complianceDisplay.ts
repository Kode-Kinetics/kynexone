/**
 * Display rules for the Saudi Compliance readiness figures.
 *
 * A readiness percentage is `ready / total`. When `total` is 0 there is nothing to be ready for,
 * so the answer is neither 0% nor 100% — it is undefined. The API sends `null` for that case and
 * these helpers turn it into an honest label, which is what stops the screen printing a full green
 * "100% Readiness" bar directly above its own "Company GOSI employer ID is not set" warning.
 */

/** Shown in a tight space (a badge) where a sentence will not fit. */
export const READINESS_UNKNOWN_SHORT = '—';

/** Shown where there is room to say why the figure is missing. */
export const READINESS_UNKNOWN_LABEL = 'Not configured';

/** `null` → "—". Never "0%", never "100%". */
export function formatReadinessPercent(percent: number | null | undefined): string {
  return percent == null ? READINESS_UNKNOWN_SHORT : `${percent}%`;
}

/**
 * The caption beside a readiness bar. `measured` is the caller's own wording for the real case
 * ("12 ready / 3 blocked"), used only when there is something to measure.
 */
export function readinessCaption(percent: number | null | undefined, measured: string): string {
  return percent == null ? READINESS_UNKNOWN_LABEL : measured;
}

/** Tailwind text colour for a readiness figure; neutral slate when the figure is undefined. */
export function readinessToneClass(percent: number | null | undefined): string {
  if (percent == null) return 'text-slate-400 dark:text-slate-500';
  if (percent >= 90) return 'text-emerald-600 dark:text-emerald-400';
  if (percent >= 60) return 'text-amber-600 dark:text-amber-400';
  return 'text-rose-600 dark:text-rose-400';
}
