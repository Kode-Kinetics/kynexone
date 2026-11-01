'use client';

/**
 * Approval dead end: pay packages that no reviewer can approve (nobody independent of the person who entered them can
 * sign in and decide). Shown only to someone who can manage users, since the fix is a new user. It is absent at 0, quiet
 * while it checks and on failure, and never replaces the figures around it. One company at a time: the pay-approval check
 * is per company.
 */

import { useId, useState } from 'react';
import Link from 'next/link';
import { AlertTriangle, ChevronDown } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { usePayApprovalSetup } from '../../hooks/usePayApprovalSetup';
import { deadEndView } from '../../lib/payReviewerCheck';

const FOCUS_RING = 'outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 dark:focus-visible:ring-cyanAccent';

/** Where "Create a pay reviewer" goes when the page has no Create User dialog of its own. */
export const usersHref = (companyId: string) => `/setup?tab=users&companyId=${encodeURIComponent(companyId)}`;

export function ApprovalDeadEndCard({ companyId, canManage, onCreate }: {
  companyId: string | null;
  /** The viewer holds security.manage. Without it nothing is requested and nothing is drawn. */
  canManage: boolean;
  /** The Users page opens its own Create User dialog; elsewhere the button is a link to that page. */
  onCreate?: () => void;
}) {
  const t = useT();
  const target = canManage ? companyId : null;
  const { data, loading, error, reload } = usePayApprovalSetup(target, undefined, { refreshOnFocus: false });
  const [open, setOpen] = useState(false);
  const listId = useId();
  const view = deadEndView({ companyId: target, setup: data, loading, error });
  if (view.state === 'hidden') return null;
  if (view.state === 'loading') return <p className="sr-only" role="status">{t('Checking pay reviewers…')}</p>;
  if (view.state === 'error') return (
    <p className="flex flex-wrap items-center gap-2 text-[13px] text-slate-600 dark:text-slate-400" data-testid="dead-end-error">
      {t('Could not check reviewers')}
      <button type="button" className={`rounded font-semibold underline ${FOCUS_RING}`} onClick={reload}>{t('Retry')}</button>
    </p>
  );
  const stranded = (data?.items ?? []).filter(i => i.eligibleReviewers.length === 0);
  const create = 'btn-primary';
  return (
    <section aria-labelledby="dead-end-heading" data-testid="approval-dead-end"
      className="min-w-0 rounded-2xl border border-amber-300 bg-amber-50 p-4 text-amber-950 motion-safe:animate-fade-in-fast dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600 dark:text-amber-300" aria-hidden="true" />
          <div className="min-w-0">
            <h2 id="dead-end-heading" className="text-base font-semibold">{t('Approval dead end')}</h2>
            <p className="mt-0.5 text-sm">{t('{count, plural, one {No reviewer can approve # pay package.} other {No reviewer can approve # pay packages.}}', { count: view.count })}</p>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {onCreate
            ? <button type="button" className={`${create} ${FOCUS_RING}`} onClick={onCreate}>{t('Create a pay reviewer')}</button>
            : <Link href={usersHref(companyId!)} className={`${create} ${FOCUS_RING}`}>{t('Create a pay reviewer')}</Link>}
          <Link href="/pay-approvals" className={`rounded px-1 text-sm font-semibold underline ${FOCUS_RING}`}>{t('Open pay approvals')}</Link>
        </div>
      </div>
      {stranded.length > 0 && (
        <div className="mt-3">
          <button type="button" aria-expanded={open} aria-controls={listId} onClick={() => setOpen(o => !o)}
            className={`inline-flex items-center gap-1 rounded text-sm font-semibold ${FOCUS_RING}`}>
            <ChevronDown className={`h-4 w-4 transition-transform motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
            {open ? t('Hide packages') : t('Show packages')}
          </button>
          <div id={listId} hidden={!open}>
            <ul className="mt-2 space-y-1 text-sm">
              {stranded.map(item => <li key={item.employeeId}><bdi>{item.name}</bdi></li>)}
            </ul>
            {view.hasMore && <p className="mt-2 text-xs">{t('Showing the first 100.')}</p>}
          </div>
        </div>
      )}
    </section>
  );
}
