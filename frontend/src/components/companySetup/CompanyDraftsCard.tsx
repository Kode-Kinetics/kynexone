'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { companySetupApi, type CompanySetupDraftSummary } from '../../api/companySetup';
import { draftHref, stepCopy, type StepId } from '../../lib/companySetupWizard';

/**
 * One line under a draft's name: where it stopped, who started it and how long ago it was last saved. The time is
 * relative ("5 minutes ago"), so it reads the same in every time zone; the exact time is in the tooltip.
 */
export function DraftMeta({ draft }: { draft: CompanySetupDraftSummary }) {
  const t = useT();
  const fmt = useFormat();
  const step = (Math.min(Math.max(draft.step, 1), 7)) as StepId;
  return (
    <p className="text-xs text-slate-600 dark:text-slate-400">
      {t('Stopped at step {step}: {title}', { step: draft.step, title: t(stepCopy(step, draft.existingCompanyId ? 'existing' : 'create').title) })}
      {' · '}
      <span title={fmt.dateTime(draft.updatedAtUtc)}>{t('Started by {name}, saved {when}', { name: draft.createdByName, when: fmt.relative(draft.updatedAtUtc) })}</span>
    </p>
  );
}

/**
 * "Continue where you left off" on Setup → Companies: open drafts, newest first. The newest carries the one primary
 * button. Silent when there are none or the drafts cannot be read (the wizard itself explains refusals).
 */
export function CompanyDraftsCard() {
  const t = useT();
  const [drafts, setDrafts] = useState<CompanySetupDraftSummary[]>([]);
  useEffect(() => {
    let active = true;
    companySetupApi.drafts().then(list => { if (active) setDrafts(list); }).catch(() => { if (active) setDrafts([]); });
    return () => { active = false; };
  }, []);
  if (drafts.length === 0) return null;
  return (
    <section aria-labelledby="company-drafts-heading" className="mb-4 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 motion-safe:animate-fade-in-fast dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]" data-testid="company-drafts-card">
      <h2 id="company-drafts-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Unfinished company setup')}</h2>
      <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{t('These drafts are saved. Resume one to carry on from the step where it stopped.')}</p>
      <ul className="mt-3 space-y-2">
        {drafts.map((d, index) => (
          <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-white px-3 py-2 dark:bg-white/[0.04]">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{d.legalNameEn || t('Untitled company')}</bdi></p>
              <DraftMeta draft={d} />
            </div>
            <Link href={draftHref(d)} className={index === 0 ? 'btn-primary' : 'btn-secondary'}>{t('Resume')}<span className="sr-only"> {d.legalNameEn}</span></Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
