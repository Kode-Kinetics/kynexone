'use client';

import { useLocale } from '../../contexts/LocaleContext';
import { LIKERT_LABELS } from '../../lib/engagementDisplay';
import type { Distribution } from '../../api/engagement';

/**
 * A 5-point distribution as one 100 % bar: two warm steps (disagree), a neutral grey midpoint, two cool steps (agree).
 * Diverging palette validated for colour-vision deficiency in light and dark (dataviz validator, ΔE ≥ 12); colour is
 * never alone — the legend names each step and the numbers are always printed beside the bar. Plain HTML, so it
 * mirrors in RTL with the document direction.
 */
export const LIKERT_COLORS_LIGHT = ['#B42318', '#EE8B78', '#C4CBD5', '#6E98FF', '#1F4FD1'];
export const LIKERT_COLORS_DARK = ['#D9483B', '#F4B3A6', '#64748B', '#9FC0FF', '#3D74F5'];

export function LikertStyles() {
  const vars = (colors: string[]) => colors.map((c, i) => `--eng-likert-${i + 1}:${c};`).join('');
  return (
    <style>{`:root{${vars(LIKERT_COLORS_LIGHT)}}.dark{${vars(LIKERT_COLORS_DARK)}}`}</style>
  );
}

export function LikertBar({ distribution, label }: { distribution: Distribution[]; label: string }) {
  const { t } = useLocale();
  const total = distribution.reduce((s, d) => s + d.count, 0);
  if (total === 0) return null;
  return (
    <div
      className="flex h-3 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.06]"
      role="img"
      aria-label={`${label} — ${distribution.map((d, i) => `${t(LIKERT_LABELS[i])} ${d.pct}%`).join('، ')}`}
    >
      {distribution.map((d, i) => d.count > 0 && (
        <span
          key={d.key}
          className="h-full first:rounded-s-full last:rounded-e-full"
          style={{ width: `${(100 * d.count) / total}%`, background: `var(--eng-likert-${i + 1})`, marginInlineEnd: i < 4 ? 2 : 0 }}
          title={`${t(LIKERT_LABELS[i])}: ${d.count} (${d.pct}%)`}
        />
      ))}
    </div>
  );
}

export function LikertLegend() {
  const { t } = useLocale();
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-600 dark:text-slate-300" aria-label={t('Answer scale')}>
      {LIKERT_LABELS.map((label, i) => (
        <li key={label} className="flex items-center gap-1.5">
          <span className="h-2.5 w-2.5 rounded-sm" style={{ background: `var(--eng-likert-${i + 1})` }} aria-hidden="true" />
          {t(label)}
        </li>
      ))}
    </ul>
  );
}
