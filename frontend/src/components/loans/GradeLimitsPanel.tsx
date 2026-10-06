'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { gradeLoanLimitsApi, type CompanyWithoutPolicy, type GradeLoanLimitRow } from '../../api/loanGovernance';
import type { LoanType } from '../../api/loans';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { loanErrorMessage, localDateToday } from '../../lib/loanWorkflow';
import {
  applyFromGradeUpward, applySameForAll, companiesWithoutPolicyFromError, draftFromRow, draftProblem, fillTemplate, inputFromDraft, isHousingAdvance, localName,
  missingGradesFromError, unsetGradeNames, type GradeEligibilityChoice, type GradeLimitBasis, type GradeLimitDraft,
} from '../../lib/gradeLoanLimits';

const basisKeys: Record<GradeLimitBasis, string> = {
  Amount: 'Fixed amount',
  MultipleOfBasic: '× basic salary',
  MultipleOfGross: '× gross salary',
  MultipleOfHousing: '× housing allowance',
};

/**
 * "Limits by grade" for one loan type: how much each grade may borrow. The company loan policy still
 * decides the rules; the strictest of policy and grade applies. Publishing never edits a limit in
 * place — it closes the current one the day before the new effective date and starts the new one.
 */
export function GradeLimitsPanel({ loanTypes, companies, initialLoanTypeId, onGradeLimitedChanged }: {
  loanTypes: LoanType[];
  companies: { id: string; name: string }[];
  initialLoanTypeId?: string;
  onGradeLimitedChanged: (loanTypeId: string, gradeLimited: boolean) => void;
}) {
  const { t, locale } = useLocale();
  const { currencyCode } = useTenantSettings();
  const { isGroupScope, selectedCompanyId } = useCompany();
  const [loanTypeId, setLoanTypeId] = useState(initialLoanTypeId || loanTypes[0]?.id || '');
  // '' = the group-wide grid; only group-scope admins may publish it.
  const [companyId, setCompanyId] = useState(isGroupScope ? '' : (selectedCompanyId ?? companies[0]?.id ?? ''));
  const [drafts, setDrafts] = useState<GradeLimitDraft[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [effectiveFrom, setEffectiveFrom] = useState(localDateToday());
  const [publishing, setPublishing] = useState(false);
  const [toggling, setToggling] = useState(false);
  const [missingFromServer, setMissingFromServer] = useState<string[]>([]);
  // Companies that would stop offering the type once it is limited by grade (no policy of their own or group).
  const [stopOffering, setStopOffering] = useState<CompanyWithoutPolicy[]>([]);
  const [helperGradeId, setHelperGradeId] = useState('');
  // The date the grid is shown as of. Today, or the effective date of the last publish (so a future-dated
  // publish shows what will apply, with a "Changes from <date>" note).
  const [viewAsOf, setViewAsOf] = useState<string | null>(null);
  // Only the latest request may write state: a slower earlier response must never overwrite a newer grid
  // or the user's unsaved edits.
  const requestSeq = useRef(0);
  const abortRef = useRef<AbortController | null>(null);
  const gradeLabel = (d: Pick<GradeLimitDraft, 'gradeName' | 'gradeNameAr' | 'gradeCode'>) => localName(locale, d.gradeName, d.gradeNameAr) || d.gradeCode;

  useEffect(() => { if (!loanTypeId && loanTypes[0]) setLoanTypeId(loanTypes[0].id); }, [loanTypeId, loanTypes]);
  useEffect(() => { if (!isGroupScope && !companyId && companies[0]) setCompanyId(companies[0].id); }, [isGroupScope, companyId, companies]);

  const loanType = loanTypes.find(type => type.id === loanTypeId);
  const money = useCallback((n: number) => n.toLocaleString('en-US', { style: 'currency', currency: currencyCode, maximumFractionDigits: 2 }), [currencyCode]);

  const applyRows = useCallback((rows: GradeLoanLimitRow[]) => {
    const next = [...rows].sort((a, b) => a.level - b.level).map(draftFromRow);
    setDrafts(next);
    setHelperGradeId(current => (next.some(d => d.gradeId === current) ? current : next[0]?.gradeId ?? ''));
  }, []);

  const load = useCallback(async (asOf?: string | null) => {
    abortRef.current?.abort();
    const seq = ++requestSeq.current;
    if (!loanTypeId || (!isGroupScope && !companyId)) { setDrafts([]); setLoading(false); return; }
    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true); setLoadError('');
    try {
      const rows = await gradeLoanLimitsApi.list({ loanTypeId, companyId: companyId || undefined, asOf: asOf || undefined }, controller.signal);
      if (seq !== requestSeq.current) return;
      // Anything but a list is a failed load, never "no grades": an empty grid would read as a fact.
      if (!Array.isArray(rows)) throw new Error('Unexpected grade-limits response');
      applyRows(rows);
    } catch (e) {
      if (seq !== requestSeq.current || controller.signal.aborted) return;
      setDrafts([]); setLoadError(loanErrorMessage(e, t('Unable to load the grade limits.')));
    } finally { if (seq === requestSeq.current) setLoading(false); }
  }, [loanTypeId, companyId, isGroupScope, t, applyRows]);
  useEffect(() => { setMissingFromServer([]); setNotice(''); setError(''); setViewAsOf(null); void load(); }, [load]);
  useEffect(() => () => abortRef.current?.abort(), []);

  const update = (gradeId: string, patch: Partial<GradeLimitDraft>) =>
    setDrafts(list => list.map(d => (d.gradeId === gradeId ? { ...d, ...patch, dirty: true } : d)));

  const changed = drafts.filter(d => d.dirty);
  const problems = useMemo(() => drafts.map(d => ({ draft: d, problem: draftProblem(d) })).filter(p => p.problem), [drafts]);
  const unset = unsetGradeNames(drafts, locale);
  const helperGrade = drafts.find(d => d.gradeId === helperGradeId);

  const publish = async () => {
    setError(''); setNotice('');
    if (!changed.length) { setError(t('Change at least one grade before publishing.')); return; }
    if (!effectiveFrom) { setError(t('Choose the date the new limits start.')); return; }
    if (problems.length) { setError(`${gradeLabel(problems[0].draft)}: ${t(problems[0].problem!)}`); return; }
    setPublishing(true);
    try {
      const result = await gradeLoanLimitsApi.publish({ loanTypeId, companyId: companyId || null, effectiveFrom, rows: changed.map(inputFromDraft) });
      // The response is the grid as of effectiveFrom: show it, and cancel any older load still in flight.
      abortRef.current?.abort();
      requestSeq.current++;
      setLoading(false);
      if (Array.isArray(result?.rows)) applyRows(result.rows); else await load(effectiveFrom);
      setViewAsOf(effectiveFrom > localDateToday() ? effectiveFrom : null);
      setNotice(fillTemplate(t('Published limits for {count} grade(s), starting {date}. Loans already requested keep the limit they were assessed against.'), { count: result?.changed ?? changed.length, date: effectiveFrom }));
    } catch (e) { setError(loanErrorMessage(e, t('Unable to publish the grade limits.'))); }
    finally { setPublishing(false); }
  };

  const toggleGradeLimited = async (enable: boolean, confirmStopOffering = false) => {
    if (!loanType) return;
    setError(''); setNotice(''); setMissingFromServer([]); setStopOffering([]);
    setToggling(true);
    try {
      await gradeLoanLimitsApi.setGradeLimited(loanType.id, enable, confirmStopOffering);
      onGradeLimitedChanged(loanType.id, enable);
      setNotice(t(enable ? 'Grade limits now apply to new requests for this loan type.' : 'Grade limits no longer apply to new requests for this loan type.'));
    } catch (e) {
      const missing = missingGradesFromError(e, locale);
      const uncovered = companiesWithoutPolicyFromError(e);
      if (missing.length) setMissingFromServer(missing);
      else if (uncovered.length) setStopOffering(uncovered);
      else setError(loanErrorMessage(e, t('Unable to change grade limiting for this loan type.')));
    } finally { setToggling(false); }
  };

  const companyLabel = companyId ? companies.find(c => c.id === companyId)?.name ?? '' : t('All companies (group default)');

  return <section className="surface space-y-4 p-4" aria-labelledby="grade-limits-heading">
    <div>
      <h2 id="grade-limits-heading" className="font-semibold">{t('Limits by grade')}</h2>
      <p className="text-sm text-slate-500">{t('Set how much each grade can borrow for a loan type. The company loan policy still decides the rules; the stricter of the two always applies.')}</p>
    </div>

    <div className="flex flex-wrap items-end gap-3">
      <label className="text-sm">{t('Loan type')}
        <select className="select ms-2" value={loanTypeId} onChange={e => setLoanTypeId(e.target.value)}>
          {loanTypes.length === 0 && <option value="">{t('No loan types yet')}</option>}
          {loanTypes.map(type => <option key={type.id} value={type.id}>{localName(locale, type.nameEn, type.nameAr)}</option>)}
        </select>
      </label>
      <label className="text-sm">{t('Applies to')}
        <select className="select ms-2" value={companyId} onChange={e => setCompanyId(e.target.value)}>
          {isGroupScope && <option value="">{t('All companies (group default)')}</option>}
          {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
    </div>
    {companyId && <p className="text-xs text-slate-500">{t('Values you change here become this company\'s own limits and take priority over the group default.')}</p>}

    {loanType && <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
      <label className="flex items-center gap-2 text-sm font-semibold">
        <input type="checkbox" checked={!!loanType.gradeLimited} disabled={toggling} onChange={e => void toggleGradeLimited(e.target.checked)} />
        {t('Limit this loan type by grade')}
      </label>
      <p className="mt-1 ps-6 text-xs text-slate-500">{t(loanType.gradeLimited
        ? 'On: every request is checked against the limit for the employee\'s grade.'
        : 'Off: requests are checked against the company loan policy only.')}</p>
      <p className="mt-1 ps-6 text-xs text-slate-500">{t('Requests already waiting for approval are checked against the grade limits again when they are approved, and can be refused or reduced then.')}</p>
      {missingFromServer.length > 0 && <p role="alert" className="mt-2 ps-6 text-sm text-amber-700 dark:text-amber-300">
        {fillTemplate(t('Set a limit for every grade before switching this on. Missing: {grades}.'), { grades: missingFromServer.join(', ') })}
      </p>}
      {stopOffering.length > 0 && <div role="alert" className="mt-2 space-y-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-500/40 dark:bg-amber-500/10">
        <p className="font-semibold text-amber-800 dark:text-amber-200">{t('These companies have no loan policy for this loan type. Once it is limited by grade, their employees can no longer apply for it:')}</p>
        <ul className="list-disc ps-5">{stopOffering.map(c => <li key={c.id}>{c.name}</li>)}</ul>
        <p>{t('Publish a loan policy for them first, or confirm that they stop offering this loan type.')}</p>
        <div className="flex flex-wrap gap-2">
          <button type="button" className="btn-primary" disabled={toggling} onClick={() => void toggleGradeLimited(true, true)}>{t('Limit by grade and stop offering it there')}</button>
          <button type="button" className="btn-secondary" disabled={toggling} onClick={() => setStopOffering([])}>{t('Cancel')}</button>
        </div>
      </div>}
      {loanType.gradeLimited && unset.length > 0 && !loading && <p role="alert" className="mt-2 ps-6 text-sm text-amber-700 dark:text-amber-300">
        {fillTemplate(t('Employees in these grades can\'t apply until a limit is set: {grades}.'), { grades: unset.join(', ') })}
      </p>}
    </div>}

    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {notice && <p role="status" className="text-sm text-emerald-700">{notice}</p>}
    {viewAsOf && <p role="status" className="rounded-md border border-sapphire/30 bg-sapphire/5 p-2 text-sm">
      {fillTemplate(t('Changes from {date}: the grid shows the limits that apply from that date. Until then the current limits stay in force.'), { date: viewAsOf })}
      {' '}<button type="button" className="text-sapphire underline" onClick={() => { setViewAsOf(null); void load(); }}>{t('Show current limits')}</button>
    </p>}

    {loading ? <div className="py-8 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" role="status" aria-label={t('Loading grade limits')} /></div>
      : loadError ? <div role="alert" className="space-y-2 text-sm"><p className="text-red-600">{loadError}</p><button type="button" className="btn-secondary" onClick={() => void load()}>{t('Try again')}</button></div>
      : !loanTypeId ? <p className="text-sm text-slate-500">{t('Create a loan type first, then set its limits by grade.')}</p>
      : drafts.length === 0 ? <p className="text-sm text-slate-500">{t('No active grades yet. Add grades in Setup, then set their loan limits here.')}</p>
      : <>
        <div className="flex flex-wrap items-end gap-2 text-sm">
          <label>{t('Copy the values of')}
            <select className="select ms-2" value={helperGradeId} onChange={e => setHelperGradeId(e.target.value)}>
              {drafts.map(d => <option key={d.gradeId} value={d.gradeId}>{gradeLabel(d)}</option>)}
            </select>
          </label>
          <button type="button" className="btn-secondary" disabled={!helperGrade} onClick={() => setDrafts(list => applySameForAll(list, helperGradeId))}>{t('Same for all grades')}</button>
          <button type="button" className="btn-secondary" disabled={!helperGrade} onClick={() => setDrafts(list => applyFromGradeUpward(list, helperGradeId))}>{fillTemplate(t('From {grade} upward'), { grade: helperGrade ? gradeLabel(helperGrade) : '' })}</button>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead><tr>{[t('Grade'), t('Eligible'), t('Limit basis'), t('Per-loan maximum'), t('Total outstanding maximum'), t('Status')].map(label => <th key={label} scope="col" className="p-2 text-start text-xs text-slate-500">{label}</th>)}</tr></thead>
            <tbody>{drafts.map(d => {
              const problem = draftProblem(d);
              const figuresDisabled = d.eligible !== 'yes';
              return <tr key={d.gradeId} className="border-t border-slate-100 align-top dark:border-white/10">
                <th scope="row" className="p-2 text-start font-medium">{gradeLabel(d)}<span className="block text-xs font-normal text-slate-500">{fillTemplate(t('Level {level}'), { level: d.level })}</span></th>
                <td className="p-2">
                  <select className="select" aria-label={fillTemplate(t('Eligible — {grade}'), { grade: gradeLabel(d) })} value={d.eligible} onChange={e => update(d.gradeId, { eligible: e.target.value as GradeEligibilityChoice })}>
                    {d.eligible === 'unset' && <option value="unset">{t('Not set')}</option>}
                    <option value="yes">{t('Eligible')}</option>
                    <option value="no">{t('Not eligible')}</option>
                  </select>
                </td>
                <td className="p-2">
                  <select className="select" aria-label={fillTemplate(t('Limit basis — {grade}'), { grade: gradeLabel(d) })} disabled={figuresDisabled} value={d.basis} onChange={e => update(d.gradeId, { basis: e.target.value as GradeLimitBasis })}>
                    {(Object.keys(basisKeys) as GradeLimitBasis[]).filter(basis => basis !== 'MultipleOfHousing' || isHousingAdvance(loanType?.code) || d.basis === basis)
                      .map(basis => <option key={basis} value={basis}>{t(basisKeys[basis])}</option>)}
                  </select>
                </td>
                <td className="p-2">
                  <input type="number" min="0" step={d.basis === 'Amount' ? '0.01' : '0.25'} inputMode="decimal" className="input w-32" disabled={figuresDisabled}
                    aria-label={fillTemplate(t('Per-loan maximum — {grade}'), { grade: gradeLabel(d) })}
                    placeholder={figuresDisabled ? '—' : t('No limit')} value={d.perLoan} onChange={e => update(d.gradeId, { perLoan: e.target.value })} />
                  {!figuresDisabled && <span className="block text-xs text-slate-500">{d.basis === 'Amount' ? currencyCode : t(d.basis === 'MultipleOfBasic' ? 'months of basic salary' : d.basis === 'MultipleOfHousing' ? 'months of housing allowance' : 'months of gross salary')}</span>}
                </td>
                <td className="p-2">
                  <input type="number" min="0" step="0.01" inputMode="decimal" className="input w-32" disabled={figuresDisabled}
                    aria-label={fillTemplate(t('Total outstanding maximum — {grade}'), { grade: gradeLabel(d) })}
                    placeholder={figuresDisabled ? '—' : t('No limit')} value={d.maxOutstanding} onChange={e => update(d.gradeId, { maxOutstanding: e.target.value })} />
                  {!figuresDisabled && <span className="block text-xs text-slate-500">{currencyCode}</span>}
                </td>
                <td className="p-2 text-xs">
                  {d.dirty ? <span className="text-sapphire">{t('Changed — not published yet')}</span>
                    : !d.hasCell ? <span className="text-amber-700 dark:text-amber-300">{t('Not set')}</span>
                    : <span className="text-slate-500">{t(companyId ? (d.isCompanyOverride ? 'Company limit' : 'Group default') : 'Group default')}{d.effectiveFrom ? ` · ${fillTemplate(t(viewAsOf && d.effectiveFrom === viewAsOf ? 'Changes from {date}' : 'since {date}'), { date: d.effectiveFrom })}` : ''}</span>}
                  {problem && <span role="alert" className="mt-1 block text-red-600">{t(problem)}</span>}
                </td>
              </tr>;
            })}</tbody>
          </table>
        </div>

        <div className="flex flex-wrap items-end justify-between gap-3 border-t border-slate-100 pt-3 dark:border-white/10">
          <p className="text-xs text-slate-500">{fillTemplate(t('Publishing for: {scope}. Empty figures mean no grade limit on that figure. Loans already requested keep the limit they were assessed against.'), { scope: companyLabel })}</p>
          <div className="flex flex-wrap items-end gap-3">
            <label className="text-sm">{t('Effective from')}<input type="date" className="input ms-2" value={effectiveFrom} onChange={e => setEffectiveFrom(e.target.value)} /></label>
            <button type="button" className="btn-primary" disabled={publishing || changed.length === 0} onClick={() => void publish()}>
              {publishing ? t('Publishing…') : fillTemplate(t('Publish {count} change(s)'), { count: changed.length })}
            </button>
          </div>
        </div>
      </>}
  </section>;
}
