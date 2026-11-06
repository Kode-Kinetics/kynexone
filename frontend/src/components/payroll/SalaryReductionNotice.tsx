'use client';

import { TrendingDown } from 'lucide-react';
import { useEffect, useId, useRef } from 'react';
import type { ReducedItem } from '../../api/payroll';
import { useLocale } from '../../contexts/LocaleContext';
import { isolateLtr, isolateName } from '../../lib/bidi';
import { formatAmount } from '../../lib/hire/money';
import { CONSENT_REFERENCE_MAX, FIXED_DEDUCTION_ITEM, SLOT_LABEL_KEYS, isPaySlot } from '../../lib/salaryReduction';

/** What goes down, one sentence per item: "Housing allowance goes from SAR 1,500.00 to SAR 1,000.00 a month". */
function useItemSentence(currency: string, lineLabel?: (code: string) => string) {
  const { t, locale } = useLocale();
  return (r: ReducedItem) => {
    // A deduction going UP lowers take-home pay: said as going up, never as "goes from ... to ..." on a pay item.
    if (r.item === FIXED_DEDUCTION_ITEM)
      return t('{item} goes up from {from} to {to} a month.', {
        item: isolateName(t('Fixed deduction')), from: isolateLtr(formatAmount(r.from, currency, locale)), to: isolateLtr(formatAmount(r.to, currency, locale)),
      });
    const label = isPaySlot(r.item) ? t(SLOT_LABEL_KEYS[r.item.toUpperCase() as keyof typeof SLOT_LABEL_KEYS]) : lineLabel?.(r.item) || r.item;
    const item = isolateName(label);
    const from = isolateLtr(formatAmount(r.from, currency, locale));
    return r.to <= 0
      ? t('{item} is removed. It was {from} a month.', { item, from })
      : t('{item} goes from {from} to {to} a month.', { item, from, to: isolateLtr(formatAmount(r.to, currency, locale)) });
  };
}

function ReducedList({ items, currency, lineLabel }: { items: readonly ReducedItem[]; currency: string; lineLabel?: (code: string) => string }) {
  const sentence = useItemSentence(currency, lineLabel);
  if (items.length === 0) return null;
  return <ul className="mt-2 list-disc space-y-0.5 ps-5 text-sm" data-testid="reduced-items">
    {items.map(r => <li key={r.item}>{sentence(r)}</li>)}
  </ul>;
}

/** "The monthly pay goes from X to Y", only when the whole pay goes down. */
function TotalLine({ currentTotal, proposedTotal, currency }: { currentTotal?: number; proposedTotal?: number; currency: string }) {
  const { t, locale } = useLocale();
  if (currentTotal == null || proposedTotal == null || !(proposedTotal < currentTotal)) return null;
  return <p className="mt-2 text-sm font-semibold" data-testid="reduction-total">
    {t('The monthly pay goes from {from} to {to}.', { from: isolateLtr(formatAmount(currentTotal, currency, locale)), to: isolateLtr(formatAmount(proposedTotal, currency, locale)) })}
  </p>;
}

/**
 * Shown on a salary form when the proposal lowers the employee's pay: what goes down, in words with an icon (never colour alone), and the
 * one thing needed before it can be saved, the reference to the employee's written agreement. `focusToken` moves focus to the field when it
 * changes (after a refusal). `items` come from the form's own check or, when the form could not tell, from the server's refusal.
 */
export function SalaryReductionNotice({
  items, currentTotal, proposedTotal, currency, reference, onReference, problem, focusToken = 0, disabled = false, lineLabel, idPrefix,
}: {
  items: readonly ReducedItem[];
  currentTotal?: number;
  proposedTotal?: number;
  currency: string;
  reference: string;
  onReference: (value: string) => void;
  /** An already-translated sentence under the field. */
  problem?: string | null;
  focusToken?: number;
  disabled?: boolean;
  lineLabel?: (code: string) => string;
  idPrefix: string;
}) {
  const { t } = useLocale();
  const generated = useId();
  const id = `${idPrefix}-${generated.replace(/:/g, '')}`;
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => { if (focusToken > 0) ref.current?.focus(); }, [focusToken]);
  return (
    <section className="rounded-xl border border-amber-300 bg-amber-50 p-3 text-amber-950 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-100 sm:col-span-2"
      aria-labelledby={`${id}-heading`} data-testid="salary-reduction-notice">
      <h3 id={`${id}-heading`} className="flex items-center gap-2 text-sm font-semibold">
        <TrendingDown className="h-4 w-4 shrink-0" aria-hidden="true" />{t('This lowers the employee’s pay')}
      </h3>
      <p className="mt-1 text-xs">{t('The employee’s written agreement is needed before their pay goes down.')}</p>
      <ReducedList items={items} currency={currency} lineLabel={lineLabel} />
      <TotalLine currentTotal={currentTotal} proposedTotal={proposedTotal} currency={currency} />
      <div className="mt-3">
        <label htmlFor={id} className="block text-sm font-medium">{t('Employee’s written agreement reference')}</label>
        <input id={id} ref={ref} className="input mt-1 w-full max-w-md" maxLength={CONSENT_REFERENCE_MAX} required aria-required="true" autoComplete="off" disabled={disabled}
          value={reference} onChange={e => onReference(e.target.value)} aria-invalid={!!problem} aria-describedby={`${id}-help${problem ? ` ${id}-error` : ''}`} />
        <p id={`${id}-help`} className="mt-1 text-xs">{t('For example the signed amendment’s number or the Qiwa request number.')} {t('At least 3 characters, with a letter or a number.')}</p>
        {problem && <p id={`${id}-error`} role="alert" className="mt-1 text-sm font-semibold text-red-700 dark:text-red-300">{problem}</p>}
      </div>
    </section>
  );
}

/** Finance's view of a reduction: it is a reduction (said first, with an icon), what goes down, and the reference the proposer gave. */
export function SalaryReductionSummary({ items, currentTotal, proposedTotal, currency, reference, lineLabel }: {
  items: readonly ReducedItem[]; currentTotal?: number; proposedTotal?: number; currency: string; reference?: string | null; lineLabel?: (code: string) => string;
}) {
  const { t } = useLocale();
  const given = reference?.trim();
  return (
    <section className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-amber-950 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-100"
      aria-label={t('This lowers the employee’s pay')} data-testid="salary-reduction-summary">
      <p className="flex items-center gap-2 text-sm font-semibold"><TrendingDown className="h-4 w-4 shrink-0" aria-hidden="true" />{t('This lowers the employee’s pay')}</p>
      <ReducedList items={items} currency={currency} lineLabel={lineLabel} />
      <TotalLine currentTotal={currentTotal} proposedTotal={proposedTotal} currency={currency} />
      <p className="mt-2 text-sm" data-testid="reduction-reference">
        {given
          ? t('Employee’s written agreement reference: {reference}', { reference: isolateName(given) })
          : t('No written agreement reference was given.')}
      </p>
    </section>
  );
}

/** Said when a change moves a pay item or allowance line: the contract wage is not the same any more. */
export function ContractWageNote() {
  const { t } = useLocale();
  return <p className="rounded-lg border border-sky-200 bg-sky-50 p-2 text-sm text-sky-900 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-100" data-testid="contract-wage-note">
    {t('This changes the contract wage. Amend the contract on Qiwa and keep the employee’s acceptance.')}
  </p>;
}
