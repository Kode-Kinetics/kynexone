'use client';

import { useCallback, useEffect, useState } from 'react';
import { successionApi, type DevelopmentPlan, type PersonRef } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { onTrackReason, planStatusLabel, planTone, progressParts, successionError } from '../../lib/succession';
import { StatusChip } from '../StatusChip';
import { DevelopmentPlanPanel } from './DevelopmentPlanPanel';
import { EmptyBlock, ErrorBlock, LoadingRows, PersonName, PersonPicker, ProgressBar, SectionCard, type SuccessionAccess } from './SuccessionParts';

/**
 * Development plans across the caller's scope (HR: their companies; a manager: their reporting line). Exceptions
 * first: off-track plans lead. Choosing a row — or a person — opens that person's plan right here.
 */
export function PlansPanel({ access }: { access: SuccessionAccess }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [rows, setRows] = useState<DevelopmentPlan[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scope, setScope] = useState<'open' | 'all'>('open');
  const [person, setPerson] = useState<PersonRef | null>(null);

  const load = useCallback(async () => {
    setError(null);
    setRows(null);
    try {
      const list = await successionApi.plans({ status: scope });
      setRows([...list].sort((a, b) => Number(a.progress.onTrack) - Number(b.progress.onTrack) || a.nextReviewOn.localeCompare(b.nextReviewOn)));
    } catch (e) { setError(successionError(e, t)); }
  }, [scope, t]);
  useEffect(() => { void load(); }, [load]);

  if (person) {
    return (
      <div className="space-y-3" data-testid="suc-plans-person">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-sm"><PersonName person={person} /></p>
          <button type="button" className="btn-secondary text-xs" onClick={() => { setPerson(null); void load(); }}>{t('suc.plans.back')}</button>
        </div>
        <DevelopmentPlanPanel employeeId={person.employeeId} access={access} />
      </div>
    );
  }

  return (
    <div className="space-y-3" data-testid="suc-plans">
      {access.devplansManage && (
        <SectionCard title={t('suc.plans.openFor')} description={t('suc.plans.openForHint')}>
          <PersonPicker id="suc-plans-person" testId="suc-plans-person" value={null} onChange={setPerson} label={t('suc.plans.employee')} />
        </SectionCard>
      )}
      <div className="flex flex-wrap items-center gap-2 text-xs" role="radiogroup" aria-label={t('suc.plans.filter')}>
        {(['open', 'all'] as const).map((s) => (
          <label key={s} className="flex items-center gap-1">
            <input type="radio" name="suc-plans-scope" checked={scope === s} onChange={() => setScope(s)} />
            {s === 'open' ? t('suc.plans.current') : t('suc.plans.all')}
          </label>
        ))}
      </div>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !rows && <LoadingRows label={t('suc.loading')} />}
      {rows?.length === 0 && <EmptyBlock title={t('suc.plans.emptyTitle')} body={access.devplansManage ? t('suc.plans.emptyBodyManage') : t('suc.plans.emptyBody')} />}
      {rows && rows.length > 0 && (
        <ul className="space-y-2">
          {rows.map((p) => {
            const progress = progressParts(p.progress);
            return (
              <li key={p.id}>
                <button type="button" className="grid w-full gap-2 rounded-xl border border-slate-200 bg-white p-3 text-start hover:border-sapphire/60 dark:border-white/10 dark:bg-white/[0.03] md:grid-cols-[minmax(0,1.4fr)_minmax(0,1.4fr)_minmax(0,1fr)] md:items-center"
                  data-testid="suc-plan-row" data-plan-id={p.id} onClick={() => setPerson(p.employee)}>
                  <span className="min-w-0"><PersonName person={p.employee} /><span className="block truncate text-xs text-slate-500">{p.purpose}</span></span>
                  <span className="flex flex-wrap items-center gap-1 text-xs">
                    <StatusChip label={t(planStatusLabel[p.status])} tone={planTone(p.status, p.progress.onTrack)} />
                    <span className="text-slate-600 dark:text-slate-300">{t(onTrackReason(p.progress))}</span>
                    <span className="w-full text-slate-500">{t('suc.plan.nextReview', { date: fx.date(p.nextReviewOn) })}</span>
                  </span>
                  <span className="space-y-1">
                    <span className="block text-xs text-slate-600 dark:text-slate-300">{t(progress.key, progress.params)}</span>
                    <ProgressBar percent={p.progress.percentComplete ?? 0} label={t('suc.plan.progressLabel')} />
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
