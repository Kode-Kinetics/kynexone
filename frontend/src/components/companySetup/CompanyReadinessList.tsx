'use client';

import { useState } from 'react';
import Link from 'next/link';
import { CheckCircle2, Circle, Star } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { usersApi, type PasswordResetLinkResult } from '../../api/identity';
import type { ReadinessItem } from '../../api/companySetup';

/** The permission POST /api/access/users/{id}/password-reset-link requires (AccessController's class-level gate). */
const ISSUE_LINK_PERMISSION = 'security.manage';

/**
 * What is still to do for the new company, computed by the server. Each row opens the screen that
 * finishes it. Organisation-wide rows appear only while they are unconfigured.
 *
 * The company super admin row carries `userId` while that person has not signed in. A caller who may
 * issue sign-in links sends it from here in one click; anyone else gets the User Management link.
 */
export function CompanyReadinessList({ items }: { items: ReadinessItem[] }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canIssueLinks = hasPermission(ISSUE_LINK_PERMISSION);
  const label = (item: ReadinessItem) => (locale === 'ar' ? item.labelAr || item.label : item.label);
  const statusLabel = (s: ReadinessItem['status']) => s === 'done' ? t('Done') : s === 'recommended' ? t('Recommended') : t('To do');
  const section = (scope: ReadinessItem['scope'], heading: string, description: string) => {
    const rows = items.filter(i => i.scope === scope);
    if (rows.length === 0) return null;
    return (
      <section aria-label={heading} className="space-y-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{heading}</h3>
        <p className="text-xs text-slate-600 dark:text-slate-400">{description}</p>
        <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
          {rows.map(item => {
            const sendsLink = item.status !== 'done' && !!item.userId && canIssueLinks;
            return (
              <li key={item.key} className="px-4 py-3" data-testid={`readiness-${item.key}`} data-status={item.status}>
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex min-w-0 items-center gap-3">
                    {item.status === 'done'
                      ? <CheckCircle2 className="h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />
                      : item.status === 'recommended' ? <Star className="h-5 w-5 shrink-0 text-amber-500" aria-hidden="true" />
                      : <Circle className="h-5 w-5 shrink-0 text-slate-400" aria-hidden="true" />}
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-slate-900 dark:text-white"><bdi>{label(item)}</bdi></p>
                      <p className="text-xs text-slate-500 dark:text-slate-400">{statusLabel(item.status)}</p>
                    </div>
                  </div>
                  {item.status !== 'done' && !sendsLink && <Link href={item.href} className="btn-secondary">{t('Open')}<span className="sr-only"> {label(item)}</span></Link>}
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
    <div className="space-y-5" data-testid="company-readiness">
      {section('company', t('This company'), t('Finish these before the first payroll for this company.'))}
      {section('organisation', t('Applies to all companies'), t('Organisation settings that are not configured yet. They apply to every company in the organisation.'))}
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
      <button type="button" className="btn-primary" disabled={state === 'sending'} onClick={() => void send()}>
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
