'use client';

import { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { ArrowRight, Building2, Plus } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { companySetupApi, httpStatus, type CompanySetupContext, type CompanySetupDraftSummary, type ManageableCompany } from '../../api/companySetup';
import { completeCompanyHref, draftHref, planUsage, readinessProgress } from '../../lib/companySetupWizard';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { useCountryName } from './CountryOptions';
import { Notice } from './wizardForm';
import { DraftMeta } from './CompanyDraftsCard';
import { ReadinessDrilldown } from './CompanyReadinessList';
import { refusalNextStep } from './StartStep';

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
  const [confirmation, setConfirmation] = useState<ConfirmState | null>(null);

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

  if (load === 'loading' || redirectTo) return <ChooserSkeleton label={t('Opening Guided setup…')} />;
  if (load === 'forbidden') return (
    <Notice tone="warning" role="alert">
      <p className="font-semibold">{t('Guided setup is for administrators and HR managers.')}</p>
      <p className="mt-1">{t('Ask your group super admin for access, or use the Setup sections your role can open.')}</p>
      <Link href="/" className="btn-secondary mt-3">{t('Go to the dashboard')}</Link>
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
  // One primary action on the page, the most useful next step: an unfinished draft first (a new company, then a
  // company's), then the first company whose checklist is not complete, else creating a company.
  const primary: string = createDrafts.length > 0 ? `draft:${createDrafts[0].id}`
    : companies.find(c => c.openDraftId) ? `company:${companies.find(c => c.openDraftId)!.id}`
    : companies.find(c => !readinessProgress(c).complete) ? `company:${companies.find(c => !readinessProgress(c).complete)!.id}`
    : 'create';
  const discard = (d: CompanySetupDraftSummary) => setConfirmation({
    title: t('Discard this draft?'),
    message: t('“{name}” will be removed from the list of drafts. No company was created from it.', { name: d.legalNameEn || t('Untitled company') }),
    confirmLabel: t('Discard draft'),
    fallbackError: t('The draft could not be discarded. Check your connection and try again.'),
    onConfirm: async () => {
      try { await companySetupApi.discardDraft(d.id); }
      catch { throw new Error(t('The draft could not be discarded. Check your connection and try again.')); }
      setDrafts(list => list.filter(x => x.id !== d.id));
    },
  });

  return (
    <div className="space-y-6 motion-safe:animate-fade-in-fast" data-testid="guided-setup-chooser">
      <ConfirmDialog state={confirmation} onCancel={() => setConfirmation(null)} />
      <div className="min-w-0">
        {showHeading && <h2 className="text-xl font-semibold tracking-tight text-slate-950 dark:text-white">{t('Guided setup')}</h2>}
        <p className={`${showHeading ? 'mt-1 ' : ''}max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400`}>{context.isGroupScope
          ? t('Create a company, or finish setting one up. Your progress is saved as a draft as you go. For an existing company, organisation sections are saved to the company when you press Save & next, and everything else when you press Save all changes. A new company is created only when you press Create company.')
          : t('Finish setting up your company. Your progress is saved as a draft as you go. Organisation sections are saved to the company when you press Save & next, and everything else when you press Save all changes.')}</p>
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
                <p className="mt-1 max-w-xl text-sm leading-6 text-slate-600 dark:text-slate-300">{t('Legal details, branches, organisation, work rules and its administrator, step by step. The company and its setup are created together.')}</p>
                {usage && <p className="mt-2 text-xs font-medium text-slate-600 dark:text-slate-400" data-testid="chooser-plan-usage">{usage.allowed === null
                  ? t('{used, plural, one {# company} other {# companies}} in this workspace', { used: usage.used })
                  : t('{used} of {allowed} companies on your plan', { used: usage.used, allowed: usage.allowed })}</p>}
              </div>
            </div>
            {context.canCreate
              ? <Link href="/setup/new-company" className={`${primary === 'create' ? 'btn-primary' : 'btn-secondary'} shrink-0`}>{t('Create a new company')}<ArrowRight className="h-4 w-4" aria-hidden="true" /></Link>
              : <span className="btn-secondary shrink-0 cursor-not-allowed opacity-60" aria-disabled="true">{t('Create a new company')}</span>}
          </div>
          {!context.canCreate && (
            <div className="mt-3 text-sm text-amber-900 dark:text-amber-200" role="status">
              <p>{context.refusalMessage || t('Your workspace does not allow another company.')}</p>
              <p className="mt-1">{refusalNextStep(context.refusalCode, t)}</p>
            </div>
          )}
          {createDrafts.length > 0 && (
            <ul className="mt-4 space-y-2" aria-label={t('Continue where you left off')} data-testid="chooser-create-drafts">
              {createDrafts.map(d => (
                <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-white px-3 py-2 dark:bg-white/[0.04]">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{d.legalNameEn || t('Untitled company')}</bdi></p>
                    <DraftMeta draft={d} />
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    <button type="button" className="inline-flex h-9 items-center rounded-lg px-3 text-sm font-medium text-rose-700 transition-colors duration-150 hover:bg-rose-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500/50 dark:text-rose-300 dark:hover:bg-rose-500/10" onClick={() => discard(d)}>{t('Discard')}<span className="sr-only"> {d.legalNameEn}</span></button>
                    <Link href={draftHref(d)} className={primary === `draft:${d.id}` ? 'btn-primary' : 'btn-secondary'}>{t('Resume')}<span className="sr-only"> {d.legalNameEn}</span></Link>
                  </div>
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
                    <Link href={completeCompanyHref(c.id, c.openDraftId)} className={`${primary === `company:${c.id}` ? 'btn-primary' : 'btn-secondary'} shrink-0`}>
                      {action}<span className="sr-only"> <bdi>{name(c)}</bdi></span>
                    </Link>
                    {progress.total > 0 && <div className="basis-full ps-8"><ReadinessDrilldown companyId={c.id} complete={progress.complete} testId="chooser-readiness-details" /></div>}
                  </li>
                );
              })}
            </ul>
          )}
      </section>
    </div>
  );
}

/** Holds the chooser's final layout (intro, create card, two company rows) while it loads, so nothing jumps. */
function ChooserSkeleton({ label }: { label: string }) {
  const bar = 'rounded bg-slate-200/80 motion-safe:animate-pulse dark:bg-white/[0.08]';
  return (
    <div role="status" aria-busy="true" className="space-y-6" data-testid="guided-setup-loading">
      <span className="sr-only">{label}</span>
      <div className={`h-4 max-w-xl ${bar}`} aria-hidden="true" />
      <div className="rounded-2xl border border-sapphire/15 p-5 sm:p-6" aria-hidden="true">
        <div className="flex items-start gap-3">
          <div className={`h-10 w-10 shrink-0 rounded-xl ${bar}`} />
          <div className="flex-1 space-y-2"><div className={`h-5 w-48 ${bar}`} /><div className={`h-4 max-w-md ${bar}`} /></div>
          <div className={`hidden h-9 w-44 rounded-lg sm:block ${bar}`} />
        </div>
      </div>
      <div className="space-y-3" aria-hidden="true">
        <div className={`h-5 w-64 ${bar}`} />
        <div className="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-white/10 dark:border-white/10">
          {[0, 1].map(i => (
            <div key={i} className="flex items-center gap-3 px-4 py-3">
              <div className={`h-5 w-5 shrink-0 ${bar}`} />
              <div className="flex-1 space-y-2"><div className={`h-4 w-40 ${bar}`} /><div className={`h-3 w-56 ${bar}`} /></div>
              <div className={`h-9 w-32 rounded-lg ${bar}`} />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
