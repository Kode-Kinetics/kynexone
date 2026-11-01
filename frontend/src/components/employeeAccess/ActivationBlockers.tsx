'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { payApprovalsApi } from '../../api/payApprovals';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { PAY_APPROVE_PERMISSION, PAY_GAP_KEY } from '../../lib/payApprovals';
import { isolateName } from '../../lib/bidi';

export interface BlockerItem { key: string; label: string }

type Reviewers = { status: 'idle' | 'loading' | 'failed' } | { status: 'loaded'; names: string[]; me: boolean };

/**
 * What stands in the way of activation, read from the server's readiness check. This component
 * never decides eligibility: the Activate button is disabled by `blockers.length > 0` alone, and
 * the server's own refusal (its `message`) is shown as it arrived.
 *
 * `blockers` is null when readiness could not be loaded.
 */
export function ActivationBlockers({
  blockers, employeeId, companyId, labelOf, onRetry, error, loading = false,
}: {
  blockers: ReadonlyArray<BlockerItem> | null;
  employeeId: number;
  companyId?: string | null;
  /** How an item reads; defaults to t(label). */
  labelOf?: (item: BlockerItem) => string;
  onRetry?: () => void;
  /** Readiness is still being read: say so rather than calling it unavailable. */
  loading?: boolean;
  /** The backend's `message` from a refused activation, verbatim. */
  error?: string;
}) {
  const { t, locale } = useLocale();
  const { hasPermission, user } = useAuth();
  const canApprovePay = hasPermission(PAY_APPROVE_PERMISSION);
  const canManage = hasPermission('security.manage');
  const hasPayGap = !!blockers?.some((item) => item.key === PAY_GAP_KEY);
  const [reviewers, setReviewers] = useState<Reviewers>({ status: 'idle' });

  useEffect(() => {
    if (!hasPayGap || !companyId || !(canApprovePay || canManage)) { setReviewers({ status: 'idle' }); return; }
    let cancelled = false;
    setReviewers({ status: 'loading' });
    payApprovalsApi.setup(companyId).then((setup) => {
      if (cancelled) return;
      const item = setup.items.find((entry) => entry.employeeId === employeeId);
      if (!item) { setReviewers({ status: 'failed' }); return; }
      setReviewers({ status: 'loaded', names: item.eligibleReviewers.map((r) => r.name), me: item.eligibleReviewers.some((r) => r.id === user?.id) });
    }).catch(() => { if (!cancelled) setReviewers({ status: 'failed' }); });
    return () => { cancelled = true; };
  }, [hasPayGap, companyId, canApprovePay, canManage, employeeId, user?.id]);

  const label = labelOf ?? ((item: BlockerItem) => t(item.label));
  const setupHref = `/setup?tab=users${companyId ? `&companyId=${encodeURIComponent(companyId)}` : ''}`;

  return (
    <div data-testid="activation-blockers" className="space-y-3 text-sm">
      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{error}</p>}
      {blockers === null && loading && <p role="status" className="text-slate-500">{t('Loading readiness…')}</p>}
      {blockers === null && !loading && (
        <div className="flex flex-wrap items-center gap-3">
          <p className="inline-flex items-center gap-2 font-semibold text-amber-800 dark:text-amber-200"><AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />{t('Readiness could not be checked.')}</p>
          {onRetry && <button type="button" onClick={onRetry} className="btn-secondary min-h-11 md:min-h-9">{t('Retry')}</button>}
        </div>
      )}
      {!!blockers?.length && (
        <div>
          <p className="font-semibold text-slate-800 dark:text-slate-100">{t('Activation is blocked by these items')}</p>
          <ul className="mt-1 list-disc space-y-2 ps-5 text-amber-800 dark:text-amber-200">
            {blockers.map((item) => (
              <li key={item.key}>
                {label(item)}
                {item.key === PAY_GAP_KEY && (
                  <div className="mt-1 space-y-1 text-slate-700 dark:text-slate-200" data-testid="pay-blocker-detail">
                    <p>{t('An independent reviewer must approve the pay package before activation. Giving the employee a role or resetting their password does not approve their pay.')}</p>
                    <p>{t('Who can act: a Finance Approver who did not enter or change this pay and is not this employee.')}</p>
                    {(canApprovePay || canManage) && reviewers.status === 'loaded' && (reviewers.names.length
                      ? <p data-testid="eligible-reviewers">{t('Eligible reviewers: {names}', { names: reviewers.names.map(isolateName).join(locale === 'ar' ? '، ' : ', ') })}</p>
                      : <p>{t('No eligible reviewer is set up yet.')}</p>)}
                    {!(canApprovePay || canManage) && <p>{t('Ask your administrator.')}</p>}
                    {canApprovePay && reviewers.status === 'loaded' && !reviewers.me && <p>{t('You are not an independent reviewer for this pay package, so someone else must approve it.')}</p>}
                    <div className="flex flex-wrap gap-2 pt-1">
                      {canApprovePay && (reviewers.status === 'loaded' && reviewers.me || reviewers.status === 'idle'
                        ? <Link className="btn-secondary min-h-11 md:min-h-9" href={`/pay-approvals/${employeeId}`}>{t('Review the pay package')}</Link>
                        : <Link className="btn-secondary min-h-11 md:min-h-9" href="/pay-approvals">{t('Pay approvals')}</Link>)}
                      {canManage && <Link className="btn-secondary min-h-11 md:min-h-9" href={setupHref}>{t('Check reviewer setup')}</Link>}
                    </div>
                  </div>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
