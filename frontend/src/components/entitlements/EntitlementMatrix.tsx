'use client';

import Link from 'next/link';
import { Hourglass, Info, TriangleAlert } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
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
  addBenefitProblems, addBenefitRequest, allowanceWarnings, benefitServerProblem, blankAddBenefit, isCashAllowance, isPendingFinance, staleRunMonths, waitingForFinance,
  type AddBenefitField, type AddBenefitForm, type AddBenefitProblems, type ServerProblem,
} from '../../lib/cashAllowance';
import { EosbFields, FinanceAllowancePanel, GlAccountField, useCashGlChoices } from './CashAllowanceFinance';
import { WaitingForFinance } from './WaitingForFinance';
import { useFormat } from '../../hooks/useFormat';
import {
  FLOOR_PROBLEM, applyFromGradeUpward, applySameForAll, blankDraft, cellKey, dependantKeys, draftFromCell, draftProblem, groupKeys, groupOrder,
  inputFromDraft, markRestNotOffered, monthStarts, periodChoiceKeys, sameDraft, summarise, tierKeys, type CellDraft, type SummaryPart,
  draftStorageKey, guideProgress, restoreDrafts, savedFrom, type SavedDrafts, gridToCsv, importGrid, type GridImport,
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
/** Reads the edits kept in this browser for one scope; never throws (private mode, full storage, a damaged entry). */
function readSaved(key: string): SavedDrafts | null {
  try { const raw = window.localStorage.getItem(key); return raw ? JSON.parse(raw) as SavedDrafts : null; } catch { return null; }
}
function writeSaved(key: string, saved: SavedDrafts) {
  try {
    if (Object.keys(saved).length === 0) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, JSON.stringify(saved));
  } catch { /* storage unavailable: the edits still live on the page */ }
}

/**
 * `embedded`: shown as a Setup section, whose header already names it. `onDirtyChange`: unpublished edits, for Setup's
 * leave guard.
 */
