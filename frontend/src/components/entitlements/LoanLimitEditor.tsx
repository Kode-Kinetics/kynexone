'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { gradeLoanLimitsApi, type GradeLoanLimitRow } from '../../api/loanGovernance';
import type { LoanType } from '../../api/loans';
import { useLocale } from '../../contexts/LocaleContext';
import {
  allowsHousingMultiple, applyFromGradeUpward, applySameForAll, draftFromRow, draftProblem, fillTemplate, localName,
  type GradeEligibilityChoice, type GradeLimitBasis, type GradeLimitDraft,
} from '../../lib/gradeLoanLimits';
import { loanLimitsLink, planLoanLimitPublish, rowsChangedSince } from '../../lib/loanLimitsInGrid';
import { loanErrorMessage } from '../../lib/loanWorkflow';
import { Modal } from '../Modal';
import { EmptyState } from '../ui/EmptyState';

const basisKeys: Record<GradeLimitBasis, string> = {
  Amount: 'Fixed amount',
  MultipleOfBasic: '× basic salary',
  MultipleOfGross: '× gross salary',
  MultipleOfHousing: '× housing allowance',
};

/**
 * One grade's loan limit, in the drawer every other benefit opens. Unlike a benefit it does not wait for the grid's Publish: a
 * loan limit has its own write model (the grade-loan-limits API: a new version from a date, the old one closed the day before),
 * so it has its own Save. The rules are Limits by grade's own — its drafts, checks and request — and nothing is loosened.
 *
 * Lost updates: the API takes only the rows sent and leaves every other grade alone, so only changed grades are sent. The grades
 * being saved are re-read just before the write; if any differs from what the editor opened with, nothing is written and the
 * current limits are shown instead. (The API has no expected-version field, so a change in the few milliseconds between that
 * re-read and the write is not caught here; the server still refuses a second version on the same day.)
 */
