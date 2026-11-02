'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { AlertTriangle, CheckCircle2, Circle, Loader2 } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { usersApi, type UserListItem } from '../../api/identity';
import type { CompanyAdminMode, CompanySetupPayReviewer } from '../../api/companySetup';
import { useReviewerVerification } from '../../hooks/usePayApprovalSetup';
import { payReviewerErrors } from '../../lib/companySetupWizard';
import { PAY_REVIEWER_ROLE, draftHasReviewer, eligiblePayReviewers, type ReviewerVerification } from '../../lib/payReviewerCheck';
import { completeEmail, emailHost, isPublicEmailDomain } from '../../lib/companySetupFields';
import { Notice, WizardField, useWizardForm } from './wizardForm';

const FOCUS_RING = 'outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 dark:focus-visible:ring-cyanAccent';

/**
 * The Approvers step: pay is entered by one person and approved by another, so the company gets a second login with the
 * Finance Approver role beside the administrator. Optional (the checklist and review warn when it is skipped), and shown
 * with the administrator step: both are role grants the same people may make.
 */
export function ApproversStep() {
  const t = useT();
  const { body, update, readOnly } = useWizardForm();
  const reviewer: CompanySetupPayReviewer = body.payReviewer ?? { mode: 'groupOnly' };
  const domain = (body.company.emailDomain ?? '').trim().toLowerCase();
  const publicHost = !domain && isPublicEmailDomain(emailHost(reviewer.email)) ? emailHost(reviewer.email) : '';
  const set = (patch: Partial<CompanySetupPayReviewer>) => update(b => ({ ...b, payReviewer: { ...(b.payReviewer ?? { mode: 'groupOnly' }), ...patch } }));
  const [users, setUsers] = useState<UserListItem[] | null>(null);
  const [usersError, setUsersError] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (reviewer.mode !== 'existing' || users) return;
    let active = true;
    setUsersError(false);
    usersApi.listAll({ status: 'Active' })
      .then(rows => { if (active) setUsers(eligiblePayReviewers(rows).sort((a, b) => a.fullName.localeCompare(b.fullName))); })
      .catch(() => { if (active) setUsersError(true); });
    return () => { active = false; };
  }, [reviewer.mode, users, attempt]);

  const option = (mode: CompanyAdminMode, title: string, description: string) => (
    // The radio's text is the two spans below (the same card as AdminStep); the rule cannot see through the wrapper.
    // eslint-disable-next-line jsx-a11y/label-has-associated-control
    <label className={`flex cursor-pointer gap-3 rounded-xl border p-4 ${reviewer.mode === mode ? 'border-sapphire bg-sapphire/[0.04] dark:border-cyanAccent' : 'border-slate-200 dark:border-white/10'}`}>
      <input type="radio" name="ccw-reviewer-mode" className="mt-1" checked={reviewer.mode === mode} disabled={readOnly} onChange={() => set({ mode })} />
      <span className="min-w-0">
        <span className="block text-sm font-semibold text-slate-900 dark:text-white">{title}</span>
        <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{description}</span>
      </span>
    </label>
  );

  return (
    <div className="space-y-5">
      <Notice tone="info">
        <p className="font-semibold">{t('Pay is entered by one person and approved by another. Create both now.')}</p>
      </Notice>

      <section aria-labelledby="ccw-reviewer-heading" className="space-y-3 rounded-2xl border border-slate-200 p-4 sm:p-5 dark:border-white/10" data-testid="reviewer-card">
        <h3 id="ccw-reviewer-heading" className="text-base font-semibold text-slate-900 dark:text-white">{t('Independent pay reviewer')}</h3>
        <fieldset className="space-y-3">
          <legend className="sr-only">{t('How the pay reviewer is set up')}</legend>
          <div className="grid gap-3 lg:grid-cols-3">
            {option('invite', t('Create a new pay reviewer'), t('They receive a welcome code to sign in with their own company email. You never set their password.'))}
            {option('existing', t('Make an existing user a reviewer'), t('Adds pay review for this company to someone who already signs in. Their other access stays the same.'))}
            {option('groupOnly', t('Add one later'), t('Pay entered for this company cannot be approved until a reviewer is added.'))}
          </div>
        </fieldset>

        {reviewer.mode === 'invite' && (
          <div className="grid gap-x-5 gap-y-4 pt-1 sm:grid-cols-2">
            <WizardField field="payReviewer.fullNameEn" label={t('Full name (English)')} required>
              {control => <input {...control} className="input w-full" autoComplete="off" readOnly={readOnly} value={reviewer.fullNameEn ?? ''} onChange={e => set({ fullNameEn: e.target.value })} />}
            </WizardField>
            <WizardField field="payReviewer.fullNameAr" label={t('Full name (Arabic)')} help={t('Optional. Shown when KynexOne is used in Arabic.')}>
              {control => <input {...control} className="input w-full" dir="rtl" lang="ar" autoComplete="off" readOnly={readOnly} value={reviewer.fullNameAr ?? ''} onChange={e => set({ fullNameAr: e.target.value })} />}
            </WizardField>
            <WizardField field="payReviewer.email" label={t('Official work email')} required className="sm:col-span-2"
              help={publicHost
                ? <span className="font-medium text-amber-800 dark:text-amber-200">{t('{domain} is a public email service. Use the person’s company email; KynexOne never creates mailboxes.', { domain: publicHost })}</span>
                : domain ? t('Must be an address on {domain}. Type just the name and @{domain} is added for you.', { domain }) : t('Their existing company email. KynexOne never creates mailboxes.')}>
              {control => <input {...control} type="email" dir="ltr" spellCheck={false} autoCapitalize="none" className="input w-full" autoComplete="off" readOnly={readOnly}
                placeholder={domain ? `name@${domain}` : 'name@company.sa'} value={reviewer.email ?? ''}
                onChange={e => set({ email: e.target.value.trim() })}
                onBlur={() => { const done = completeEmail(reviewer.email ?? '', domain); if (done !== (reviewer.email ?? '')) set({ email: done }); }} />}
            </WizardField>
            <div className="min-w-0">
              <label htmlFor="ccw-reviewer-role" className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Role for this login')}</label>
              <input id="ccw-reviewer-role" className="input w-full cursor-default bg-slate-50 dark:bg-white/[0.03]" readOnly value={PAY_REVIEWER_ROLE} aria-describedby="ccw-reviewer-role-help" />
              <p id="ccw-reviewer-role-help" className="mt-1.5 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Fixed. It can approve pay for this company only.')}</p>
            </div>
          </div>
        )}

        {reviewer.mode === 'existing' && (
          <div className="max-w-xl pt-1">
            <WizardField field="payReviewer.existingUserId" label={t('User')} required
              help={t('Only active users who hold the Finance Approver role and are not administrators are listed.')}>
              {control => <select {...control} className="select w-full" disabled={readOnly || !users} value={reviewer.existingUserId ?? ''} onChange={e => set({ existingUserId: e.target.value || undefined })}>
                <option value="">{users ? t('Choose a user…') : t('Loading users…')}</option>
                {(users ?? []).map(u => <option key={u.id} value={u.id}>{`${u.fullName} · ${u.email}`}</option>)}
              </select>}
            </WizardField>
            {users && users.length === 0 && <div className="mt-2" data-testid="no-eligible-reviewers"><Notice tone="info">{t('No user holds the Finance Approver role yet. Create a new pay reviewer instead.')}</Notice></div>}
            {usersError && <div className="mt-2"><Notice tone="error" role="alert">{t('The user list could not be loaded. Check your connection and try again, or create a new pay reviewer instead.')}<button type="button" className="btn-secondary ms-2" onClick={() => setAttempt(a => a + 1)}>{t('Try again')}</button></Notice></div>}
          </div>
        )}
      </section>

      <VerificationRow />
    </div>
  );
}

