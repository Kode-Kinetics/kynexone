'use client';

import { AlertTriangle, CheckCircle2 } from 'lucide-react';
import { useLocale } from '../contexts/LocaleContext';
import {
  BELOW_MINIMUM_REASON_MAX, coveringAck, floorCitation, floorLabel, floorMessage, floorStatus, reasonIsValid,
  type FloorCheck, type StoredFloorAck,
} from '../lib/belowMinimum';

interface Props {
  checks: readonly FloorCheck[];
  /** Acknowledgements the server already holds for the saved values (editing). */
  stored?: readonly StoredFloorAck[];
  acknowledged: boolean;
  onAcknowledged: (v: boolean) => void;
  reason: string;
  onReason: (v: string) => void;
  /** Shown above the list; callers pass the module's own heading when "Legal minimums" does not fit. */
  heading?: string;
}

const fmt = (n: number | null | undefined) => (n == null ? '' : String(Number(n.toFixed(2))));

/**
 * The legal minimums that apply to what is typed (owner decision 2026-10-25: statutory values are defaults). Each shows the
 * Saudi default, or "More generous than the law", or an amber "Below the legal minimum of N (Art. X)". Going under needs the
 * box ticked and a reason; the form's own Save sends them as `acknowledgedWarnings`. Used by the leave policy form and the
 * end-of-service rates, and by anything else that follows the same contract.
 */
export function BelowMinimumPanel({ checks, stored = [], acknowledged, onAcknowledged, reason, onReason, heading }: Props) {
  const { t, locale } = useLocale();
  if (checks.length === 0) return null;
  const open = checks.filter(c => c.below && !coveringAck(stored, c));
  return (
    <div data-testid="below-minimum-panel" className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
      <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">{heading ?? t('Legal minimums')}</p>
      <ul className="space-y-2">
        {checks.map(c => {
          const status = floorStatus(c);
          const held = c.below ? coveringAck(stored, c) : undefined;
          return (
            <li key={c.code} data-testid={`floor-${c.code}`} data-status={status}
              className={`rounded-lg px-3 py-2 text-sm ${status === 'below'
                ? 'bg-amber-50 text-amber-900 dark:bg-amber-500/10 dark:text-amber-200'
                : 'bg-slate-50 text-slate-700 dark:bg-white/5 dark:text-slate-300'}`}>
              <div className="flex items-start gap-2">
                {status === 'below'
                  ? <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
                  : <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" />}
                <div className="min-w-0">
                  <p className="font-medium">{floorLabel(c, locale)}</p>
                  {status === 'below' && (
                    <p>
                      {c.statutoryValue != null
                        ? t('Below the legal minimum of {min} ({citation})', { min: fmt(c.statutoryValue), citation: floorCitation(c, locale) })
                        : t('Below the legal minimum ({citation})', { citation: floorCitation(c, locale) })}
                    </p>
                  )}
                  {status === 'below' && <p className="mt-1 text-xs opacity-90">{floorMessage(c, locale)}</p>}
                  {status === 'default' && <p>{t('Saudi default: {value}', { value: fmt(c.statutoryValue) })}</p>}
                  {status === 'generous' && <p>{t('More generous than the law')} · {t('Saudi default: {value}', { value: fmt(c.statutoryValue) })}</p>}
                  {held && (
                    <p className="mt-1 text-xs">
                      {t('Acknowledged by {name} on {date}: {reason}', { name: held.acknowledgedBy, date: held.acknowledgedAtUtc.slice(0, 10), reason: held.reason })}
                    </p>
                  )}
                </div>
              </div>
            </li>
          );
        })}
      </ul>
      {open.length > 0 && (
        <div className="mt-3 space-y-2">
          <label className="flex items-center gap-2 text-sm text-slate-800 dark:text-slate-200">
            <input type="checkbox" className="rounded" data-testid="below-minimum-ack" checked={acknowledged} onChange={e => onAcknowledged(e.target.checked)} />
            {t('I understand employees may claim the difference')}
          </label>
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Why is this below the legal minimum? (10 to 500 characters)')}
            <textarea data-testid="below-minimum-reason" rows={2} maxLength={BELOW_MINIMUM_REASON_MAX}
              className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm dark:border-white/10 dark:bg-transparent"
              value={reason} onChange={e => onReason(e.target.value)} aria-invalid={acknowledged && !reasonIsValid(reason)} />
          </label>
        </div>
      )}
    </div>
  );
}
