'use client';

import { useState } from 'react';
import type { NineBox, NineBoxPlacement } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { BANDS, bandLabel, nineBoxCellLabel, nineBoxRows, potentialLabel, personName } from '../../lib/succession';

/**
 * The 9-box (SUC-03, SUC-08): potential (rows, High at the top) against performance (columns, Low → High in reading
 * order). The grid carries the document's direction, so in Arabic CSS grid mirrors it and High performance sits on
 * the left. Every placement opens its inputs — nothing is a hidden score. People without an eligible appraisal are
 * never placed: they are listed apart as "Insufficient evidence".
 */
export function NineBoxGrid({ data }: { data: NineBox }) {
  const { t, dir, locale } = useLocale();
  const fx = useFormat();
  const [picked, setPicked] = useState<NineBoxPlacement | null>(null);
  const byCode = new Map(data.cells.map((c) => [c.code, c]));
  const rows = nineBoxRows();
  const total = data.cells.reduce((s, c) => s + c.people.length, 0);

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500 dark:text-slate-400">
        {t('suc.ninebox.meta', {
          period: data.reviewPeriod ?? '—', placed: total, unrated: data.unrated.length, missing: data.notAssessed,
          high: fx.number(data.bandRule.highFrom), moderate: fx.number(data.bandRule.moderateFrom),
        })}
      </p>
      <div className="overflow-x-auto">
        <div className="min-w-[18rem]">
          <div className="grid grid-cols-[2.5rem_repeat(3,minmax(0,1fr))] gap-1" dir={dir} data-testid="suc-ninebox" role="table"
            aria-label={t('suc.ninebox.label')}>
            {rows.map((row) => (
              <div key={row[0].potential} className="contents" role="row">
                <div role="rowheader" className="flex items-center justify-center rounded-lg bg-slate-50 p-1 text-caption font-semibold text-slate-600 [writing-mode:vertical-rl] dark:bg-white/[0.03] dark:text-slate-300 sm:text-caption">
                  {t(potentialLabel[row[0].potential])}
                </div>
                {row.map((cell) => {
                  const people = byCode.get(cell.code)?.people ?? [];
                  return (
                    <div key={cell.code} role="cell" data-testid={`suc-ninebox-cell-${cell.code}`} data-count={people.length}
                      className="min-h-[5.5rem] space-y-1 rounded-lg border border-slate-200 bg-white p-1.5 dark:border-white/10 dark:bg-white/[0.03] sm:p-2">
                      <p className="flex items-start justify-between gap-1 text-caption font-semibold leading-tight text-slate-700 dark:text-slate-200 sm:text-caption">
                        <span>{t(nineBoxCellLabel[cell.code])}</span>
                        <span className="tabular-nums text-slate-400">{fx.integer(people.length)}</span>
                      </p>
                      <ul className="space-y-0.5">
                        {people.map((p) => (
                          <li key={p.assessmentId}>
                            <button type="button" data-testid="suc-ninebox-person" data-employee-id={p.employee.employeeId}
                              aria-pressed={picked?.assessmentId === p.assessmentId}
                              className={`w-full truncate rounded px-1 py-0.5 text-start text-caption hover:bg-sapphire/10 ${picked?.assessmentId === p.assessmentId ? 'bg-sapphire/15 font-semibold' : ''}`}
                              onClick={() => setPicked(p)}>
                              {personName(p.employee, locale)}{p.provisional ? ` ${t('suc.ninebox.provisionalMark')}` : ''}
                            </button>
                          </li>
                        ))}
                      </ul>
                    </div>
                  );
                })}
              </div>
            ))}
            <div role="row" className="contents">
              <div aria-hidden="true" />
              {BANDS.map((b) => (
                <div key={b} role="columnheader" className="rounded-lg bg-slate-50 p-1 text-center text-caption font-semibold text-slate-600 dark:bg-white/[0.03] dark:text-slate-300 sm:text-caption">
                  {t(bandLabel[b])}
                </div>
              ))}
            </div>
          </div>
          <div className="mt-1 flex justify-between text-caption text-slate-500" dir={dir}>
            <span>{t('suc.ninebox.axisPotential')}</span>
            <span>{t('suc.ninebox.axisPerformance')}</span>
          </div>
        </div>
      </div>

      {picked && <PlacementInputs placement={picked} onClose={() => setPicked(null)} />}

      <div className="rounded-xl border border-dashed border-slate-300 p-3 dark:border-white/15" data-testid="suc-ninebox-unrated">
        <p className="text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.ninebox.insufficient')}</p>
        <p className="text-caption text-slate-500">{t('suc.ninebox.insufficientHint')}</p>
        {data.unrated.length === 0
          ? <p className="mt-1 text-xs text-slate-500">{t('suc.ninebox.noUnrated')}</p>
          : (
            <ul className="mt-1 flex flex-wrap gap-1">
              {data.unrated.map((p) => (
                <li key={p.assessmentId}>
                  <button type="button" className="rounded-full border border-slate-200 px-2 py-0.5 text-xs hover:border-sapphire dark:border-white/10"
                    data-testid="suc-ninebox-unrated-person" data-employee-id={p.employee.employeeId} onClick={() => setPicked(p)}>
                    {personName(p.employee, locale)}
                  </button>
                </li>
              ))}
            </ul>
          )}
      </div>
    </div>
  );
}

function PlacementInputs({ placement: p, onClose }: { placement: NineBoxPlacement; onClose: () => void }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const rows: Array<[string, string]> = [
    [t('suc.inputs.cell'), p.performanceBand === 'Unrated' ? t('suc.ninebox.insufficient') : t(nineBoxCellLabel[p.nineBoxCell] ?? p.nineBoxCell)],
    [t('suc.inputs.period'), p.reviewPeriod],
    [t('suc.inputs.review'), p.appraisalReviewId ?? t('suc.inputs.noReview')],
    [t('suc.inputs.score'), p.performanceScore == null ? '—' : fx.number(p.performanceScore)],
    [t('suc.inputs.band'), t(bandLabel[p.performanceBand])],
    [t('suc.inputs.bandRule'), p.bandRuleVersion],
    [t('suc.inputs.proposed'), t(potentialLabel[p.proposedPotential])],
    [t('suc.inputs.calibrated'), p.calibratedPotential == null ? t('suc.inputs.notCalibrated') : t(potentialLabel[p.calibratedPotential])],
    [t('suc.inputs.reason'), p.calibrationReason ?? (p.calibratedPotential != null && p.calibratedPotential === p.proposedPotential ? t('suc.inputs.confirmed') : '—')],
    [t('suc.inputs.assessor'), p.assessor ? personName(p.assessor, locale) : '—'],
  ];
  return (
    <section className="wg-card space-y-2 rounded-xl p-3" data-testid="suc-ninebox-inputs" aria-live="polite" aria-label={t('suc.inputs.title')}>
      <div className="flex items-start justify-between gap-2">
        <div>
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{personName(p.employee, locale)}</p>
          <p className="text-caption text-slate-500">{p.provisional ? t('suc.inputs.provisional') : t('suc.inputs.final')}</p>
        </div>
        <button type="button" className="btn-secondary h-7 px-2 text-xs" onClick={onClose}>{t('suc.close')}</button>
      </div>
      <dl className="grid gap-x-4 gap-y-1 text-xs sm:grid-cols-2">
        {rows.map(([k, v]) => (
          <div key={k} className="flex justify-between gap-2 border-b border-slate-100 py-1 dark:border-white/[0.06]">
            <dt className="text-slate-500">{k}</dt>
            <dd className="break-all text-end font-medium text-slate-800 dark:text-slate-100">{v}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
