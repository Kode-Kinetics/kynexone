'use client';

import { useCallback, useEffect, useId, useState } from 'react';
import Link from 'next/link';
import { CheckCircle2, ChevronDown, Circle, Star } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { usersApi, type PasswordResetLinkResult } from '../../api/identity';
import { companySetupApi, type ReadinessItem } from '../../api/companySetup';

/** The permission POST /api/access/users/{id}/password-reset-link requires (AccessController's class-level gate). */
const ISSUE_LINK_PERMISSION = 'security.manage';

/** The one item to do next: the first To-do row in the order shown (this company's rows, then the workspace's). */
export function nextReadinessItem(items: readonly ReadinessItem[]): ReadinessItem | null {
  return items.find(i => i.scope === 'company' && i.status === 'todo') ?? items.find(i => i.scope === 'workspace' && i.status === 'todo') ?? null;
}

/**
 * What is still to do for a company, computed by the server. Each row opens the screen that finishes it, and the one
 * item to do next is marked and carries the only primary button. Workspace-wide rows appear only while they are
 * unconfigured.
 *
 * The company super admin row carries `userId` while that person has not signed in. A caller who may
 * issue sign-in links sends it from here in one click; anyone else gets the User Management link.
 */
export function CompanyReadinessList({ items, compact = false }: {
  items: ReadinessItem[];
  /** Inside a chooser row or the summary: no section descriptions, tighter rows. */
  compact?: boolean;
}) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canIssueLinks = hasPermission(ISSUE_LINK_PERMISSION);
  const label = (item: ReadinessItem) => (locale === 'ar' ? item.labelAr || item.label : item.label);
  const statusLabel = (s: ReadinessItem['status']) => s === 'done' ? t('Done') : s === 'recommended' ? t('Recommended (optional)') : t('To do');
  const next = nextReadinessItem(items);
  const done = items.filter(i => i.status === 'done').length;
  const section = (scope: ReadinessItem['scope'], heading: string, description: string) => {
    const rows = items.filter(i => i.scope === scope);
    if (rows.length === 0) return null;
    return (
      <section aria-label={heading} className="space-y-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{heading}</h3>
        {!compact && <p className="text-xs text-slate-600 dark:text-slate-400">{description}</p>}
        <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
          {rows.map(item => {
            const sendsLink = item.status !== 'done' && !!item.userId && canIssueLinks;
            const isNext = item.key === next?.key && item.scope === next.scope;
            return (
              <li key={`${item.scope}-${item.key}`} className={`${compact ? 'px-3 py-2.5' : 'px-4 py-3'} ${isNext ? 'bg-sapphire/[0.04] dark:bg-cyanAccent/[0.05]' : ''}`}
                data-testid={`readiness-${item.key}`} data-status={item.status} data-next={isNext ? 'true' : undefined}>
                <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2">
                  <div className="flex min-w-0 flex-[1_1_12rem] items-center gap-3">
                    {item.status === 'done'
                      ? <CheckCircle2 className="h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />
                      : item.status === 'recommended' ? <Star className="h-5 w-5 shrink-0 text-amber-500" aria-hidden="true" />
                      : <Circle className="h-5 w-5 shrink-0 text-slate-400" aria-hidden="true" />}
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-slate-900 dark:text-white"><bdi>{label(item)}</bdi></p>
                      <p className="text-xs text-slate-600 dark:text-slate-400">{statusLabel(item.status)}</p>
                    </div>
                  </div>
                  {item.status !== 'done' && !sendsLink && (
                    isNext
                      ? <Link href={item.href} className="btn-primary max-w-full shrink-0 justify-center max-sm:w-full"><bdi className="truncate">{t('Next: {item}', { item: label(item) })}</bdi></Link>
                      : <Link href={item.href} className="btn-secondary shrink-0">{t('Open')}<span className="sr-only"> {label(item)}</span></Link>
                  )}
                </div>
                {sendsLink && <SendSignInLink userId={item.userId!} href={item.href} />}
              </li>
            );
          })}
        </ul>
      </section>
    );
  };
  return (
    <div className={compact ? 'space-y-3' : 'space-y-5'} data-testid="company-readiness">
      {items.length > 0 && (
        <div className="space-y-1.5" data-testid="readiness-summary">
          <p className="text-sm text-slate-700 dark:text-slate-300">
            <span className="font-semibold text-slate-900 dark:text-white">{t('{done} of {total} done', { done, total: items.length })}</span>
            {!next && <>{' · '}{t('Nothing else to do before payroll.')}</>}
          </p>
          <div className="h-1.5 max-w-md overflow-hidden rounded-full bg-slate-200 dark:bg-white/10" aria-hidden="true">
            <div className={`h-full rounded-full ${done === items.length ? 'bg-emerald-600' : 'bg-sapphire'}`} style={{ width: `${Math.round((done / items.length) * 100)}%` }} />
          </div>
        </div>
      )}
      {section('company', t('This company'), t('Finish these before the first payroll for this company.'))}
      {section('workspace', t('Applies to all companies'), t('Workspace settings that are not configured yet. They apply to every company in the group.'))}
    </div>
  );
}

