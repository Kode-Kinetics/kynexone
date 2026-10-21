'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { companySetupApi, type CompanySetupDraftSummary } from '../../api/companySetup';
import { WIZARD_STEPS, type StepId } from '../../lib/companySetupWizard';

/**
 * "Continue where you left off" on Setup → Companies: open create-company drafts, newest first.
 * Silent when there are none or the drafts cannot be read (the wizard itself explains refusals).
 */
export function CompanyDraftsCard() {
  const t = useT();
  const fmt = useFormat();
  const [drafts, setDrafts] = useState<CompanySetupDraftSummary[]>([]);
  useEffect(() => {
    let active = true;
    companySetupApi.drafts().then(list => { if (active) setDrafts(list); }).catch(() => { if (active) setDrafts([]); });
    return () => { active = false; };
  }, []);
  if (drafts.length === 0) return null;
  return (
    <section aria-labelledby="company-drafts-heading" className="mb-4 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]" data-testid="company-drafts-card">
      <h2 id="company-drafts-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Unfinished company setup')}</h2>
      <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{t('These drafts are saved. Resume one to carry on from the step where it stopped.')}</p>
      <ul className="mt-3 space-y-2">
        {drafts.map(d => (
          <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-white px-3 py-2 dark:bg-white/[0.04]">
            <div className="min-w-0">
              <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{d.legalNameEn || t('Untitled company')}</bdi></p>
              <p className="text-xs text-slate-600 dark:text-slate-400">
                {t('Stopped at step {step}: {title}', { step: d.step, title: t(WIZARD_STEPS[(Math.min(Math.max(d.step, 1), 7)) as StepId].title) })}
                {' · '}{t('Saved {when} by {name}', { when: fmt.dateTime(d.updatedAtUtc), name: d.createdByName })}
              </p>
            </div>
            <Link href={`/setup/new-company?draft=${d.id}`} className="btn-primary">{t('Resume')}<span className="sr-only"> {d.legalNameEn}</span></Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
