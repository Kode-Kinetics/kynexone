'use client';

import { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { ArrowRight, Building2, Plus } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { companySetupApi, httpStatus, type CompanySetupContext, type CompanySetupDraftSummary, type ManageableCompany } from '../../api/companySetup';
import { completeCompanyHref, draftHref, planUsage, readinessProgress, stepCopy, type StepId } from '../../lib/companySetupWizard';
import { useCountryName } from './CountryOptions';
import { Notice } from './wizardForm';

type Load = 'loading' | 'ready' | 'error' | 'forbidden';

/**
 * Setup → Guided setup (Addendum A): one wizard for both journeys. A big "Create a new company" card, then one row
 * per active company the caller may manage with its readiness, its draft and one next action. A deep link that names
 * a company (`linkedCompanyId`: a link from elsewhere in the app, a notification, an old studio bookmark) opens that
 * company's wizard directly.
 *
 * The chooser never reads `companyId` from the address itself: there it is Setup's company scope, which every in-page
 * move keeps (the navigation's Guided setup entry included). Setup decides which arrival is a deep link
 * (setupLandingCompanyLink) and hands it over once.
 *
 * Who sees what is the server's answer (context.manageableCompanies / canCreate): a company super admin gets only
 * their own company and no create card.
 */
export function GuidedSetupChooser({ showHeading = true, linkedCompanyId = null, onLinkedCompanyUsed }: {
  /** Setup's section header already names the section; the chooser then shows only its explanation. */
  showHeading?: boolean;
  /** The company a deep link named, to open its wizard instead of the chooser. */
  linkedCompanyId?: string | null;
  /** Called once the link has been followed or refused, so it is never followed twice. */
  onLinkedCompanyUsed?: () => void;
} = {}) {
  const { t, locale } = useLocale();
  const fmt = useFormat();
  const country = useCountryName();
  const router = useRouter();
  const searchParams = useSearchParams();
  const linkedCompany = useRef(linkedCompanyId);
  const [load, setLoad] = useState<Load>('loading');
  const [context, setContext] = useState<CompanySetupContext | null>(null);
  const [drafts, setDrafts] = useState<CompanySetupDraftSummary[]>([]);
  const [linkRefused, setLinkRefused] = useState(false);
  const [redirectTo, setRedirectTo] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    setLoad('loading');
    Promise.all([companySetupApi.context(), companySetupApi.drafts().catch(() => [] as CompanySetupDraftSummary[])])
      .then(([ctx, list]) => {
        if (!active) return;
        const linked = linkedCompany.current;
        linkedCompany.current = null;
        if (linked) {
          onLinkedCompanyUsed?.();
          const target = ctx.manageableCompanies?.find(c => c.id === linked);
          if (target) { setRedirectTo(completeCompanyHref(target.id, target.openDraftId)); return; }
          // Fail closed: a link to a company this caller cannot manage opens nothing in its place.
          setLinkRefused(true);
        }
        setContext(ctx);
        setDrafts(list);
        setLoad('ready');
      })
      .catch(err => { if (active) setLoad(httpStatus(err) === 403 ? 'forbidden' : 'error'); });
    return () => { active = false; };
  }, [attempt, router]);

  // Setup rewrites a legacy address (mode=starter) to its canonical one first; a redirect issued before that
  // rewrite lands would be overwritten by it. Leave once the address is canonical.
  const legacyAddress = !!searchParams?.get('mode');
  useEffect(() => {
    if (redirectTo && !legacyAddress) router.replace(redirectTo);
  }, [redirectTo, legacyAddress, router]);

  if (load === 'loading' || redirectTo) return <p role="status" className="surface p-6 text-sm text-slate-600 dark:text-slate-300">{t('Opening Guided setup…')}</p>;
  if (load === 'forbidden') return (
    <Notice tone="warning" role="alert">
      <p className="font-semibold">{t('Guided setup is for administrators and HR managers.')}</p>
      <p className="mt-1">{t('Ask your group super admin for access, or use the Setup sections your role can open.')}</p>
    </Notice>
  );
  if (load === 'error' || !context) return (
    <Notice tone="error" role="alert">
      <p>{t('Guided setup could not be opened. Check your connection and try again.')}</p>
      <button type="button" className="btn-secondary mt-3" onClick={() => setAttempt(a => a + 1)}>{t('Try again')}</button>
    </Notice>
  );

  const usage = planUsage(context);
  const companies = context.manageableCompanies ?? [];
  const createDrafts = drafts.filter(d => !d.existingCompanyId);
  const name = (c: ManageableCompany) => (locale === 'ar' && c.legalNameAr?.trim()) || c.legalNameEn;
  const stepTitle = (d: CompanySetupDraftSummary) => t(stepCopy((Math.min(Math.max(d.step, 1), 7)) as StepId, 'create').title);

  return (
    <div className="space-y-6" data-testid="guided-setup-chooser">
      <div className="min-w-0">
        {showHeading && <h2 className="text-xl font-semibold tracking-tight text-slate-950 dark:text-white">{t('Guided setup')}</h2>}
        <p className={`${showHeading ? 'mt-1 ' : ''}max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400`}>{context.isGroupScope
          ? t('Create a company, or finish setting one up. Each screen leads to the next, and nothing is saved until you review it.')
          : t('Finish setting up your company. Each screen leads to the next, and nothing is saved until you review it.')}</p>
      </div>

      {linkRefused && <Notice tone="warning" role="alert">{t('The link named a company you cannot set up, so it was not opened. Choose one of the companies below.')}</Notice>}

      {/* A company super admin cannot create companies: the card is not offered at all (A5). */}
      {context.isGroupScope && (
        <section aria-labelledby="gsc-create" className="rounded-2xl border border-sapphire/25 bg-sapphire/[0.04] p-5 dark:border-cyanAccent/25 dark:bg-cyanAccent/[0.04] sm:p-6" data-testid="chooser-create">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="flex min-w-0 items-start gap-3">
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-950"><Plus className="h-5 w-5" aria-hidden="true" /></span>
              <div className="min-w-0">
                <h3 id="gsc-create" className="text-lg font-semibold text-slate-950 dark:text-white">{t('Create a new company')}</h3>
                <p className="mt-1 max-w-xl text-sm leading-6 text-slate-600 dark:text-slate-300">{t('Legal details, branches, organisation, work rules and its administrator, in seven steps. The company and its setup are created together.')}</p>
                {usage && <p className="mt-2 text-xs font-medium text-slate-600 dark:text-slate-400" data-testid="chooser-plan-usage">{usage.allowed === null
                  ? t('{used, plural, one {# company} other {# companies}} in this organisation', { used: usage.used })
                  : t('{used} of {allowed} companies on your plan', { used: usage.used, allowed: usage.allowed })}</p>}
              </div>
            </div>
            {context.canCreate
              ? <Link href="/setup/new-company" className="btn-primary">{t('Create a new company')}<ArrowRight className="h-4 w-4 rtl:rotate-180" aria-hidden="true" /></Link>
              : <span className="btn-primary cursor-not-allowed opacity-50" aria-disabled="true">{t('Create a new company')}</span>}
          </div>
          {!context.canCreate && <p className="mt-3 text-sm text-amber-900 dark:text-amber-200" role="status">{context.refusalMessage || t('Your licence does not allow another company.')}</p>}
          {createDrafts.length > 0 && (
            <ul className="mt-4 space-y-2" aria-label={t('Continue where you left off')} data-testid="chooser-create-drafts">
              {createDrafts.map(d => (
                <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-white px-3 py-2 dark:bg-white/[0.04]">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{d.legalNameEn || t('Untitled company')}</bdi></p>
                    <p className="text-xs text-slate-600 dark:text-slate-400">{t('Stopped at step {step}: {title}', { step: d.step, title: stepTitle(d) })}{' · '}{t('Saved {when} by {name}', { when: fmt.dateTime(d.updatedAtUtc), name: d.createdByName })}</p>
                  </div>
                  <Link href={draftHref(d)} className="btn-secondary">{t('Resume')}<span className="sr-only"> {d.legalNameEn}</span></Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      <section aria-labelledby="gsc-existing" className="space-y-3" data-testid="chooser-existing">
        <div>
          <h3 id="gsc-existing" className="text-base font-semibold text-slate-900 dark:text-white">{context.isGroupScope ? t('Complete setup for an existing company') : t('Complete setup for your company')}</h3>
          <p className="mt-0.5 text-sm text-slate-600 dark:text-slate-400">{t('Adds what is missing and updates what you change. Nothing is removed.')}</p>
        </div>
        {context.manageableCompanies == null
          ? <p className="rounded-xl border border-dashed border-slate-300 px-4 py-4 text-sm text-slate-600 dark:border-white/15 dark:text-slate-400" data-testid="chooser-unavailable">{t('Completing setup for an existing company is not available on this server yet. Use All setup areas for now.')}</p>
          : companies.length === 0
          ? <p className="rounded-xl border border-dashed border-slate-300 px-4 py-4 text-sm text-slate-600 dark:border-white/15 dark:text-slate-400" data-testid="chooser-empty">{context.isGroupScope
              ? t('There is no active company yet. Create the first one above.')
              : t('There is no active company you can set up. Ask your group super admin.')}</p>
          : (
            <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
              {companies.map(c => {
                const progress = readinessProgress(c);
                const action = c.openDraftId ? t('Resume setup') : progress.complete ? t('Review setup') : t('Complete setup');
                return (
                  <li key={c.id} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3" data-testid="chooser-company" data-company-id={c.id}>
                    <div className="flex min-w-0 flex-1 items-start gap-3">
                      <Building2 className="mt-0.5 h-5 w-5 shrink-0 text-slate-500 dark:text-slate-400" aria-hidden="true" />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{name(c)}</bdi></p>
                        <p className="text-xs text-slate-600 dark:text-slate-400">
                          <bdi>{country(c.countryCode) || c.countryCode}</bdi>
                          {' · '}<span data-testid="chooser-readiness">{progress.total === 0 ? t('No checklist yet') : t('{done} of {total} done', { done: progress.done, total: progress.total })}</span>
                          {c.openDraftId && <>{' · '}<span className="font-semibold text-amber-800 dark:text-amber-200" data-testid="chooser-draft">{t('Draft in progress')}</span></>}
                        </p>
                        {progress.total > 0 && (
                          <div className="mt-1.5 h-1.5 max-w-xs overflow-hidden rounded-full bg-slate-200 dark:bg-white/10" aria-hidden="true">
                            <div className={`h-full rounded-full ${progress.complete ? 'bg-emerald-600' : 'bg-sapphire'}`} style={{ width: `${progress.percent}%` }} />
                          </div>
                        )}
                      </div>
                    </div>
                    <Link href={completeCompanyHref(c.id, c.openDraftId)} className={c.openDraftId || !progress.complete ? 'btn-primary' : 'btn-secondary'}>
                      {action}<span className="sr-only"> <bdi>{name(c)}</bdi></span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
      </section>
    </div>
  );
}