/**
 * "5 of 9 done" made explainable: a disclosure under the count that loads the company's checklist on first open and
 * lists every item with the screen that finishes it. Nothing is requested until the person asks.
 */
export function ReadinessDrilldown({ companyId, complete, testId }: { companyId: string; complete: boolean; testId?: string }) {
  const { t } = useLocale();
  const panelId = useId();
  const [open, setOpen] = useState(false);
  const [state, setState] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [items, setItems] = useState<ReadinessItem[]>([]);
  const load = useCallback(() => {
    setState('loading');
    companySetupApi.readiness(companyId)
      .then(list => { setItems(list); setState('ready'); })
      .catch(() => setState('error'));
  }, [companyId]);
  useEffect(() => { if (open && state === 'idle') load(); }, [open, state, load]);
  return (
    <div className="w-full" data-testid={testId}>
      <button type="button" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(v => !v)}
        className="inline-flex min-h-8 items-center gap-1 rounded-md px-1 text-xs font-semibold text-sapphire transition-colors duration-150 hover:bg-sapphire/[0.06] hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire/50 dark:text-cyanAccent dark:hover:bg-cyanAccent/10 -ms-1">
        {open ? t('Hide the checklist') : complete ? t('Show the checklist') : t('Show what is left')}
        <ChevronDown className={`h-3.5 w-3.5 transition-transform duration-150 motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
      </button>
      <div id={panelId} hidden={!open} className="mt-2 motion-safe:animate-fade-in-fast">
        {state === 'loading' && (
          <div role="status" aria-busy="true" className="space-y-2" data-testid="readiness-loading">
            <span className="sr-only">{t('Loading the checklist…')}</span>
            {[0, 1, 2].map(i => <div key={i} className="h-11 rounded-lg bg-slate-100 motion-safe:animate-pulse dark:bg-white/[0.06]" aria-hidden="true" />)}
          </div>
        )}
        {state === 'error' && (
          <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">
            {t('The checklist could not be loaded. Check your connection and try again.')}{' '}
            <button type="button" className="font-semibold underline underline-offset-2" onClick={load}>{t('Try again')}</button>
          </p>
        )}
        {state === 'ready' && (items.length > 0
          ? <CompanyReadinessList items={items} compact />
          : <p className="text-sm text-slate-600 dark:text-slate-400">{t('No setup checklist yet for this company.')}</p>)}
      </div>
    </div>
  );
}

/**
 * One click: the server's single-use sign-in (password-reset) link for the new company super admin. The server
 * emails it when the workspace can send mail; otherwise it hands the link back once, and only here, to be passed on
 * through the company's own channel. KynexOne never creates a mailbox and nobody types a password for them.
 */
function SendSignInLink({ userId, href }: { userId: string; href: string }) {
  const { t } = useLocale();
  const [state, setState] = useState<'idle' | 'sending' | 'failed'>('idle');
  const [result, setResult] = useState<PasswordResetLinkResult | null>(null);
  const [copied, setCopied] = useState(false);

  const send = async () => {
    setState('sending');
    try {
      setResult(await usersApi.issuePasswordResetLink(userId));
      setCopied(false);
      setState('idle');
    } catch {
      setState('failed');
    }
  };
  const copy = (url: string) => {
    void navigator.clipboard?.writeText(url).then(() => setCopied(true), () => setCopied(false));
  };

  if (result) return (
    <div className="mt-3 space-y-2" role="status" data-testid="sign-in-link-result" data-emailed={result.emailSent ? 'true' : 'false'}>
      {result.emailSent || !result.resetUrl
        ? <p className="text-sm text-emerald-700 dark:text-emerald-400">{t('Emailed to {email}. The link works once and expires in 1 hour.', { email: result.email })}</p>
        : <>
            <p className="text-sm text-amber-800 dark:text-amber-300">{t('Nothing was emailed. Copy this sign-in link and send it to {email} through the company’s own channel. It works once and expires in 1 hour.', { email: result.email })}</p>
            <div className="flex flex-wrap gap-2 sm:flex-nowrap">
              <label className="sr-only" htmlFor={`sign-in-link-${userId}`}>{t('Single-use sign-in link')}</label>
              <input id={`sign-in-link-${userId}`} readOnly dir="ltr" value={result.resetUrl} onFocus={e => e.currentTarget.select()}
                className="input min-w-0 flex-1 font-mono text-xs" data-testid="sign-in-link-url" />
              <button type="button" className="btn-secondary shrink-0" onClick={() => copy(result.resetUrl!)}>{copied ? t('Copied') : t('Copy link')}</button>
            </div>
          </>}
    </div>
  );

  return (
    <div className="mt-3 flex flex-wrap items-center gap-2">
      <button type="button" className="btn-secondary" disabled={state === 'sending'} onClick={() => void send()}>
        {state === 'sending' ? t('Sending sign-in link…') : t('Send sign-in link')}
      </button>
      {state === 'failed' && (
        <p role="alert" className="text-sm text-rose-700 dark:text-rose-400">
          {t('The sign-in link could not be created. Try again, or open User Management.')}{' '}
          <Link href={href} className="underline">{t('Open User Management')}</Link>
        </p>
      )}
    </div>
  );
}
