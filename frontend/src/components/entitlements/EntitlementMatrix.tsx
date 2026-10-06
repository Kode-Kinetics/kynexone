'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  entitlementsApi, type BlockReason, type EntitlementMatrix as Matrix, type MatrixCellError, type MatrixComponent, type MatrixGrade,
  type MatrixCell, type MatrixOffering, type PublishMatrixResult,
} from '../../api/entitlements';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { LegacyImportPanel } from './LegacyImportPanel';
import { fillTemplate, localName } from '../../lib/gradeLoanLimits';
import { numberLocale } from '../../lib/format';
import { loanErrorMessage } from '../../lib/loanWorkflow';
import {
  FLOOR_PROBLEM, applyFromGradeUpward, applySameForAll, blankDraft, cellKey, dependantKeys, draftFromCell, draftProblem, groupKeys, groupOrder,
  inputFromDraft, markRestNotOffered, monthStarts, periodChoiceKeys, sameDraft, summarise, tierKeys, type CellDraft, type SummaryPart,
} from '../../lib/entitlementMatrix';

const valueTypeKeys: Record<string, string> = {
  Amount: 'Fixed amount', PercentOfBasic: '% of basic salary', InKind: 'Provided in kind', CoverageTier: 'Class of cover',
  Quantity: 'Number of tickets', MultipleOfBasic: '× basic salary', MultipleOfGross: '× gross salary', MultipleOfHousing: '× housing allowance',
  EligibilityOnly: 'Eligible',
};

const modeKeys: Record<string, string> = {
  Adopted: 'Adopted from the group', Tailored: 'Tailored for this company', Skipped: 'Not offered by this company',
};

type Drafts = Record<string, CellDraft>;

/**
 * Benefits by grade: one grid, benefits down the side and grades across, for the group or one company. Statutory floors
 * (housing, transport, medical) can't be marked "Not offered" or skipped, and say why. Publishing is future-dated and
 * never edits a value in place; the impact line says who it reaches now and who at their next contract year.
 */