/** Can pay be approved? A checklist on the draft while creating; the live answer too when completing an existing company. */
function VerificationRow() {
  const t = useT();
  const { body } = useWizardForm();
  const companyId = body.existingCompanyId ?? null;
  return (
    <section aria-labelledby="ccw-verify-heading" className="space-y-2" data-testid="reviewer-verification">
      <h3 id="ccw-verify-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Can pay be approved?')}</h3>
      {companyId && <LiveVerification companyId={companyId} />}
      <DraftChecklist />
    </section>
  );
}

function LiveVerification({ companyId }: { companyId: string }) {
  const { result, reload } = useReviewerVerification(companyId);
  return <VerificationStatus result={result} onRetry={reload} />;
}

/** The four states of the live check. The height is reserved, so the row never jumps between them. */
export function VerificationStatus({ result, onRetry }: { result: ReviewerVerification; onRetry: () => void }) {
  const t = useT();
  const { locale } = useLocale();
  const base = 'flex min-h-[3.5rem] items-start gap-3 rounded-xl border px-4 py-3 text-sm';
  return (
    <div role="status" aria-live="polite" data-testid="reviewer-live" data-state={result.state}>
      {result.state === 'checking' && (
        <div className={`${base} border-slate-200 text-slate-700 dark:border-white/10 dark:text-slate-200`}>
          <Loader2 className="mt-0.5 h-4 w-4 shrink-0 motion-safe:animate-spin" aria-hidden="true" />
          <p>{t('Checking…')}</p>
        </div>
      )}
      {result.state === 'ready' && (
        <div className={`${base} border-emerald-300 bg-emerald-50 text-emerald-950 dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-100`}>
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" />
          <p>{result.name ? <bdi>{t('A usable reviewer exists: {name}', { name: result.name })}</bdi> : t('A usable reviewer exists.')}</p>
        </div>
      )}
      {result.state === 'notReady' && (
        <div className={`${base} border-amber-300 bg-amber-50 text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100`}>
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" aria-hidden="true" />
          <div className="min-w-0 flex-1">
            <p className="font-semibold">{t('Not ready')}</p>
            <p className="mt-0.5">
              {result.reason
                ? <bdi>{locale === 'ar' ? result.reason.labelAr || result.reason.label : result.reason.label}</bdi>
                : t('No one who can sign in and approve pay exists for this company yet. A new reviewer must set a password and finish two-step sign-in first.')}
            </p>
            {result.reason?.href && <Link href={result.reason.href} className={`mt-1 inline-block rounded font-semibold underline ${FOCUS_RING}`}>{t('Open Users')}</Link>}
          </div>
        </div>
      )}
      {result.state === 'error' && (
        <div className={`${base} flex-wrap items-center border-slate-300 text-slate-800 dark:border-white/20 dark:text-slate-100`}>
          <p className="min-w-0 flex-1">{t('Could not check pay reviewers.')}</p>
          <button type="button" className={`btn-secondary ${FOCUS_RING}`} onClick={onRetry}>{t('Retry')}</button>
        </div>
      )}
    </div>
  );
}