export function LoanLimitEditor({ title, grade, loanType, companyId, companyLabel, currency, today, asOf, onClose, onSaved }: {
  /** The drawer's title, named as the grid names every cell's drawer: the benefit, then the grade. */
  title: string; grade: { id: string; code: string };
  loanType: LoanType; companyId: string; companyLabel: string; currency: string; today: string; asOf: string | null;
  onClose: () => void; onSaved: (notice: string) => void;
}) {
  const { t, locale } = useLocale();
  const id = useId();
  const [baseline, setBaseline] = useState<GradeLoanLimitRow[]>([]);
  const [drafts, setDrafts] = useState<GradeLimitDraft[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [effectiveFrom, setEffectiveFrom] = useState(asOf && asOf > today ? asOf : today);
  const live = useRef(true);
  useEffect(() => { live.current = true; return () => { live.current = false; }; }, []);

  const params = useMemo(() => ({ loanTypeId: loanType.id, companyId: companyId || undefined, asOf: asOf || undefined }), [loanType.id, companyId, asOf]);
  const show = useCallback((rows: GradeLoanLimitRow[]) => {
    const sorted = [...rows].sort((a, b) => a.level - b.level);
    setBaseline(sorted); setDrafts(sorted.map(draftFromRow));
  }, []);
  const readRows = useCallback(async () => {
    const rows = await gradeLoanLimitsApi.list(params);
    // Anything but a list is a failed read, never "no grades".
    if (!Array.isArray(rows)) throw new Error('Unexpected grade-limits response');
    return rows;
  }, [params]);
  const open = useCallback(async () => {
    setLoading(true); setLoadError('');
    try { const rows = await readRows(); if (live.current) show(rows); }
    catch (e) { if (live.current) setLoadError(loanErrorMessage(e, t('Unable to load the grade limits.'))); }
    finally { if (live.current) setLoading(false); }
  }, [readRows, show, t]);
  useEffect(() => { void open(); }, [open]);

  const label = (d: { gradeName: string; gradeNameAr?: string | null; gradeCode: string }) => localName(locale, d.gradeName, d.gradeNameAr) || d.gradeCode;
  const draft = drafts.find(d => d.gradeId === grade.id);
  const changed = drafts.filter(d => d.dirty);
  const firstProblem = changed.map(d => ({ d, problem: draftProblem(d) })).find(p => p.problem);
  const update = (patch: Partial<GradeLimitDraft>) => setDrafts(list => list.map(d => (d.gradeId === grade.id ? { ...d, ...patch, dirty: true } : d)));
  const noFigures = !!draft && draft.eligible !== 'yes';

  const save = async () => {
    setError('');
    const plan = planLoanLimitPublish({ drafts, loanTypeId: loanType.id, companyId, effectiveFrom });
    if (!plan.ok) { setError(plan.grade ? `${label(plan.grade)}: ${t(plan.key)}` : t(plan.key)); return; }
    setSaving(true);
    try {
      const now = await readRows();
      const moved = rowsChangedSince(baseline, now, plan.changed.map(d => d.gradeId));
      if (moved.length) {
        // Somebody else published first. Show what is in force now; the edit is not carried over onto it.
        if (!live.current) return;
        show(now);
        setError(fillTemplate(t('Someone else changed {grades} while you were editing, so nothing was saved. The current limits are shown; check them, then save again.'),
          { grades: moved.map(id => { const row = now.find(r => r.gradeId === id) ?? baseline.find(r => r.gradeId === id); return row ? label(row) : id; }).join(', ') }));
        return;
      }
      const result = await gradeLoanLimitsApi.publish(plan.request);
      if (!live.current) return;
      onSaved(fillTemplate(t('Published limits for {count} grade(s), starting {date}. Loans already requested keep the limit they were assessed against.'),
        { count: result?.changed ?? plan.changed.length, date: effectiveFrom }));
    } catch (e) { if (live.current) setError(loanErrorMessage(e, t('Unable to publish the grade limits.'))); }
    finally { if (live.current) setSaving(false); }
  };

  return <Modal isOpen placement="end" size="md" title={title} onClose={onClose} closeLabel={t('Close')}
    footer={<>
      <button type="button" className="btn-secondary" onClick={onClose}>{t('Done editing')}</button>
      <button type="button" className="btn-primary" disabled={saving || loading || !!loadError || changed.length === 0 || !!firstProblem} onClick={() => void save()}>
        {saving ? t('Publishing…') : t('Save loan limit')}</button>
    </>}>
    <div className="space-y-3">
      <p data-purpose className="text-body text-fg-muted">{t('Set how much each grade can borrow for a loan type. The company loan policy still decides the rules; the stricter of the two always applies.')}</p>
      {!loanType.gradeLimited && <div role="status" className="space-y-1 rounded-lg border border-warning/40 bg-warning-soft p-2 text-meta text-warning-fg">
        <p>{t('Grade limits are switched off for this loan type, so these limits are not applied to requests yet.')}</p>
        <Link href={loanLimitsLink(loanType.id, companyId)} className="font-semibold underline">{t('Open Loan policies for this loan type')}</Link>
      </div>}
      {companyId && <p className="text-meta text-fg-muted">{t('Values you change here become this company\'s own limits and take priority over the group default.')}</p>}

      {loading ? <div className="py-8 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-primary border-t-transparent" role="status" aria-label={t('Loading grade limits')} /></div>
        : loadError ? <div role="alert" className="space-y-2 text-body"><p className="text-danger-fg">{loadError}</p><button type="button" className="btn-secondary" onClick={() => void open()}>{t('Try again')}</button></div>
        : !draft ? <EmptyState kind="first-use" title={t('There are no active grades')} description={t('Add grades in Setup, then set their loan limits here.')}
          action={{ label: t('Add the first grade'), href: '/setup?tab=grades' }} />
        : <>
          <fieldset className="grid gap-3 sm:grid-cols-2" disabled={saving}>
            <div className="text-sm">
              <label htmlFor={`${id}-eligible`}>{t('Eligible')}</label>
              <select id={`${id}-eligible`} className="select mt-1 w-full" value={draft.eligible} onChange={e => update({ eligible: e.target.value as GradeEligibilityChoice })}>
                {draft.eligible === 'unset' && <option value="unset">{t('Not set')}</option>}
                <option value="yes">{t('Eligible')}</option>
                <option value="no">{t('Not eligible')}</option>
              </select>
            </div>
            <div className="text-sm">
              <label htmlFor={`${id}-basis`}>{t('Limit basis')}</label>
              <select id={`${id}-basis`} className="select mt-1 w-full" disabled={noFigures} value={draft.basis} onChange={e => update({ basis: e.target.value as GradeLimitBasis })}>
                {(Object.keys(basisKeys) as GradeLimitBasis[]).filter(basis => basis !== 'MultipleOfHousing' || allowsHousingMultiple(loanType) || draft.basis === basis)
                  .map(basis => <option key={basis} value={basis}>{t(basisKeys[basis])}</option>)}
              </select>
            </div>
            <div className="text-sm">
              <label htmlFor={`${id}-per-loan`}>{t('Per-loan maximum')}</label>
              <input id={`${id}-per-loan`} type="number" min="0" step={draft.basis === 'Amount' ? '0.01' : '0.25'} inputMode="decimal" className="input mt-1 w-full" disabled={noFigures}
                placeholder={noFigures ? '—' : t('No limit')} value={draft.perLoan} onChange={e => update({ perLoan: e.target.value })} />
              {!noFigures && <span className="block text-xs text-fg-muted">{draft.basis === 'Amount' ? currency
                : t(draft.basis === 'MultipleOfBasic' ? 'months of basic salary' : draft.basis === 'MultipleOfHousing' ? 'Months of housing allowance' : 'months of gross salary')}</span>}
            </div>
            <div className="text-sm">
              <label htmlFor={`${id}-outstanding`}>{t('Total outstanding maximum')}</label>
              <input id={`${id}-outstanding`} type="number" min="0" step="0.01" inputMode="decimal" className="input mt-1 w-full" disabled={noFigures}
                placeholder={noFigures ? '—' : t('No limit')} value={draft.maxOutstanding} onChange={e => update({ maxOutstanding: e.target.value })} />
              {!noFigures && <span className="block text-xs text-fg-muted">{currency}</span>}
            </div>
          </fieldset>
          <p className="text-meta text-fg-muted">
            {draft.dirty ? t('Changed — not published yet')
              : !draft.hasCell ? t('Not set')
              : `${t(companyId && draft.isCompanyOverride ? 'Company limit' : 'Group default')}${draft.effectiveFrom ? ` · ${fillTemplate(t('since {date}'), { date: draft.effectiveFrom })}` : ''}`}
          </p>

          <div className="text-sm">
            <label htmlFor={`${id}-from`}>{t('Effective from')}</label>
            <input id={`${id}-from`} type="date" className="input mt-1 w-full" min={today} value={effectiveFrom} onChange={e => setEffectiveFrom(e.target.value)} />
          </div>
          <p className="text-meta text-fg-muted">{fillTemplate(t('Publishing for: {scope}. Empty figures mean no grade limit on that figure. Loans already requested keep the limit they were assessed against.'), { scope: companyLabel })}</p>
          <p className="text-meta text-fg-muted">{t('Loan limits are saved on their own, as a new version from the date you choose. They do not go through the Publish button below the grid.')}</p>
          {changed.length > 0 && <p className="text-meta font-medium text-fg">
            {fillTemplate(t('You are changing {count} value(s) for {grades} grade(s), starting {date}.'), { count: changed.length, grades: changed.length, date: effectiveFrom })}</p>}

          {firstProblem && <p role="alert" className="text-sm text-danger-fg">{firstProblem.d.gradeId === grade.id ? t(firstProblem.problem!) : `${label(firstProblem.d)}: ${t(firstProblem.problem!)}`}</p>}
          {error && <p role="alert" className="text-sm text-danger-fg">{error}</p>}
          <div className="flex flex-wrap gap-2 border-t border-line pt-3">
            <button type="button" className="btn-secondary btn-sm" disabled={saving} onClick={() => setDrafts(list => applySameForAll(list, grade.id))}>{t('Same for all grades')}</button>
            <button type="button" className="btn-secondary btn-sm" disabled={saving} onClick={() => setDrafts(list => applyFromGradeUpward(list, grade.id))}>{fillTemplate(t('From {grade} upward'), { grade: grade.code })}</button>
            <button type="button" className="btn-secondary btn-sm" disabled={saving || !baseline.some(r => r.gradeId === grade.id)}
              onClick={() => setDrafts(list => list.map(d => { const row = baseline.find(r => r.gradeId === d.gradeId); return d.gradeId === grade.id && row ? draftFromRow(row) : d; }))}>{t('Undo changes to this grade')}</button>
          </div>
        </>}
    </div>
  </Modal>;
}