export function EntitlementMatrix({ companies }: { companies: { id: string; name: string }[] }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('entitlements.manage');
  const { isGroupScope, selectedCompanyId } = useCompany();
  const [companyId, setCompanyId] = useState(isGroupScope ? '' : (selectedCompanyId ?? companies[0]?.id ?? ''));
  const [matrix, setMatrix] = useState<Matrix | null>(null);
  const [original, setOriginal] = useState<Drafts>({});
  const [drafts, setDrafts] = useState<Drafts>({});
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [asOf, setAsOf] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [effectiveFrom, setEffectiveFrom] = useState('');
  const [impact, setImpact] = useState<PublishMatrixResult | null>(null);
  // A dry run the server refuses (a value already starts that day, a later one is scheduled…) is said before Publish.
  const [impactError, setImpactError] = useState('');
  const [publishing, setPublishing] = useState(false);
  const [error, setError] = useState('');
  const [serverErrors, setServerErrors] = useState<MatrixCellError[]>([]);
  const [notice, setNotice] = useState('');
  const requestSeq = useRef(0);

  useEffect(() => { if (!isGroupScope && !companyId && companies[0]) setCompanyId(companies[0].id); }, [isGroupScope, companyId, companies]);

  const currency = matrix?.currency ?? '';
  const money = useCallback((n: number) => (Number.isFinite(n)
    ? `${currency ? `${currency} ` : ''}${n.toLocaleString(numberLocale(locale), { maximumFractionDigits: 2 })}` : '—'), [currency, locale]);
  // Values that are i18n keys (a class, who is covered) are translated; numbers and formatted amounts are not.
  const say = useCallback((parts: SummaryPart[]) => parts.reduce((text, p, i) => {
    const piece = p.raw ?? fillTemplate(t(p.key ?? ''), Object.fromEntries(Object.entries(p.values ?? {})
      .map(([k, v]) => [k, typeof v === 'string' && /[A-Za-z]/.test(v) ? t(v) : v])));
    return i === 0 ? piece : `${text}${p.attach ? ' ' : ' · '}${piece}`;
  }, ''), [t]);
  const reasonText = (r: BlockReason | null | undefined, field: 'title' | 'why' | 'fix') =>
    r ? (locale === 'ar' ? r[`${field}Ar` as const] : r[`${field}En` as const]) : '';
  const componentName = (c: MatrixComponent) => localName(locale, c.nameEn, c.nameAr);
  const gradeName = (g: MatrixGrade) => localName(locale, g.name, g.nameAr) || g.code;

  const apply = useCallback((m: Matrix) => {
    const next: Drafts = {};
    for (const c of m.components) for (const g of m.grades) {
      next[cellKey(g.id, c.code)] = draftFromCell(c, g.id, m.cells.find(x => x.gradeId === g.id && x.componentCode === c.code));
    }
    setMatrix(m); setOriginal(next); setDrafts(next); setServerErrors([]);
    // Editing the grid as of a later date publishes from that date by default (an earlier date would sit under it).
    const floor = m.asOf > m.today ? m.asOf : m.today;
    setEffectiveFrom(current => (current && current >= floor ? current : floor));
  }, []);

  const load = useCallback(async (date?: string | null) => {
    if (!isGroupScope && !companyId) return;
    const seq = ++requestSeq.current;
    setLoading(true); setLoadError('');
    try {
      const m = await entitlementsApi.matrix({ companyId: companyId || undefined, asOf: date || undefined });
      if (seq !== requestSeq.current) return;
      if (!m || !Array.isArray(m.grades)) throw new Error('Unexpected matrix response');
      apply(m);
    } catch (e) {
      if (seq !== requestSeq.current) return;
      setMatrix(null); setLoadError(loanErrorMessage(e, t('Unable to load benefits by grade.')));
    } finally { if (seq === requestSeq.current) setLoading(false); }
  }, [companyId, isGroupScope, apply, t]);
  useEffect(() => { setAsOf(null); setNotice(''); setError(''); setEditing(null); void load(); }, [load]);

  const components = useMemo(() => {
    const list = matrix?.components ?? [];
    return [...groupOrder.flatMap(group => list.filter(c => c.group === group && !c.isLoanFacility)), ...list.filter(c => c.isLoanFacility)];
  }, [matrix]);
  const grades = matrix?.grades ?? [];
  const offering = (code: string): MatrixOffering | undefined => matrix?.offerings.find(o => o.componentCode === code);
  const changed = useMemo(() => Object.values(drafts).filter(d => original[cellKey(d.gradeId, d.componentCode)] && !sameDraft(d, original[cellKey(d.gradeId, d.componentCode)])), [drafts, original]);
  const componentOf = useCallback((code: string) => matrix?.components.find(c => c.code === code), [matrix]);
  const problems = useMemo(() => changed.map(d => ({ d, problem: draftProblem(d, componentOf(d.componentCode)!) })).filter(p => p.problem), [changed, componentOf]);
  const cellByKey = useMemo(() => new Map((matrix?.cells ?? []).map(x => [cellKey(x.gradeId, x.componentCode), x])), [matrix]);
  const gapKeys = useMemo(() => new Set((matrix?.gaps ?? []).filter(g => !g.scheduledFrom).map(g => cellKey(g.gradeId, g.componentCode))), [matrix]);
  const scheduled = useMemo(() => new Map((matrix?.gaps ?? []).filter(g => g.scheduledFrom).map(g => [cellKey(g.gradeId, g.componentCode), g.scheduledFrom!])), [matrix]);
  const openGaps = [...gapKeys].filter(k => drafts[k]?.state === 'unset').length;
  const firstScheduled = [...scheduled.values()].sort()[0];
  const openCell = (key: string) => {
    // A statutory floor can only be offered: opening an empty one starts from its lawful minimum.
    const d = drafts[key]; const c = d ? componentOf(d.componentCode) : undefined;
    if (d && c?.isFloor && d.state === 'unset') update(key, { state: 'offered' });
    setEditing(key);
  };

  // The impact line: a dry run of exactly what Publish would send, refreshed as the change set settles.
  useEffect(() => {
    setImpact(null); setImpactError('');
    if (!canManage || changed.length === 0 || problems.length > 0 || !effectiveFrom) return;
    const timer = setTimeout(() => {
      entitlementsApi.publish({ companyId: companyId || null, effectiveFrom, cells: changed.map(inputFromDraft) }, true)
        .then(setImpact).catch(e => { setImpact(null); setImpactError(loanErrorMessage(e, '')); });
    }, 500);
    return () => clearTimeout(timer);
  }, [changed, problems.length, effectiveFrom, companyId, canManage]);

  const update = (key: string, patch: Partial<CellDraft>) => setDrafts(list => ({ ...list, [key]: { ...list[key], ...patch } }));

  const publish = async () => {
    setError(''); setNotice(''); setServerErrors([]);
    if (!changed.length) { setError(t('Change at least one benefit before publishing.')); return; }
    if (problems.length) { setError(t('Some benefits need correcting before they can be published.')); return; }
    setPublishing(true);
    try {
      const result = await entitlementsApi.publish({ companyId: companyId || null, effectiveFrom, cells: changed.map(inputFromDraft) });
      if (result.matrix) apply(result.matrix); else await load(effectiveFrom);
      setAsOf(matrix && effectiveFrom > matrix.today ? effectiveFrom : null);
      setEditing(null);
      setNotice(fillTemplate(t('Published {count} change(s), starting {date}. Contracts already running keep the package fixed for their year.'),
        { count: result.published + result.reverted, date: effectiveFrom }));
    } catch (e) {
      const body = (e as { response?: { data?: { errors?: MatrixCellError[] } } })?.response?.data;
      if (Array.isArray(body?.errors)) setServerErrors(body.errors);
      setError(loanErrorMessage(e, t('Unable to publish benefits by grade.')));
    } finally { setPublishing(false); }
  };

  const discard = () => { setDrafts(original); setEditing(null); setServerErrors([]); setError(''); };

  const companyLabel = companyId ? companies.find(c => c.id === companyId)?.name ?? '' : t('All companies (group default)');
  const editingDraft = editing ? drafts[editing] : null;
  const editingComponent = editingDraft ? componentOf(editingDraft.componentCode) : undefined;
  const editingGrade = editingDraft ? grades.find(g => g.id === editingDraft.gradeId) : undefined;
  const serverError = (key: string) => serverErrors.find(e => cellKey(e.gradeId, e.componentCode) === key);

  return <section className="space-y-4" aria-labelledby="benefits-matrix-heading">
    <div className="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 id="benefits-matrix-heading" className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('Benefits by grade')}</h1>
        <p className="max-w-3xl text-xs text-slate-500 dark:text-slate-400">{t('Set every benefit by grade in one place: housing, transport, tickets, medical cover, education and per diem.')}</p>
      </div>
      <label className="text-sm text-slate-700 dark:text-slate-200">{t('Applies to')}
        <select className="select ms-2" value={companyId} onChange={e => setCompanyId(e.target.value)} aria-label={t('Applies to')}>
          {isGroupScope && <option value="">{t('All companies (group default)')}</option>}
          {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
    </div>
    <p className="text-xs text-slate-500 dark:text-slate-400">{t(companyId
      ? 'This company uses the group values unless you tailor a grade. Values you set here take priority over the group default.'
      : 'These values apply to every company unless a company tailors or skips a benefit.')}</p>

    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {notice && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {asOf && <p role="status" className="rounded-md border border-sapphire/30 bg-sapphire/5 p-2 text-sm">
      {fillTemplate(t('Showing the values that apply from {date}. Until then the current values stay in force.'), { date: asOf })}
      {' '}<button type="button" className="text-sapphire underline" onClick={() => { setAsOf(null); void load(); }}>{t('Show current values')}</button>
    </p>}

    {loading ? <div className="h-40 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading benefits by grade')} />
      : loadError ? <div role="alert" className="space-y-2 text-sm"><p className="text-red-600">{loadError}</p><button type="button" className="btn-secondary" onClick={() => void load()}>{t('Try again')}</button></div>
      : !matrix ? null
      : grades.length === 0 ? <p className="rounded-2xl border border-dashed border-slate-300 p-6 text-sm text-slate-500 dark:border-white/15">{t('No active grades yet. Add grades in Setup, then set their benefits here.')}</p>
      : <>
        {scheduled.size > 0 && firstScheduled && <div role="status" className="rounded-xl border border-sapphire/30 bg-sapphire/5 p-3 text-sm text-slate-700 dark:text-slate-200">
          <p className="font-semibold">{fillTemplate(t('{count} published value(s) start later, the first on {date}.'), { count: scheduled.size, date: firstScheduled })}</p>
          <p className="text-xs">{t('Until then those grades have no value for that benefit.')}{' '}
            <button type="button" className="text-sapphire underline" onClick={() => { setAsOf(firstScheduled); void load(firstScheduled); }}>
              {fillTemplate(t('Show the values from {date}'), { date: firstScheduled })}
            </button></p>
        </div>}
        {openGaps > 0 && <div role="status" className="rounded-xl border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200">
          <p className="font-semibold">{fillTemplate(t('{count} grade value(s) still need setting.'), { count: openGaps })}</p>
          <p className="text-xs">{t('Employees in those grades have no package for that benefit until it has a value, or is marked Not offered. They are outlined in amber below.')}</p>
        </div>}

        <div className="surface overflow-x-auto rounded-2xl">
          <table className="w-full min-w-[720px] text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                <th scope="col" className="sticky start-0 z-10 bg-white p-3 text-start text-xs font-semibold text-slate-500 dark:bg-slate-900">{t('Benefit')}</th>
                {grades.map(g => <th key={g.id} scope="col" className="p-3 text-start text-xs font-semibold text-slate-600 dark:text-slate-300">
                  {gradeName(g)}<span className="block font-normal text-slate-400">{g.code}</span>
                </th>)}
              </tr>
            </thead>
            {[...groupOrder, 'Loans' as const].map(group => {
              const rows = group === 'Loans' ? components.filter(c => c.isLoanFacility) : components.filter(c => c.group === group && !c.isLoanFacility);
              if (rows.length === 0) return null;
              return <tbody key={group}>
                <tr><th colSpan={grades.length + 1} scope="colgroup" className="bg-slate-50 px-3 py-2 text-start text-[11px] font-bold uppercase tracking-wide text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
                  {t(group === 'Loans' ? 'Loan facilities — set in Loans' : groupKeys[group])}
                </th></tr>
                {rows.map(c => <MatrixRow key={c.code} component={c} grades={grades} drafts={drafts} original={original} gapKeys={gapKeys}
                  offering={offering(c.code)} companyView={!!companyId} canManage={canManage} today={matrix.today} companyId={companyId}
                  onEdit={openCell} onMarkRest={() => setDrafts(list => markRestNotOffered(list, grades, c, new Set(scheduled.keys())))} onOfferingChanged={() => void load(asOf)}
                  say={say} money={money} reasonText={reasonText} componentName={componentName} serverError={serverError} editing={editing} cellByKey={cellByKey} scheduled={scheduled} />)}
              </tbody>;
            })}
          </table>
        </div>

        {editingDraft && editingComponent && editingGrade && <CellEditor
          key={editing} draft={editingDraft} component={editingComponent} grade={editingGrade} grades={grades} companyView={!!companyId}
          hasCompanyValue={!!cellByKey.get(editing!)?.isCompanyOverride}
          currency={currency} readOnly={!canManage} reasonText={reasonText} componentName={componentName} gradeName={gradeName}
          serverError={serverError(editing!)}
          onChange={patch => update(editing!, patch)}
          onSameForAll={() => setDrafts(list => applySameForAll(list, grades, editingDraft.componentCode, editingDraft.gradeId))}
          onUpward={() => setDrafts(list => applyFromGradeUpward(list, grades, editingDraft.componentCode, editingDraft.gradeId))}
          onReset={() => update(editing!, original[editing!] ?? blankDraft(editingComponent, editingDraft.gradeId))}
          onClose={() => setEditing(null)} />}

        {canManage && <div className="surface flex flex-wrap items-end justify-between gap-3 rounded-2xl p-3">
          <div className="max-w-xl space-y-1 text-xs text-slate-500 dark:text-slate-400">
            <p>{fillTemplate(t('Publishing for: {scope}. Values are never edited in place: the value in force ends the day before and the new one starts.'), { scope: companyLabel })}</p>
            {changed.length > 0 && impact && <p role="status" className="font-medium text-slate-700 dark:text-slate-200">
              {fillTemplate(t('Reaches {now} employee(s) on that date and {later} at their next contract year.'), { now: impact.affectedNow, later: impact.affectedAtRenewal })}
              {(impact.superseded ?? 0) > 0 && <span className="block font-normal">{fillTemplate(t('{count} value(s) that have not started yet will be replaced on their start date.'), { count: impact.superseded ?? 0 })}</span>}
            </p>}
            {changed.length > 0 && impactError && <p role="alert" className="text-amber-800 dark:text-amber-200">{impactError}</p>}
            {problems.length > 0 && <p className="text-red-600">{fillTemplate(t('{count} change(s) need correcting first.'), { count: problems.length })}</p>}
          </div>
          <div className="flex flex-wrap items-end gap-2">
            <label className="text-sm">{t('Effective from')}
              <input type="date" className="input ms-2" min={matrix.today} value={effectiveFrom} onChange={e => setEffectiveFrom(e.target.value)} />
            </label>
            <button type="button" className="btn-secondary" disabled={publishing || changed.length === 0} onClick={discard}>{t('Discard changes')}</button>
            <button type="button" className="btn-primary" disabled={publishing || changed.length === 0 || problems.length > 0} onClick={() => void publish()}>
              {publishing ? t('Publishing…') : fillTemplate(t('Publish {count} change(s)'), { count: changed.length })}
            </button>
          </div>
        </div>}

        {canManage && !companyId && isGroupScope && <LegacyImportPanel today={matrix.today} onImported={() => void load(asOf)} />}
      </>}
  </section>;
}

function MatrixRow({ component: c, grades, drafts, original, gapKeys, offering, companyView, canManage, today, companyId, onEdit, onMarkRest,
  onOfferingChanged, say, money, reasonText, componentName, serverError, editing, cellByKey, scheduled }: {
  component: MatrixComponent; grades: MatrixGrade[]; drafts: Drafts; original: Drafts; gapKeys: Set<string>; offering?: MatrixOffering;
  companyView: boolean; canManage: boolean; today: string; companyId: string; editing: string | null;
  onEdit: (key: string) => void; onMarkRest: () => void; onOfferingChanged: () => void;
  say: (parts: SummaryPart[]) => string; money: (n: number) => string;
  reasonText: (r: BlockReason | null | undefined, field: 'title' | 'why' | 'fix') => string; componentName: (c: MatrixComponent) => string;
  serverError: (key: string) => MatrixCellError | undefined; cellByKey: Map<string, MatrixCell>; scheduled: Map<string, string>;
}) {
  const { t } = useLocale();
  const skipped = offering?.mode === 'Skipped';
  const anyUnset = grades.some(g => drafts[cellKey(g.id, c.code)]?.state === 'unset' && !scheduled.has(cellKey(g.id, c.code)));
  return <tr className={`border-b border-slate-100 align-top dark:border-white/[0.06] ${skipped ? 'opacity-60' : ''}`}>
    <th scope="row" className="sticky start-0 z-10 w-56 min-w-[9rem] bg-white p-3 text-start font-medium text-slate-800 dark:bg-slate-900 dark:text-slate-100">
      <span className="block">{componentName(c)}</span>
      {c.isFloor && <details className="mt-1 text-xs font-normal">
        <summary className="cursor-pointer text-slate-600 dark:text-slate-300">{t('Required by law')}</summary>
        <p className="mt-1 font-semibold text-slate-600 dark:text-slate-300">{reasonText(c.floorReason, 'title')}</p>
        <p className="text-slate-500 dark:text-slate-400">{reasonText(c.floorReason, 'why')}</p>
      </details>}
      {companyView && offering && offering.mode !== 'Group' && !c.isLoanFacility && <span className={`mt-1 inline-block rounded-full px-2 py-0.5 text-[11px] font-semibold ${
        offering.mode === 'Skipped' ? 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200'
          : offering.mode === 'Tailored' ? 'bg-sapphire/10 text-sapphire' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
        {t(modeKeys[offering.mode])}
      </span>}
      {companyView && offering?.changesOn && <span className="mt-1 block text-[11px] font-normal text-slate-500">{fillTemplate(t(offering.offered ? 'Not offered from {date}' : 'Offered again from {date}'), { date: offering.changesOn })}</span>}
      {canManage && companyView && offering?.canBeSkipped && <OfferingSwitch code={c.code} name={componentName(c)} companyId={companyId} offering={offering} today={today} onChanged={onOfferingChanged} />}
      {canManage && !c.isFloor && !c.isLoanFacility && !skipped && anyUnset && <button type="button" className="mt-1 block text-[11px] font-normal text-sapphire underline" onClick={onMarkRest}>
        {t('Mark the rest Not offered')}
      </button>}
      {c.isLoanFacility && <Link href="/loans" className="mt-1 block text-[11px] font-normal text-sapphire underline">{t('Edit in Loans → Loan policies → Limits by grade')}</Link>}
    </th>
    {grades.map(g => {
      const key = cellKey(g.id, c.code);
      const d = drafts[key];
      if (!d) return <td key={g.id} className="p-2" />;
      const dirty = original[key] && JSON.stringify(original[key]) !== JSON.stringify(d);
      const problem = dirty ? draftProblem(d, c) : null;
      const gap = gapKeys.has(key) && d.state === 'unset';
      const startsOn = d.state === 'unset' ? scheduled.get(key) : undefined;
      const summary = c.isLoanFacility ? loanSummary(cellByKey.get(key), money, t)
        : startsOn ? fillTemplate(t('Starts {date}'), { date: startsOn }) : say(summarise(d, money));
      const tone = problem || serverError(key) ? 'border-red-400 bg-red-50 dark:bg-red-500/10'
        : dirty ? 'border-sapphire bg-sapphire/5'
        : gap ? 'border-amber-400 bg-amber-50 dark:bg-amber-500/10'
        : startsOn ? 'border-dashed border-sapphire/40'
        : 'border-slate-200 dark:border-white/10';
      return <td key={g.id} className="p-2">
        <button type="button" disabled={c.isLoanFacility || skipped} aria-pressed={editing === key}
          aria-label={fillTemplate(t('{benefit} for {grade}: {value}'), { benefit: componentName(c), grade: g.code, value: summary })}
          onClick={() => onEdit(key)}
          className={`w-full min-w-[120px] rounded-lg border px-2 py-1.5 text-start text-xs transition hover:border-sapphire disabled:cursor-default disabled:hover:border-slate-200 ${tone}`}>
          <span className={d.state === 'notOffered' || d.state === 'unset' ? 'text-slate-500' : 'text-slate-800 dark:text-slate-100'}>{summary}</span>
          {companyView && !dirty && cellByKey.get(key) && <span className="mt-0.5 block text-[10px] text-slate-400">{t(cellByKey.get(key)!.isCompanyOverride ? 'This company' : 'Group default')}</span>}
          {dirty && <span className="mt-0.5 block text-[10px] font-semibold text-sapphire">{t('Changed — not published yet')}</span>}
          {problem && <span className="mt-0.5 block text-[10px] text-red-600">{problem === FLOOR_PROBLEM ? reasonText(c.floorReason, 'title') : t(problem)}</span>}
        </button>
      </td>;
    })}
  </tr>;
}

function loanSummary(cell: MatrixCell | undefined, money: (n: number) => string, t: (k: string) => string) {
  if (!cell) return t('Not set');
  if (!cell.eligible) return t('Not eligible');
  // One sentence per shape, the outstanding cap included, so Arabic can order it.
  const owed = cell.maxOutstandingAmount !== null ? { owed: money(cell.maxOutstandingAmount) } : null;
  if (cell.valueType === 'Amount' && cell.amount !== null)
    return fillTemplate(t(owed ? 'Up to {amount}, at most {owed} owed' : 'Up to {amount}'), { amount: money(cell.amount), ...owed });
  if (cell.rate !== null && cell.valueType.startsWith('MultipleOf'))
    return fillTemplate(t(owed ? 'Up to {rate} {basis}, at most {owed} owed' : 'Up to {rate} {basis}'), { rate: cell.rate, basis: t(valueTypeKeys[cell.valueType]), ...owed });
  return owed ? fillTemplate(t('Eligible, at most {owed} owed'), owed) : t('Eligible');
}

function OfferingSwitch({ code, name, companyId, offering, today, onChanged }: {
  code: string; name: string; companyId: string; offering: MatrixOffering; today: string; onChanged: () => void;
}) {
  const { t } = useLocale();
  const months = monthStarts(today, 12);
  const [open, setOpen] = useState(false);
  const [from, setFrom] = useState(months[0]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const stop = offering.offered;
  const submit = async () => {
    setBusy(true); setError('');
    try {
      await entitlementsApi.setOffering({ companyId, componentCode: code, offered: !stop, effectiveFrom: from });
      setOpen(false); onChanged();
    } catch (e) { setError(loanErrorMessage(e, t('Unable to change whether this company offers the benefit.'))); }
    finally { setBusy(false); }
  };
  if (!open) return <button type="button" className="mt-1 block text-[11px] font-normal text-sapphire underline" onClick={() => setOpen(true)}>
    {t(stop ? 'Skip for this company' : 'Offer again in this company')}
  </button>;
  return <div className="mt-2 space-y-2 rounded-lg border border-slate-200 p-2 text-xs font-normal dark:border-white/10">
    <p>{fillTemplate(t(stop ? 'Choose the month this company stops offering {benefit}.' : 'Choose the month this company offers {benefit} again.'), { benefit: name })}</p>
    <select className="select w-full" value={from} onChange={e => setFrom(e.target.value)} aria-label={t('First month')}>
      {months.map(m => <option key={m} value={m}>{m}</option>)}
    </select>
    {stop && <p className="text-slate-500">{t('Employees already in a contract year keep the package fixed for it. New contract years leave this benefit out.')}</p>}
    {error && <p role="alert" className="text-red-600">{error}</p>}
    <div className="flex gap-2">
      <button type="button" className="btn-primary h-7 px-2 text-xs" disabled={busy} onClick={() => void submit()}>{t(stop ? 'Skip from that month' : 'Offer from that month')}</button>
      <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={busy} onClick={() => setOpen(false)}>{t('Cancel')}</button>
    </div>
  </div>;
}

function CellEditor({ draft: d, component: c, grade, companyView, hasCompanyValue, currency, readOnly, reasonText, componentName, gradeName, serverError,
  onChange, onSameForAll, onUpward, onReset, onClose }: {
  draft: CellDraft; component: MatrixComponent; grade: MatrixGrade; grades: MatrixGrade[]; companyView: boolean; hasCompanyValue: boolean;
  currency: string; readOnly: boolean; reasonText: (r: BlockReason | null | undefined, field: 'title' | 'why' | 'fix') => string;
  componentName: (c: MatrixComponent) => string; gradeName: (g: MatrixGrade) => string; serverError?: MatrixCellError;
  onChange: (patch: Partial<CellDraft>) => void; onSameForAll: () => void; onUpward: () => void; onReset: () => void; onClose: () => void;
}) {
  const { t, locale } = useLocale();
  const problem = draftProblem(d, c);
  const offered = d.state === 'offered';
  const medical = c.code === 'MEDICAL';
  const setState = (state: CellDraft['state']) => onChange(state === 'offered' && d.state !== 'offered'
    ? { ...blankDraft(c, d.gradeId), state: 'offered', note: d.note }
    : { state });
  const tiers = c.allowedCoverageTiers;
  const ref = useRef<HTMLElement>(null);
  useEffect(() => { ref.current?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' }); }, []);
  return <aside ref={ref} role="dialog" aria-modal="false" aria-labelledby="cell-editor-heading"
    className="surface space-y-3 rounded-2xl border border-sapphire/30 p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div>
        <h2 id="cell-editor-heading" className="font-semibold text-slate-800 dark:text-slate-100">{componentName(c)} — {gradeName(grade)}</h2>
        <p className="text-xs text-slate-500">{t(groupKeys[c.group])}</p>
      </div>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onClose}>{t('Done')}</button>
    </div>
    {c.isFloor && <div className="rounded-lg border border-slate-200 bg-slate-50 p-2 text-xs dark:border-white/10 dark:bg-white/[0.03]">
      <p className="font-semibold">{reasonText(c.floorReason, 'title')}</p>
      <p className="text-slate-600 dark:text-slate-300">{reasonText(c.floorReason, 'why')}</p>
    </div>}

    <fieldset disabled={readOnly} className="space-y-3">
      <div className="flex flex-wrap gap-3 text-sm" role="radiogroup" aria-label={t('Does this grade get it?')}>
        <label className="flex items-center gap-1.5"><input type="radio" checked={offered} onChange={() => setState('offered')} />{t('Offered')}</label>
        <label className={`flex items-center gap-1.5 ${c.isFloor ? 'text-slate-400' : ''}`}>
          <input type="radio" checked={d.state === 'notOffered'} disabled={c.isFloor} onChange={() => setState('notOffered')} />{t('Not offered')}
        </label>
        {companyView && hasCompanyValue && <label className="flex items-center gap-1.5">
          <input type="radio" checked={d.state === 'groupDefault'} onChange={() => setState('groupDefault')} />{t('Use the group default')}
        </label>}
      </div>
      {c.isFloor && <p className="text-xs text-slate-500">{reasonText(c.floorReason, 'fix')}</p>}

      {offered && <div className="grid gap-3 sm:grid-cols-2">
        <label className="text-sm">{t('How it is given')}
          <select className="select mt-1 w-full" value={d.valueType} onChange={e => onChange({ valueType: e.target.value as CellDraft['valueType'] })}>
            {c.allowedValueTypes.map(v => <option key={v} value={v}>{t(valueTypeKeys[v] ?? v)}</option>)}
          </select>
        </label>
        {d.valueType === 'Amount' && <label className="text-sm">{currency ? fillTemplate(t('Amount ({currency})'), { currency }) : t('Amount (company currency)')}
          <input type="number" min="0" step="0.01" inputMode="decimal" className="input mt-1 w-full" value={d.amount} onChange={e => onChange({ amount: e.target.value })} />
        </label>}
        {d.valueType === 'PercentOfBasic' && <label className="text-sm">{t('Percent of basic salary')}
          <input type="number" min="0" max="100" step="0.5" inputMode="decimal" className="input mt-1 w-full" value={d.percent} onChange={e => onChange({ percent: e.target.value })} />
        </label>}
        {(d.valueType === 'CoverageTier' || d.valueType === 'Quantity') && tiers.length > 0 && <label className="text-sm">{t(medical ? 'Class of cover' : 'Travel class')}
          <select className="select mt-1 w-full" value={d.coverageTier} onChange={e => onChange({ coverageTier: e.target.value })}>
            {tiers.map(x => <option key={x} value={x}>{t(tierKeys[x] ?? x)}</option>)}
          </select>
        </label>}
        {d.valueType === 'Quantity' && <label className="text-sm">{t('How many')}
          <input type="number" min="1" max="99" step="1" inputMode="numeric" className="input mt-1 w-full" value={d.quantity} onChange={e => onChange({ quantity: e.target.value })} />
        </label>}
        {c.allowedDependantScopes.length > 1 || c.allowedDependantScopes[0] !== 'None' ? <label className="text-sm">{t('Who is covered')}
          <select className="select mt-1 w-full" value={d.dependantScope} disabled={c.allowedDependantScopes.length === 1}
            onChange={e => onChange({ dependantScope: e.target.value as CellDraft['dependantScope'], maxDependants: e.target.value === 'None' ? '' : d.maxDependants })}>
            {c.allowedDependantScopes.map(s => <option key={s} value={s}>{t(dependantKeys[s])}</option>)}
          </select>
        </label> : null}
        {d.dependantScope !== 'None' && <label className="text-sm">{t(c.code === 'EDUCATION' ? 'Most children covered' : 'Most dependants covered')}
          <input type="number" min="0" max="20" step="1" inputMode="numeric" className="input mt-1 w-full" placeholder={t('No limit')}
            value={d.maxDependants} onChange={e => onChange({ maxDependants: e.target.value })} />
        </label>}
        {c.allowedLimitPeriods.length > 1 && <label className="text-sm">{t('Resets')}
          <select className="select mt-1 w-full" value={d.limitPeriod} onChange={e => onChange({ limitPeriod: e.target.value })}>
            {c.allowedLimitPeriods.map(p => <option key={p} value={p}>{t(periodChoiceKeys[p] ?? p)}</option>)}
          </select>
        </label>}
      </div>}

      {offered && <details className="rounded-lg border border-slate-200 p-2 text-sm dark:border-white/10" open={d.nationalityScope !== 'Any' || d.afterProbation || !!d.minServiceMonths}>
        <summary className="cursor-pointer text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Conditions (optional)')}</summary>
        <p className="mt-1 text-xs text-slate-500">{t('Only months of service, the end of probation and — with a recorded legal basis — nationality can be conditions. Age, gender, marital status and disability never can.')}</p>
        {medical && <p className="mt-1 text-xs text-slate-500">{t('Medical cover applies from the first day to every employee and their family, so it has no conditions.')}</p>}
        <div className="mt-2 grid gap-3 sm:grid-cols-2">
          <label className="text-sm">{t('Months of service first')}
            <input type="number" min="0" max="600" step="1" inputMode="numeric" className="input mt-1 w-full" value={d.minServiceMonths} disabled={medical}
              onChange={e => onChange({ minServiceMonths: e.target.value })} />
          </label>
          <label className="flex items-center gap-2 text-sm sm:mt-6"><input type="checkbox" checked={d.afterProbation} disabled={medical}
            onChange={e => onChange({ afterProbation: e.target.checked })} />{t('Only after probation')}</label>
          <label className="text-sm">{t('Nationality')}
            <select className="select mt-1 w-full" value={d.nationalityScope} disabled={medical}
              onChange={e => onChange({ nationalityScope: e.target.value as CellDraft['nationalityScope'] })}>
              <option value="Any">{t('Any nationality')}</option>
              <option value="Saudi">{t('Saudi employees')}</option>
              <option value="NonSaudi">{t('Non-Saudi employees')}</option>
            </select>
          </label>
          {d.nationalityScope !== 'Any' && <label className="text-sm">{t('Legal basis (required)')}
            <input className="input mt-1 w-full" maxLength={300} value={d.nationalityBasis} dir={locale === 'ar' ? 'rtl' : 'ltr'}
              placeholder={t('For example: home-leave ticket in the employment contract')} onChange={e => onChange({ nationalityBasis: e.target.value })} />
          </label>}
        </div>
      </details>}

      {d.state !== 'groupDefault' && <label className="block text-sm">{t('Note (optional)')}
        <input className="input mt-1 w-full" maxLength={500} value={d.note} onChange={e => onChange({ note: e.target.value })} />
      </label>}
    </fieldset>

    {problem && <p role="alert" className="text-sm text-red-600">{problem === FLOOR_PROBLEM ? reasonText(c.floorReason, 'fix') : t(problem)}</p>}
    {serverError && <p role="alert" className="text-sm text-red-600">{serverError.reason ? reasonText(serverError.reason, 'title') : serverError.message}</p>}

    {!readOnly && <div className="flex flex-wrap gap-2 border-t border-slate-100 pt-3 dark:border-white/10">
      <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onSameForAll}>{t('Same for all grades')}</button>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onUpward}>{fillTemplate(t('From {grade} upward'), { grade: grade.code })}</button>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onReset}>{t('Undo changes to this grade')}</button>
    </div>}
  </aside>;
}