function DraftChecklist() {
  const t = useT();
  const { body } = useWizardForm();
  const r = body.payReviewer;
  if (!draftHasReviewer(body) || !r) {
    return (
      <p className="flex items-start gap-2 text-sm text-slate-700 dark:text-slate-300" data-testid="reviewer-checklist-none">
        <Circle className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" aria-hidden="true" />
        {t('No pay reviewer in this setup. Pay entered here cannot be approved until one is added.')}
      </p>
    );
  }
  const blocked = new Set(payReviewerErrors(body, body.company.emailDomain).map(e => e.field));
  const emailOk = !!(r.email ?? '').trim() && !blocked.has('payReviewer.email');
  const rows: Array<{ ok: boolean; text: string }> = r.mode === 'invite'
    ? [
      { ok: !!(r.fullNameEn ?? '').trim(), text: t('Name entered') },
      { ok: emailOk, text: t('Official company email, different from the administrator') },
      { ok: true, text: t('Role: Finance Approver, this company only') },
    ]
    : [{ ok: !!r.existingUserId && !blocked.has('payReviewer.existingUserId'), text: t('A user is chosen, and is not the administrator') }];
  return (
    <ul className="space-y-1.5 text-sm" data-testid="reviewer-checklist">
      {rows.map(row => (
        <li key={row.text} className="flex items-start gap-2 text-slate-800 dark:text-slate-200" data-ok={row.ok}>
          {row.ok ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" /> : <Circle className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" aria-hidden="true" />}
          <span>{row.text}<span className="sr-only"> ({row.ok ? t('Done') : t('To do')})</span></span>
        </li>
      ))}
      <li className="flex items-start gap-2 text-amber-900 dark:text-amber-200" data-testid="reviewer-not-ready">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" aria-hidden="true" />
        <span>{t('Not ready until the person sets a password and finishes two-step sign-in. Pay stays blocked until then.')}</span>
      </li>
    </ul>
  );
}