export function EntitlementMatrix({ companies, embedded = false, onDirtyChange }: {
  companies: { id: string; name: string }[]; embedded?: boolean; onDirtyChange?: (dirty: boolean) => void;
}) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('entitlements.manage');
  // Finance (payroll.approve) confirms a cash allowance somebody else added, and corrects a confirmed one.
  const canFinance = hasPermission('payroll.approve');
  const fmt = useFormat();
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
  const [adding, setAdding] = useState(false);
  const [importing, setImporting] = useState(false);
  // Finance's confirm / correct panel for one allowance, the allowances the server said their own maker cannot confirm, and what a change left to say.
  const [finance, setFinance] = useState<{ code: string; mode: 'confirm' | 'correct' } | null>(null);
  const [makerBlocked, setMakerBlocked] = useState<ReadonlySet<string>>(new Set());
  const [cautions, setCautions] = useState<string[]>([]);
  const [staleNote, setStaleNote] = useState('');
  const requestSeq = useRef(0);
  // The browser key the edits on screen are kept under: the scope and date of the grid that is loaded.
  const storageKey = useRef<string | null>(null);

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

  const apply = useCallback((m: Matrix, saved?: SavedDrafts | null) => {
    const next: Drafts = {};
    for (const c of m.components) for (const g of m.grades) {
      next[cellKey(g.id, c.code)] = draftFromCell(c, g.id, m.cells.find(x => x.gradeId === g.id && x.componentCode === c.code));
    }
    // Unpublished edits come back after a reload or a new benefit, as long as the value each one started from is unchanged.
    const kept = restoreDrafts(next, saved);
    setMatrix(m); setOriginal(next); setDrafts(kept.drafts); setServerErrors([]);
    return kept.restored;
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
      const key = draftStorageKey(companyId, date ?? null);
      storageKey.current = key;
      if (apply(m, readSaved(key)) > 0) setNotice(t('Your unpublished changes were kept. Publish them, or discard them.'));
    } catch (e) {
      if (seq !== requestSeq.current) return;
      setMatrix(null); setLoadError(loanErrorMessage(e, t('Unable to load benefits by grade.')));
    } finally { if (seq === requestSeq.current) setLoading(false); }
  }, [companyId, isGroupScope, apply, t]);
  useEffect(() => { setAsOf(null); setNotice(''); setError(''); setEditing(null); setFinance(null); setCautions([]); setStaleNote(''); void load(); }, [load]);

  const components = useMemo(() => {
    const list = matrix?.components ?? [];
    return [...groupOrder.flatMap(group => list.filter(c => c.group === group && !c.isLoanFacility)), ...list.filter(c => c.isLoanFacility)];
  }, [matrix]);
  const grades = useMemo(() => matrix?.grades ?? [], [matrix]);
  const offering = (code: string): MatrixOffering | undefined => matrix?.offerings.find(o => o.componentCode === code);
  const changed = useMemo(() => Object.values(drafts).filter(d => original[cellKey(d.gradeId, d.componentCode)] && !sameDraft(d, original[cellKey(d.gradeId, d.componentCode)])), [drafts, original]);
  const componentOf = useCallback((code: string) => matrix?.components.find(c => c.code === code), [matrix]);
  const problems = useMemo(() => changed.map(d => ({ d, problem: draftProblem(d, componentOf(d.componentCode)!) })).filter(p => p.problem), [changed, componentOf]);
  const cellByKey = useMemo(() => new Map((matrix?.cells ?? []).map(x => [cellKey(x.gradeId, x.componentCode), x])), [matrix]);
  const gapKeys = useMemo(() => new Set((matrix?.gaps ?? []).filter(g => !g.scheduledFrom).map(g => cellKey(g.gradeId, g.componentCode))), [matrix]);
  const scheduled = useMemo(() => new Map((matrix?.gaps ?? []).filter(g => g.scheduledFrom).map(g => [cellKey(g.gradeId, g.componentCode), g.scheduledFrom!])), [matrix]);
  // An allowance still waiting for Finance takes no grade value: it is not a gap to set and not a step in the guide.
  const pendingCodes = useMemo(() => new Set(components.filter(isPendingFinance).map(c => c.code)), [components]);
  const openGaps = [...gapKeys].filter(k => drafts[k]?.state === 'unset' && !pendingCodes.has(k.split('|')[1])).length;
  // A new benefit is a row for the whole group, so it is added from the group view by someone who can reach every company.
  const canAddBenefit = canManage && !companyId && isGroupScope;
  const dirty = changed.length > 0;

  // Every edit is kept in the browser as it is made, so nothing typed is lost to a reload or a new benefit.
  useEffect(() => { if (matrix && storageKey.current) writeSaved(storageKey.current, savedFrom(drafts, original)); }, [drafts, original, matrix]);
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  // The step strip: how many grade values are set, and the next one to set, in the order the grid shows them.
  const skippedHere = useCallback((code: string) => matrix?.offerings.find(o => o.componentCode === code)?.mode === 'Skipped'
    || !!matrix?.components.some(c => c.code === code && isPendingFinance(c)), [matrix]);
  const scheduledKeys = useMemo(() => new Set(scheduled.keys()), [scheduled]);
  const progress = useMemo(() => guideProgress(components, grades, drafts, scheduledKeys, skippedHere), [components, grades, drafts, scheduledKeys, skippedHere]);
  const nextAfter = (key: string | null) => guideProgress(components, grades, drafts, scheduledKeys, skippedHere, key).nextKey;
  const nothingPublished = !!matrix && matrix.cells.length === 0;
  const nextLabel = (key: string) => {
    const [gradeId, code] = key.split('|');
    const c = componentOf(code); const g = grades.find(x => x.id === gradeId);
    return c && g ? fillTemplate(t('{benefit} for {grade}'), { benefit: componentName(c), grade: gradeName(g) }) : '';
  };
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
      if (storageKey.current) writeSaved(storageKey.current, {});
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

  const discard = () => {
    if (storageKey.current) writeSaved(storageKey.current, {});
    setDrafts(original); setEditing(null); setServerErrors([]); setError(''); setNotice('');
  };

  const companyLabel = companyId ? companies.find(c => c.id === companyId)?.name ?? '' : t('All companies (group default)');
  const editingDraft = editing ? drafts[editing] : null;
  const editingComponent = editingDraft ? componentOf(editingDraft.componentCode) : undefined;
  const editingGrade = editingDraft ? grades.find(g => g.id === editingDraft.gradeId) : undefined;
  const serverError = (key: string) => serverErrors.find(e => cellKey(e.gradeId, e.componentCode) === key);

  return <section className="space-y-4" aria-labelledby="benefits-matrix-heading">
    <div className="flex flex-wrap items-end justify-between gap-3">
      <div>
        {embedded
          ? <h2 id="benefits-matrix-heading" className="sr-only">{t('Benefits by grade')}</h2>
          : <h1 id="benefits-matrix-heading" className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('Benefits by grade')}</h1>}
        <p className="max-w-3xl text-xs text-slate-500 dark:text-slate-400">{t('Set every benefit by grade in one place: housing, transport, tickets, medical cover, education and per diem.')}</p>
      </div>
      <label className="text-sm text-slate-700 dark:text-slate-200">{t('Applies to')}
        <select className="select ms-2" value={companyId} onChange={e => setCompanyId(e.target.value)} aria-label={t('Applies to')}>
          {isGroupScope && <option value="">{t('All companies (group default)')}</option>}
          {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      {canAddBenefit && <div className="flex flex-col items-end gap-1">
        <button type="button" className="btn-secondary" aria-expanded={adding} disabled={loading || !matrix}
          onClick={() => setAdding(open => !open)}>{t('Add a benefit')}</button>
      </div>}
    </div>
    {adding && canAddBenefit && <AddBenefitPanel
      onClose={() => setAdding(false)}
      onAdded={async (name, added) => {
        setAdding(false); setStaleNote(''); setCautions(allowanceWarnings(added.warnings));
        // A cash allowance added without the Finance permission waits for Finance: it can take no value until then.
        setNotice(isPendingFinance(added)
          ? t('Added. Finance must confirm the GL account and end-of-service setting before it can be used.')
          : fillTemplate(t('{benefit} was added. Set its value for each grade, then publish.'), { benefit: name }));
        await load(asOf);
      }} />}
    <p className="text-xs text-slate-500 dark:text-slate-400">{t(companyId
      ? 'This company uses the group values unless you tailor a grade. Values you set here take priority over the group default.'
      : 'These values apply to every company unless a company tailors or skips a benefit.')}</p>

    {error && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {notice && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {cautions.length > 0 && <div role="status" className="rounded-xl border border-sky-200 bg-sky-50 p-3 text-sm text-sky-900 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-100" data-testid="allowance-cautions">
      <p className="flex items-center gap-2 font-semibold"><Info className="h-4 w-4 shrink-0" aria-hidden="true" />{t('Worth a second look')}</p>
      <ul className="mt-1 list-disc space-y-0.5 ps-9 text-xs">{cautions.map(c => <li key={c}>{t(c)}</li>)}</ul>
    </div>}
    {staleNote && <p role="status" className="flex items-start gap-2 rounded-xl border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-100" data-testid="stale-runs">
      <TriangleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{staleNote}</p>}
    {asOf && <p role="status" className="rounded-lg border border-sapphire/30 bg-sapphire/5 p-2 text-sm">
      {fillTemplate(t('Showing the values that apply from {date}. Until then the current values stay in force.'), { date: asOf })}
      {' '}<button type="button" className="text-sapphire underline" onClick={() => { setAsOf(null); void load(); }}>{t('Show current values')}</button>
    </p>}

    {loading ? <div className="h-40 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading benefits by grade')} />
      : loadError ? <div role="alert" className="space-y-2 text-sm"><p className="text-red-600">{loadError}</p><button type="button" className="btn-secondary" onClick={() => void load()}>{t('Try again')}</button></div>
      : !matrix ? null
      : grades.length === 0 ? <p className="rounded-2xl border border-dashed border-slate-300 p-6 text-sm text-slate-500 dark:border-white/15">{t('No active grades yet. Add grades in Setup, then set their benefits here.')}{' '}
        <Link href="/setup?tab=grades" className="font-semibold text-sapphire underline">{t('Open Grades')}</Link></p>
      : <>
        {scheduled.size > 0 && firstScheduled && <div role="status" className="rounded-xl border border-sapphire/30 bg-sapphire/5 p-3 text-sm text-slate-700 dark:text-slate-200">
          <p className="font-semibold">{fillTemplate(t('{count} published value(s) start later, the first on {date}.'), { count: scheduled.size, date: firstScheduled })}</p>
          <p className="text-xs">{t('Until then those grades have no value for that benefit.')}{' '}
            <button type="button" className="text-sapphire underline" onClick={() => { setAsOf(firstScheduled); void load(firstScheduled); }}>
              {fillTemplate(t('Show the values from {date}'), { date: firstScheduled })}
            </button></p>
        </div>}
        <WaitingForFinance items={waitingForFinance(components, canFinance, makerBlocked)}
          nameOf={code => { const c = componentOf(code); return c ? componentName(c) : code; }}
          onConfirm={code => { setFinance({ code, mode: 'confirm' }); setNotice(''); setCautions([]); setStaleNote(''); setEditing(null); }} />
        <GuideStrip progress={progress} changes={changed.length} canManage={canManage} canAddBenefit={canAddBenefit}
          pendingNames={components.filter(isPendingFinance).map(componentName)}
          nextLabel={progress.nextKey ? nextLabel(progress.nextKey) : ''}
          onNext={() => progress.nextKey && openCell(progress.nextKey)} onAddBenefit={() => setAdding(true)} />
        {openGaps > 0 && (nothingPublished
          ? <div role="status" className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-200">
            <p className="font-semibold">{t('Nothing is set yet.')}</p>
            <p className="text-xs">{t('Give each grade a value for each benefit, or mark it Not offered. Press Set next value to go through them in order, then publish.')}</p>
          </div>
          : <div role="status" className="rounded-xl border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200">
            <p className="font-semibold">{fillTemplate(t('{count} grade value(s) still need setting.'), { count: openGaps })}</p>
            <p className="text-xs">{t('Employees in those grades have no package for that benefit until it has a value, or is marked Not offered. They are outlined in amber below.')}</p>
          </div>)}

        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => downloadCsv(gridToCsv(components, grades, drafts), companyId)}>{t('Export to Excel (CSV)')}</button>
          {canManage && <button type="button" className="btn-secondary h-8 px-3 text-xs" aria-expanded={importing} onClick={() => setImporting(open => !open)}>{t('Import from Excel')}</button>}
        </div>
        {importing && canManage && <ImportPanel components={components} grades={grades} onClose={() => setImporting(false)}
          onApply={result => {
            setDrafts(list => {
              const next = { ...list };
              for (const { key, patch } of result.patches) if (next[key]) next[key] = { ...next[key], ...patch };
              return next;
            });
            setImporting(false);
            setNotice(fillTemplate(t('{count} value(s) were added to the grid. Review them, then publish.'), { count: result.patches.length }));
          }} />}
        <div className="surface max-h-[75vh] overflow-auto rounded-2xl">
          <table className="w-full min-w-[720px] text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                <th scope="col" className="sticky start-0 top-0 z-30 bg-white p-3 text-start text-xs font-semibold text-slate-500 dark:bg-slate-900">{t('Benefit')}</th>
                {grades.map(g => <th key={g.id} scope="col" className="sticky top-0 z-20 bg-white p-3 text-start text-xs font-semibold text-slate-600 dark:bg-slate-900 dark:text-slate-300">
                  {gradeName(g)}<span className="block font-normal text-slate-400">{g.code}</span>
                </th>)}
              </tr>
            </thead>
            {[...groupOrder, 'Loans' as const].map(group => {
              const rows = group === 'Loans' ? components.filter(c => c.isLoanFacility) : components.filter(c => c.group === group && !c.isLoanFacility);
              if (rows.length === 0) return null;
              return <tbody key={group}>
                <tr><th colSpan={grades.length + 1} scope="colgroup" className="bg-slate-50 px-3 py-2 text-start text-caption font-semibold uppercase tracking-wide text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
                  {t(group === 'Loans' ? 'Loan facilities — set in Loans' : groupKeys[group])}
                </th></tr>
                {rows.map(c => <MatrixRow key={c.code} component={c} grades={grades} drafts={drafts} original={original} gapKeys={gapKeys}
                  offering={offering(c.code)} companyView={!!companyId} canManage={canManage} today={matrix.today} companyId={companyId}
                  canFinance={canFinance} makerBlocked={makerBlocked.has(c.code)} financeOpen={finance?.code === c.code ? finance.mode : null}
                  onFinance={mode => { setFinance({ code: c.code, mode }); setNotice(''); setCautions([]); setStaleNote(''); setEditing(null); }}
                  canRemove={canAddBenefit} onRemoved={name => { setNotice(fillTemplate(t('{benefit} was removed.'), { benefit: name })); void load(asOf); }}
                  onEdit={openCell} onMarkRest={() => setDrafts(list => markRestNotOffered(list, grades, c, new Set(scheduled.keys())))} onOfferingChanged={() => void load(asOf)}
                  say={say} money={money} reasonText={reasonText} componentName={componentName} serverError={serverError} editing={editing} cellByKey={cellByKey} scheduled={scheduled} />)}
              </tbody>;
            })}
          </table>
        </div>

        {finance && componentOf(finance.code) && <FinanceAllowancePanel key={`${finance.code}-${finance.mode}`} mode={finance.mode} component={componentOf(finance.code)!}
          name={componentName(componentOf(finance.code)!)} today={matrix.today} onClose={() => setFinance(null)}
          onMakerBlocked={code => setMakerBlocked(blocked => new Set(blocked).add(code))} onReload={() => { setFinance(null); void load(asOf); }}
          onConfirmed={async confirmed => {
            const benefit = componentName(confirmed); setFinance(null); setStaleNote(''); setCautions(allowanceWarnings(confirmed.warnings));
            setNotice(fillTemplate(t('{benefit} was confirmed. Set its value for each grade, then publish.'), { benefit }));
            await load(asOf);
          }}
          onCorrected={async result => {
            const benefit = componentName(result.component); const from = result.component.cashAllowance?.effectiveFrom;
            setFinance(null); setCautions(allowanceWarnings(result.component.warnings));
            setNotice(from ? fillTemplate(t('{benefit} was changed from {month}.'), { benefit, month: fmt.gregorian(from, 'monthYear') })
              : fillTemplate(t('{benefit} was changed.'), { benefit }));
            setStaleNote(result.staleRuns.length > 0
              ? fillTemplate(t('These draft runs were calculated with the old setting; process them again: {months}.'),
                { months: staleRunMonths(result.staleRuns, iso => fmt.gregorian(iso, 'monthYear')) }) : '');
            await load(asOf);
          }} />}

        {editingDraft && editingComponent && editingGrade && <CellEditor
          key={editing} draft={editingDraft} component={editingComponent} grade={editingGrade} grades={grades} companyView={!!companyId}
          hasCompanyValue={!!cellByKey.get(editing!)?.isCompanyOverride}
          currency={currency} readOnly={!canManage} reasonText={reasonText} componentName={componentName} gradeName={gradeName}
          serverError={serverError(editing!)}
          onChange={patch => update(editing!, patch)}
          onSameForAll={() => setDrafts(list => applySameForAll(list, grades, editingDraft.componentCode, editingDraft.gradeId))}
          onUpward={() => setDrafts(list => applyFromGradeUpward(list, grades, editingDraft.componentCode, editingDraft.gradeId))}
          onReset={() => update(editing!, original[editing!] ?? blankDraft(editingComponent, editingDraft.gradeId))}
          onNext={nextAfter(editing) ? () => openCell(nextAfter(editing)!) : undefined}
          onClose={() => setEditing(null)} />}

        {canManage && <div className="surface flex flex-wrap items-end justify-between gap-3 rounded-2xl p-3">
          <div className="max-w-xl space-y-1 text-xs text-slate-500 dark:text-slate-400">
            <p>{fillTemplate(t('Publishing for: {scope}. Values are never edited in place: the value in force ends the day before and the new one starts.'), { scope: companyLabel })}</p>
            {changed.length > 0 && effectiveFrom && <p className="font-medium text-slate-700 dark:text-slate-200">
              {fillTemplate(t('You are changing {count} value(s) for {grades} grade(s), starting {date}.'),
                { count: changed.length, grades: new Set(changed.map(d => d.gradeId)).size, date: effectiveFrom })}
            </p>}
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

function MatrixRow({ component: c, grades, drafts, original, gapKeys, offering, companyView, canManage, today, companyId, canRemove, onRemoved, onEdit, onMarkRest,
  onOfferingChanged, say, money, reasonText, componentName, serverError, editing, cellByKey, scheduled, canFinance, makerBlocked, financeOpen, onFinance }: {
  component: MatrixComponent; grades: MatrixGrade[]; drafts: Drafts; original: Drafts; gapKeys: Set<string>; offering?: MatrixOffering;
  companyView: boolean; canManage: boolean; today: string; companyId: string; editing: string | null;
  canRemove: boolean; onRemoved: (name: string) => void;
  /** Finance (payroll.approve): confirm a pending cash allowance, or correct a confirmed one. `financeOpen` is the panel open for this row. */
  canFinance: boolean; makerBlocked: boolean; financeOpen: 'confirm' | 'correct' | null; onFinance: (mode: 'confirm' | 'correct') => void;
  onEdit: (key: string) => void; onMarkRest: () => void; onOfferingChanged: () => void;
  say: (parts: SummaryPart[]) => string; money: (n: number) => string;
  reasonText: (r: BlockReason | null | undefined, field: 'title' | 'why' | 'fix') => string; componentName: (c: MatrixComponent) => string;
  serverError: (key: string) => MatrixCellError | undefined; cellByKey: Map<string, MatrixCell>; scheduled: Map<string, string>;
}) {
  const { t } = useLocale();
  const skipped = offering?.mode === 'Skipped';
  const pending = isPendingFinance(c);
  const confirmedCash = isCashAllowance(c) && !pending && !!c.cashAllowance;
  const anyUnset = !pending && grades.some(g => drafts[cellKey(g.id, c.code)]?.state === 'unset' && !scheduled.has(cellKey(g.id, c.code)));
  return <tr className={`border-b border-slate-100 align-top dark:border-white/[0.06] ${skipped ? 'opacity-60' : ''}`}>
    <th scope="row" className="sticky start-0 z-10 w-56 min-w-[9rem] bg-white p-3 text-start font-medium text-slate-800 dark:bg-slate-900 dark:text-slate-100">
      <span className="block">{componentName(c)}</span>
      {c.isFloor && <details className="mt-1 text-xs font-normal">
        <summary className="cursor-pointer text-slate-600 dark:text-slate-300">{t('Required by law')}</summary>
        <p className="mt-1 font-semibold text-slate-600 dark:text-slate-300">{reasonText(c.floorReason, 'title')}</p>
        <p className="text-slate-500 dark:text-slate-400">{reasonText(c.floorReason, 'why')}</p>
      </details>}
      {c.isCustom && <span className="mt-1 inline-block rounded-full bg-slate-100 px-2 py-0.5 text-caption font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-300">{t('Added by you')}</span>}
      {isCashAllowance(c) && <span className="ms-1 mt-1 inline-block rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-caption font-semibold text-emerald-800 dark:border-emerald-500/40 dark:bg-emerald-500/10 dark:text-emerald-200">{t('Paid in salary')}</span>}
      {pending && <span className="mt-1 inline-flex items-center gap-1 rounded-full border border-amber-400 bg-amber-50 px-2 py-0.5 text-caption font-semibold text-amber-900 dark:border-amber-500/50 dark:bg-amber-500/10 dark:text-amber-200" data-testid="waiting-for-finance">
        <Hourglass className="h-3 w-3" aria-hidden="true" />{t('Waiting for Finance')}</span>}
      {pending && canFinance && !makerBlocked && <button type="button" className="mt-1 block text-caption font-normal text-sapphire underline" aria-expanded={financeOpen === 'confirm'}
        aria-label={fillTemplate(t('Confirm {benefit}'), { benefit: componentName(c) })} onClick={() => onFinance('confirm')}>{t('Confirm')}</button>}
      {pending && canFinance && makerBlocked && <span className="mt-1 block text-caption font-normal text-slate-600 dark:text-slate-300" data-testid="maker-cannot-confirm">
        {t('You added this allowance, so someone else in Finance must confirm it.')}</span>}
      {pending && !canFinance && <span className="mt-1 block text-caption font-normal text-slate-600 dark:text-slate-300">{t('Finance must confirm it before it can be used.')}</span>}
      {confirmedCash && canFinance && <button type="button" className="mt-1 block text-caption font-normal text-sapphire underline" aria-expanded={financeOpen === 'correct'}
        aria-label={fillTemplate(t('Correct GL account or end of service for {benefit}'), { benefit: componentName(c) })} onClick={() => onFinance('correct')}>{t('Correct GL / end of service')}</button>}
      {c.isCustom && canRemove && <RemoveBenefit code={c.code} name={componentName(c)} onRemoved={onRemoved} />}
      {companyView && offering && offering.mode !== 'Group' && !c.isLoanFacility && <span className={`mt-1 inline-block rounded-full px-2 py-0.5 text-caption font-semibold ${
        offering.mode === 'Skipped' ? 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200'
          : offering.mode === 'Tailored' ? 'bg-sapphire/10 text-sapphire' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
        {t(modeKeys[offering.mode])}
      </span>}
      {companyView && offering?.changesOn && <span className="mt-1 block text-caption font-normal text-slate-500">{fillTemplate(t(offering.offered ? 'Not offered from {date}' : 'Offered again from {date}'), { date: offering.changesOn })}</span>}
      {canManage && companyView && offering?.canBeSkipped && <OfferingSwitch code={c.code} name={componentName(c)} companyId={companyId} offering={offering} today={today} onChanged={onOfferingChanged} />}
      {canManage && !c.isFloor && !c.isLoanFacility && !skipped && anyUnset && <button type="button" className="mt-1 block text-caption font-normal text-sapphire underline" onClick={onMarkRest}>
        {t('Mark the rest Not offered')}
      </button>}
      {c.isLoanFacility && <Link href="/loans" className="mt-1 block text-caption font-normal text-sapphire underline">{t('Edit in Loans → Loan policies → Limits by grade')}</Link>}
    </th>
    {grades.map(g => {
      const key = cellKey(g.id, c.code);
      const d = drafts[key];
      if (!d) return <td key={g.id} className="p-2" />;
      const dirty = original[key] && JSON.stringify(original[key]) !== JSON.stringify(d);
      const problem = dirty ? draftProblem(d, c) : null;
      const gap = !pending && gapKeys.has(key) && d.state === 'unset';
      const startsOn = d.state === 'unset' ? scheduled.get(key) : undefined;
      const summary = pending ? t('Waiting for Finance') : c.isLoanFacility ? loanSummary(cellByKey.get(key), money, t)
        : startsOn ? fillTemplate(t('Starts {date}'), { date: startsOn }) : say(summarise(d, money));
      const tone = problem || serverError(key) ? 'border-red-400 bg-red-50 dark:bg-red-500/10'
        : dirty ? 'border-sapphire bg-sapphire/5'
        : gap ? 'border-amber-400 bg-amber-50 dark:bg-amber-500/10'
        : startsOn ? 'border-dashed border-sapphire/40'
        : 'border-slate-200 dark:border-white/10';
      return <td key={g.id} className="p-2">
        <button type="button" disabled={c.isLoanFacility || skipped || pending} aria-pressed={editing === key}
          aria-label={fillTemplate(t('{benefit} for {grade}: {value}'), { benefit: componentName(c), grade: g.code,
            value: [summary, gap && !dirty ? t('Needs a value') : '', dirty ? t('Changed — not published yet') : ''].filter(Boolean).join(', ') })}
          onClick={() => onEdit(key)}
          className={`w-full min-w-[120px] rounded-lg border px-2 py-1.5 text-start text-xs transition hover:border-sapphire disabled:cursor-default disabled:hover:border-slate-200 ${tone}`}>
          <bdi className={d.state === 'notOffered' || d.state === 'unset' || pending ? 'text-slate-500' : 'text-slate-800 dark:text-slate-100'}>{pending && <Hourglass className="me-1 inline h-3 w-3" aria-hidden="true" />}{summary}</bdi>
          {gap && !dirty && <span className="mt-0.5 block text-caption font-semibold text-amber-700 dark:text-amber-300">{t('Needs a value')}</span>}
          {companyView && !dirty && cellByKey.get(key) && <span className="mt-0.5 block text-caption text-slate-400">{t(cellByKey.get(key)!.isCompanyOverride ? 'This company' : 'Group default')}</span>}
          {dirty && <span className="mt-0.5 block text-caption font-semibold text-sapphire">{t('Changed — not published yet')}</span>}
          {problem && <span className="mt-0.5 block text-caption text-red-600">{problem === FLOOR_PROBLEM ? reasonText(c.floorReason, 'title') : t(problem)}</span>}
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

/** Downloads the grid as a CSV file Excel opens (UTF-8 with a byte-order mark, so Arabic names survive). */
function downloadCsv(csv: string, scope: string) {
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = `benefits-by-grade-${scope || 'group'}.csv`;
  document.body.appendChild(a); a.click(); a.remove();
  URL.revokeObjectURL(url);
}

/** Paste cells copied from Excel, or choose a CSV file; check what can be read, then put it in the grid unpublished. */
function ImportPanel({ components, grades, onApply, onClose }: {
  components: MatrixComponent[]; grades: MatrixGrade[]; onApply: (result: GridImport) => void; onClose: () => void;
}) {
  const { t } = useLocale();
  const [text, setText] = useState('');
  const result = useMemo(() => (text.trim() ? importGrid(text, components, grades) : null), [text, components, grades]);
  const readFile = async (file: File | undefined) => { if (file) setText(await file.text()); };
  return <section aria-labelledby="import-grid-heading" className="surface space-y-3 rounded-2xl border border-sapphire/30 p-4">
    <div>
      <h2 id="import-grid-heading" className="font-semibold text-slate-800 dark:text-slate-100">{t('Import from Excel')}</h2>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Copy the cells in Excel, with the grade codes in the first row and the benefits in the first column, and paste them here. Or choose a CSV file exported from this screen.')}</p>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Write an amount (1500), a percentage of basic (25%), In kind, a count (2x), a class of cover (B) or Not offered. Nothing is published until you press Publish.')}</p>
    </div>
    <label className="block text-sm">{t('Pasted cells')}
      <textarea className="input mt-1 h-32 w-full font-mono text-xs" dir="ltr" value={text} onChange={e => setText(e.target.value)} />
    </label>
    <label className="block text-sm">{t('Or choose a CSV file')}
      <input type="file" accept=".csv,.txt,text/csv" className="mt-1 block text-xs" onChange={e => void readFile(e.target.files?.[0])} />
    </label>
    {result && <div className="space-y-1 text-sm" aria-live="polite">
      <p className="font-medium text-slate-700 dark:text-slate-200">{fillTemplate(t('{count} value(s) can be added to the grid.'), { count: result.patches.length })}</p>
      {result.errors.length > 0 && <>
        <p className="text-red-600">{fillTemplate(t('{count} value(s) could not be read and will be left out. They are listed below.'), { count: result.errors.length })}</p>
        <ul className="max-h-40 list-disc overflow-auto ps-5 text-xs text-red-700 dark:text-red-300">
          {result.errors.slice(0, 50).map(e => <li key={`${e.row}-${e.column}`}>
            {fillTemplate(t('Row {row}, column {column} ({benefit} {grade}): {problem}'), { row: e.row, column: e.column, benefit: e.benefit, grade: e.grade, problem: t(e.message) })}
          </li>)}
        </ul>
      </>}
    </div>}
    <div className="flex flex-wrap gap-2">
      <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!result || result.patches.length === 0} onClick={() => result && onApply(result)}>{t('Put these values in the grid')}</button>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onClose}>{t('Cancel')}</button>
    </div>
  </section>;
}

/**
 * The guided path through the grid: ① grades, ② the benefits on offer, ③ a value for every grade, ④ review and publish.
 * Every step stays a status the admin can act on, never a gate: they can work in any order.
 */
function GuideStrip({ progress, changes, canManage, canAddBenefit, pendingNames, nextLabel, onNext, onAddBenefit }: {
  progress: { total: number; set: number; nextKey: string | null }; changes: number; canManage: boolean; canAddBenefit: boolean;
  /** Cash allowances waiting for Finance: nothing can be set for them until it confirms. */
  pendingNames: string[];
  nextLabel: string; onNext: () => void; onAddBenefit: () => void;
}) {
  const { t } = useLocale();
  const valuesDone = progress.total > 0 && progress.set === progress.total;
  const current = !valuesDone ? 3 : changes > 0 ? 4 : 0;
  const steps: { n: number; label: string; detail: string; done: boolean }[] = [
    { n: 1, label: t('Grades'), detail: t('Ready'), done: true },
    { n: 2, label: t('Choose benefits'), detail: t('Add or turn off benefits'), done: true },
    { n: 3, label: t('Set values'), detail: fillTemplate(t('{set} of {total} set'), { set: progress.set, total: progress.total }), done: valuesDone },
    { n: 4, label: t('Review & publish'), detail: changes > 0 ? fillTemplate(t('{count} change(s) to publish'), { count: changes }) : t('Nothing waiting'), done: valuesDone && changes === 0 },
  ];
  return <nav aria-label={t('Steps to set benefits by grade')} className="surface rounded-2xl p-3">
    <ol className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
      {steps.map(step => <li key={step.n} aria-current={step.n === current ? 'step' : undefined}
        className={`flex items-center gap-2 ${step.n === current ? 'font-semibold text-sapphire' : 'text-slate-700 dark:text-slate-200'}`}>
        <span aria-hidden="true" className={`grid h-6 w-6 place-items-center rounded-full text-xs ${step.done ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200' : step.n === current ? 'bg-sapphire text-white' : 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300'}`}>
          {step.done ? '✓' : step.n}
        </span>
        <span>{step.label}<span className="block text-caption font-normal text-slate-500 dark:text-slate-400">{step.detail}</span></span>
      </li>)}
    </ol>
    {pendingNames.length > 0 && <p className="mt-3 flex items-start gap-2 border-t border-slate-100 pt-3 text-sm text-amber-900 dark:border-white/10 dark:text-amber-200" data-testid="guide-waiting-for-finance">
      <Hourglass className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      {t('{count, plural, one {Nothing can be set for {benefits} until Finance confirms it.} other {Nothing can be set for {benefits} until Finance confirms them.}}',
        { count: pendingNames.length, benefits: pendingNames.join(', ') })}
    </p>}
    {canManage && <div className="mt-3 flex flex-wrap items-center gap-3 border-t border-slate-100 pt-3 text-sm dark:border-white/10" aria-live="polite">
      {progress.nextKey
        ? <>
          <span className="text-slate-700 dark:text-slate-200">{fillTemplate(t('Next: {item}'), { item: nextLabel })}</span>
          <button type="button" className="btn-primary h-8 px-3 text-xs" onClick={onNext}>{t('Set next value')}</button>
        </>
        : <span className="text-slate-700 dark:text-slate-200">{t(changes > 0 ? 'Every grade has a value. Review the changes below, then publish.' : 'Every grade has a value.')}</span>}
      {canAddBenefit && <button type="button" className="text-xs text-sapphire underline" onClick={onAddBenefit}>{t('Add a benefit')}</button>}
    </div>}
  </nav>;
}

/**
 * Adds a benefit of the customer's own to the grid: a name (English, Arabic or both), then whether it is paid in the monthly
 * salary. A contract benefit is only recorded on the package; a monthly cash allowance needs the GL account its expense posts
 * to and an end of service choice (a category and a note). GOSI and WPS are shown as fixed. What the server refuses is said next
 * to its field. Added by someone without the Finance permission, a cash allowance waits for Finance to confirm it.
 */
function AddBenefitPanel({ onAdded, onClose }: { onAdded: (name: string, added: MatrixComponent) => Promise<void>; onClose: () => void }) {
  const { t, locale } = useLocale();
  const [form, setForm] = useState<AddBenefitForm>(blankAddBenefit);
  const [busy, setBusy] = useState(false);
  const [problems, setProblems] = useState<AddBenefitProblems>({});
  const [server, setServer] = useState<ServerProblem | null>(null);
  const [focus, setFocus] = useState<{ field: AddBenefitField; n: number } | null>(null);
  const nameRef = useRef<HTMLInputElement>(null);
  const glRef = useRef<HTMLSelectElement>(null);
  const categoryRef = useRef<HTMLSelectElement>(null);
  const reasonRef = useRef<HTMLTextAreaElement>(null);
  const cash = form.kind === 'Cash';
  const set = (patch: Partial<AddBenefitForm>) => { setForm(f => ({ ...f, ...patch })); setServer(null); setProblems({}); };

  // The GL accounts are read when they are first needed: a contract benefit never asks for them.
  const { gl, retry } = useCashGlChoices(cash);

  useEffect(() => {
    if (!focus) return;
    ({ name: nameRef, glDriver: glRef, eosbCategory: categoryRef, eosbReason: reasonRef })[focus.field].current?.focus();
  }, [focus]);
  const ask = (field: AddBenefitField) => setFocus(f => ({ field, n: (f?.n ?? 0) + 1 }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found = addBenefitProblems(form);
    setProblems(found); setServer(null);
    const first = (['name', 'glDriver', 'eosbCategory', 'eosbReason'] as const).find(f => found[f]);
    if (first) { ask(first); return; }
    setBusy(true);
    try {
      const added = await entitlementsApi.addBenefit(addBenefitRequest(form));
      await onAdded(localName(locale, added.nameEn, added.nameAr), added);
    } catch (err) {
      const refused = benefitServerProblem(err, t('Unable to add the benefit.'));
      setServer(refused); setBusy(false);
      if (refused.field !== 'general') ask(refused.field);
    }
  };

  const shown = (field: AddBenefitField) => (server?.field === field ? (server.key ? t(server.key) : server.message ?? '') : '') || (problems[field] ? t(problems[field]!) : '');
  const nameError = shown('name'); const glError = shown('glDriver');
  const generalError = server?.field === 'general' ? server.message ?? '' : '';

  return <form onSubmit={e => void submit(e)} aria-labelledby="add-benefit-heading" noValidate className="surface space-y-3 rounded-2xl border border-sapphire/30 p-4">
    <div>
      <h2 id="add-benefit-heading" className="font-semibold text-slate-800 dark:text-slate-100">{t('Add a benefit')}</h2>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('It is added for every grade and company. Set its value for each grade in the grid, then publish.')}</p>
    </div>
    <div className="grid gap-3 sm:grid-cols-2">
      <p className="text-xs text-slate-500 sm:col-span-2 dark:text-slate-400">{t('Enter the name in English, Arabic or both.')}</p>
      <label className="text-sm">{t('Benefit name (English)')}
        <input ref={nameRef} className="input mt-1 w-full" maxLength={100} dir="ltr" value={form.nameEn} aria-invalid={!!nameError} aria-describedby={nameError ? 'add-benefit-name-error' : undefined}
          onChange={e => set({ nameEn: e.target.value })} />
      </label>
      <label className="text-sm">{t('Benefit name (Arabic)')}
        <input className="input mt-1 w-full" maxLength={100} dir="rtl" value={form.nameAr} aria-invalid={!!nameError} aria-describedby={nameError ? 'add-benefit-name-error' : undefined}
          onChange={e => set({ nameAr: e.target.value })} />
      </label>
      {nameError && <p id="add-benefit-name-error" role="alert" className="text-sm text-red-600 sm:col-span-2">{nameError}</p>}
    </div>

    <fieldset className="space-y-2">
      <legend className="text-sm font-medium text-slate-800 dark:text-slate-100">{t('Is it paid in the monthly salary?')}</legend>
      <label className="flex items-start gap-2 text-sm">
        <input type="radio" name="benefit-kind" className="mt-1" checked={!cash} onChange={() => set({ kind: 'Contract' })} />
        <span>{t('No, it’s a contract benefit (recorded on the employee’s package, not paid in salary)')}</span>
      </label>
      <label className="flex items-start gap-2 text-sm">
        <input type="radio" name="benefit-kind" className="mt-1" checked={cash} onChange={() => set({ kind: 'Cash' })} />
        <span>{t('Yes, a monthly cash allowance paid in the salary')}</span>
      </label>
    </fieldset>

    {cash && <div className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="cash-allowance-fields">
      <GlAccountField idPrefix="add-benefit" gl={gl} value={form.glDriverKey} onChange={key => set({ glDriverKey: key })} error={glError} selectRef={glRef} onRetry={retry}
        forbiddenKey="You don’t have access to the GL accounts. Ask someone in Finance to add this allowance, or to give you access." />

      <dl className="grid gap-2 text-sm sm:grid-cols-2">
        <div>
          <dt className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('GOSI (social insurance)')} <span className="font-normal text-slate-500">· {t('Fixed')}</span></dt>
          <dd className="text-slate-800 dark:text-slate-100">{t('No — GOSI is calculated on basic + housing only')}</dd>
        </div>
        <div>
          <dt className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('WPS (salary transfer)')} <span className="font-normal text-slate-500">· {t('Fixed')}</span></dt>
          <dd className="text-slate-800 dark:text-slate-100">{t('Yes — paid in the salary transfer')}</dd>
        </div>
      </dl>

      <EosbFields idPrefix="add-benefit" value={form} onChange={patch => set(patch)} categoryError={shown('eosbCategory')} reasonError={shown('eosbReason')}
        categoryRef={categoryRef} reasonRef={reasonRef} />

      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Each employee’s amount is set on their salary and approved by Finance before it’s paid.')}</p>
    </div>}

    {generalError && <p role="alert" className="text-sm text-red-600">{generalError}</p>}
    <div className="flex flex-wrap gap-2">
      <button type="submit" className="btn-primary h-8 px-3 text-xs" disabled={busy}>{t('Add benefit')}</button>
      <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy} onClick={onClose}>{t('Cancel')}</button>
    </div>
  </form>;
}

/** Takes away a benefit that was added by mistake. The server refuses once any grade has a value for it. */
function RemoveBenefit({ code, name, onRemoved }: { code: string; name: string; onRemoved: (name: string) => void }) {
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const remove = async () => {
    setBusy(true); setError('');
    try { await entitlementsApi.removeBenefit(code); onRemoved(name); }
    catch (e) { setError(loanErrorMessage(e, t('Unable to remove the benefit.'))); setBusy(false); }
  };
  if (!open) return <button type="button" className="mt-1 block text-caption font-normal text-sapphire underline" onClick={() => setOpen(true)}>{t('Remove benefit')}</button>;
  return <div className="mt-2 space-y-2 rounded-lg border border-slate-200 p-2 text-xs font-normal dark:border-white/10">
    <p>{fillTemplate(t('Remove {benefit}? This works only while no grade has a value for it.'), { benefit: name })}</p>
    {error && <p role="alert" className="text-red-600">{error}</p>}
    <div className="flex gap-2">
      <button type="button" className="btn-primary h-7 px-2 text-xs" disabled={busy} onClick={() => void remove()}>{t('Yes, remove it')}</button>
      <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={busy} onClick={() => { setOpen(false); setError(''); }}>{t('Keep it')}</button>
    </div>
  </div>;
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
  if (!open) return <button type="button" className="mt-1 block text-caption font-normal text-sapphire underline" onClick={() => setOpen(true)}>
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
  onChange, onSameForAll, onUpward, onReset, onNext, onClose }: {
  draft: CellDraft; component: MatrixComponent; grade: MatrixGrade; grades: MatrixGrade[]; companyView: boolean; hasCompanyValue: boolean;
  currency: string; readOnly: boolean; reasonText: (r: BlockReason | null | undefined, field: 'title' | 'why' | 'fix') => string;
  componentName: (c: MatrixComponent) => string; gradeName: (g: MatrixGrade) => string; serverError?: MatrixCellError;
  onChange: (patch: Partial<CellDraft>) => void; onSameForAll: () => void; onUpward: () => void; onReset: () => void; onClose: () => void;
  /** Keeps this value and opens the next grade value still to set; absent when none is left. */
  onNext?: () => void;
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
      <div className="flex flex-wrap gap-2">
        {onNext && !readOnly && <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!!problem} onClick={onNext}>{t('Save & next')}</button>}
        <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={onClose}>{t('Done editing')}</button>
      </div>
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
            {c.allowedValueTypes.map(v => <option key={v} value={v}>{t(v === 'Quantity' && c.isCustom ? 'Number of items' : valueTypeKeys[v] ?? v)}</option>)}
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
