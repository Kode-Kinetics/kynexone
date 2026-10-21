'use client';

import { Building2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import type { CompanySetupDraftBody, ManageableCompany } from '../../api/companySetup';
import { readinessProgress } from '../../lib/companySetupWizard';
import { useCountryName } from './CountryOptions';
import { Notice } from './wizardForm';

/**
 * Existing mode, step 1 (Addendum A): instead of "Start from", what this company has today, how far its setup is,
 * and what the wizard will and will not do. Nothing is written until Start.
 */
export function CompanySummaryStep({ body, company, draft, onDiscard }: {
  body: CompanySetupDraftBody;
  company: ManageableCompany;
  /** Set once this screen holds a draft. */
  draft: { createdByName: string; createdAtUtc: string } | null;
  onDiscard?: () => void;
}) {
  const { t, locale } = useLocale();
  const fmt = useFormat();
  const country = useCountryName();
  const progress = readinessProgress(company);
  const nameAr = body.company.legalNameAr?.trim() || company.legalNameAr?.trim();
  const own = <T extends { existingId?: string | null }>(rows: readonly T[]) => rows.filter(r => !!r.existingId).length;
  const locations = body.branches.flatMap(b => b.locations);
  const added = body.branches.length + locations.length + body.costCentres.length - own(body.branches) - own(locations) - own(body.costCentres);
  const facts: { label: string; value: string }[] = [
    { label: t('Country'), value: country(body.company.countryCode) || body.company.countryCode },
    { label: t('Salary currency'), value: body.company.currency || '—' },
    { label: t('Commercial registration (CR) number'), value: body.company.registrationNumber || t('Not entered yet') },
  ];
  return (
    <div className="space-y-6" data-testid="company-summary">
      <section className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
        <div className="flex min-w-0 items-start gap-3">
          <Building2 className="mt-0.5 h-6 w-6 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
          <div className="min-w-0">
            <p className="text-base font-semibold text-slate-950 dark:text-white"><bdi>{locale === 'ar' && nameAr ? nameAr : body.company.legalNameEn || company.legalNameEn}</bdi></p>
            {nameAr && locale !== 'ar' && <p className="text-sm text-slate-600 dark:text-slate-400" dir="rtl" lang="ar">{nameAr}</p>}
          </div>
        </div>
        <dl className="mt-4 grid gap-3 sm:grid-cols-3">
          {facts.map(f => (
            <div key={f.label} className="min-w-0">
              <dt className="text-xs text-slate-500 dark:text-slate-400">{f.label}</dt>
              <dd className="truncate text-sm font-medium text-slate-900 dark:text-white"><bdi>{f.value}</bdi></dd>
            </div>
          ))}
        </dl>
      </section>

      <section aria-labelledby="ccw-summary-readiness" className="space-y-2">
        <h3 id="ccw-summary-readiness" className="text-base font-semibold text-slate-900 dark:text-white">{t('Setup progress')}</h3>
        <p className="text-sm text-slate-700 dark:text-slate-300" data-testid="summary-readiness">{progress.total === 0 ? t('No setup checklist yet for this company.') : t('{done} of {total} done', { done: progress.done, total: progress.total })}</p>
        {progress.total > 0 && (
          <div className="h-2 max-w-md overflow-hidden rounded-full bg-slate-200 dark:bg-white/10" aria-hidden="true">
            <div className={`h-full rounded-full ${progress.complete ? 'bg-emerald-600' : 'bg-sapphire'}`} style={{ width: `${progress.percent}%` }} />
          </div>
        )}
        <p className="text-sm text-slate-600 dark:text-slate-400" data-testid="summary-counts">
          {t('Already set up — branches: {branches}, locations: {locations}, cost centres: {costCentres}.', { branches: own(body.branches), locations: own(locations), costCentres: own(body.costCentres) })}
          {added > 0 && <>{' '}{t('Suggested from the country pack: {count}. You review each one.', { count: added })}</>}
        </p>
      </section>

      <Notice tone="info">
        <p className="font-semibold">{t('What this does')}</p>
        <ul className="mt-1 list-disc space-y-0.5 ps-5">
          <li>{t('Adds what is missing and updates what you edit. You see every change before anything is saved.')}</li>
          <li>{t('Never removes anything. To remove a branch, cost centre, location or administrator, use its own Setup tab.')}</li>
          <li>{t('The country and salary currency stay as they are.')}</li>
        </ul>
      </Notice>

      {draft
        ? <div className="flex flex-wrap items-center justify-between gap-3" data-testid="summary-draft">
            <p className="text-sm text-slate-700 dark:text-slate-300">{t('You are continuing a draft started by {name} on {when}.', { name: draft.createdByName, when: fmt.dateTime(draft.createdAtUtc) })}</p>
            {onDiscard && <button type="button" className="btn-secondary" onClick={onDiscard}>{t('Discard draft')}</button>}
          </div>
        : <p className="text-sm text-slate-600 dark:text-slate-400">{t('Nothing is saved until you press Start. The draft is then kept until you save the changes or discard it.')}</p>}
    </div>
  );
}
