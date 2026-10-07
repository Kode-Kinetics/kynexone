'use client';

import Link from 'next/link';
import { Hourglass } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * The one next action when a contract's benefits wait for a second person (a term activated after it started): propose
 * them, or review the proposal already waiting. Plain words, no codes. Used on the employee's package panel and on the
 * contract register, so both screens say the same thing.
 */
export function BenefitsAwaitingNotice({ action, busy, onPropose, onReview, reviewHref }: {
  action: 'proposeBenefits' | 'reviewProposal';
  busy?: boolean;
  /** Shown only when the viewer may propose (entitlements.manage). */
  onPropose?: () => void;
  onReview?: () => void;
  reviewHref?: string;
}) {
  const { t } = useLocale();
  const button = 'inline-flex shrink-0 items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-semibold text-white hover:bg-sapphire/90 disabled:opacity-60';
  return (
    <div role="status" className="flex flex-col items-start gap-2 rounded-xl border border-sky-200 bg-sky-50 px-3 py-2 text-xs text-sky-900 dark:border-sky-500/20 dark:bg-sky-500/10 dark:text-sky-100">
      <span className="flex items-start gap-2">
        <Hourglass className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        {t('Benefits for this contract are waiting for a second person to confirm.')}
      </span>
      {action === 'proposeBenefits' && onPropose && (
        <button type="button" disabled={busy} onClick={onPropose} className={button}>{t('Propose benefits')}</button>
      )}
      {action === 'reviewProposal' && (reviewHref
        ? <Link href={reviewHref} className={button}>{t('Review proposal')}</Link>
        : onReview && <button type="button" onClick={onReview} className={button}>{t('Review proposal')}</button>)}
    </div>
  );
}
