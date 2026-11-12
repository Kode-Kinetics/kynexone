'use client';

import Link from 'next/link';
import { Banknote, Check, ChevronLeft, ChevronRight, Hourglass, Info, Plus, TriangleAlert } from 'lucide-react';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent } from 'react';
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
import { useMediaQuery } from '../../hooks/useMediaQuery';
import { useRetiredBenefitPlans } from '../../hooks/useRetiredBenefitPlans';
import { InfoTip } from '../InfoTip';
import { Modal } from '../Modal';
import { EmptyState } from '../ui/EmptyState';
import { ResultBanner } from '../ui/ResultBanner';
import { Skeleton } from '../ui/Skeleton';
import { ActionMenu, type ActionMenuItem } from './ActionMenu';
import { MatrixCell as GridCell } from './MatrixCell';
import {
  cellView, gradeWindow, monthlyFixedTotals, needsValueCodes, rowChip, statusSummary, type RowChip, type StatusItem,
} from '../../lib/benefitsGrid';
import {
  FLOOR_PROBLEM, applyFromGradeUpward, applySameForAll, blankDraft, cellKey, dependantKeys, draftFromCell, draftProblem, groupKeys, groupOrder,
  inputFromDraft, markRestNotOffered, monthStarts, periodChoiceKeys, sameDraft, summarise, tierKeys, type CellDraft, type SummaryPart,
  draftStorageKey, defaultEffectiveFrom, guideProgress, restoreDrafts, savedFrom, type SavedDrafts, gridToCsv, importGrid, type GridImport,
} from '../../lib/entitlementMatrix';

const valueTypeKeys: Record<string, string> = {
  Amount: 'Fixed amount', PercentOfBasic: '% of basic salary', InKind: 'Provided in kind', CoverageTier: 'Class of cover',
  Quantity: 'Number of tickets', MultipleOfBasic: '× basic salary', MultipleOfGross: '× gross salary', MultipleOfHousing: '× housing allowance',
  EligibilityOnly: 'Eligible',
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
  // What the last Add benefit did, kept until dismissed (Screen Standard 3d); the new row carries a New chip until the scope changes.
  const [added, setAdded] = useState<{ code: string; name: string; pending: boolean } | null>(null);
  const [newCode, setNewCode] = useState<string | null>(null);
  const [onlyGaps, setOnlyGaps] = useState(false);
  const phone = useMediaQuery('(max-width: 639px)');
  const [phoneGrade, setPhoneGrade] = useState('');
  // Benefit plans made before Release A stay readable; the toolbar links to them only when there are some this viewer may open.
  const plansLink = useRetiredBenefitPlans(true).inUse;
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
    // Editing the grid as of a later date publishes from that date by default (an earlier date would sit under it).
    setEffectiveFrom(current => defaultEffectiveFrom(current, m));
    return kept.restored;
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
  useEffect(() => { setAsOf(null); setNotice(''); setError(''); setEditing(null); setFinance(null); setCautions([]); setStaleNote(''); setAdded(null); setNewCode(null); void load(); }, [load]);

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
    if (!effectiveFrom) { setError(t('Choose the date these changes start from.')); return; }
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

  // The benefits an admin can still fill: what the "Needs a value" filter keeps.
  const needs = useMemo(() => needsValueCodes(components, grades, drafts, scheduledKeys, pendingCodes), [components, grades, drafts, scheduledKeys, pendingCodes]);
  const filtering = onlyGaps && needs.size > 0;
  const shown = useMemo(() => (filtering ? components.filter(c => needs.has(c.code)) : components), [filtering, components, needs]);
  const totals = useMemo(() => monthlyFixedTotals(shown, grades, drafts), [shown, grades, drafts]);
  const status = statusSummary({ set: progress.set, total: progress.total, pendingFinance: pendingCodes.size, changes: changed.length, nextStart: firstScheduled ?? null });
  const phoneGradeId = grades.find(g => g.id === phoneGrade)?.id ?? grades[0]?.id ?? '';

  // Which grades are in view, and whether the grid scrolls sideways at all (measured, so it holds in either direction).
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ from: 0, to: 0, total: 0, atStart: true, atEnd: true, scrolls: false });
  const measure = useCallback(() => {
    const el = scroller.current;
    if (!el) return;
    const rtl = getComputedStyle(el).direction === 'rtl';
    const box = el.getBoundingClientRect();
    const sticky = el.querySelector<HTMLElement>('th[data-first]')?.getBoundingClientRect().width ?? 0;
    const cols = Array.from(el.querySelectorAll<HTMLElement>('th[data-grade]')).map(th => {
      const r = th.getBoundingClientRect();
      return rtl ? { start: -r.right, end: -r.left } : { start: r.left, end: r.right };
    });
    const window_ = gradeWindow(rtl ? { start: -(box.right - sticky), end: -box.left } : { start: box.left + sticky, end: box.right }, cols);
    const max = el.scrollWidth - el.clientWidth;
    const position = Math.abs(el.scrollLeft);
    const next = { ...window_, atStart: position <= 1, atEnd: position >= max - 1, scrolls: max > 1 };
    setView(prev => (prev.from === next.from && prev.to === next.to && prev.total === next.total && prev.atStart === next.atStart && prev.atEnd === next.atEnd && prev.scrolls === next.scrolls ? prev : next));
  }, []);
  const gridShown = !!matrix && !loading && !phone && grades.length > 0;
  useLayoutEffect(() => { if (gridShown) measure(); });
  useEffect(() => {
    const el = scroller.current;
    if (!gridShown || !el) return;
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(el);
    el.addEventListener('scroll', measure, { passive: true });
    window.addEventListener('resize', measure);
    return () => { observer?.disconnect(); el.removeEventListener('scroll', measure); window.removeEventListener('resize', measure); };
  }, [gridShown, measure]);
  const scrollGrades = (direction: 1 | -1) => {
    const el = scroller.current;
    if (!el) return;
    const rtl = getComputedStyle(el).direction === 'rtl';
    const width = el.querySelector<HTMLElement>('th[data-grade]')?.getBoundingClientRect().width ?? 120;
    const reduce = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollBy({ left: direction * (rtl ? -1 : 1) * width, behavior: reduce ? 'auto' : 'smooth' });
  };

  const addedBenefit = (name: string, benefit: MatrixComponent) => {
    setAdding(false); setStaleNote(''); setCautions(allowanceWarnings(benefit.warnings)); setNotice('');
    setAdded({ code: benefit.code, name, pending: isPendingFinance(benefit) });
    setNewCode(benefit.code);
  };
  const scopeNote = t(companyId
    ? 'This company uses the group values unless you tailor a grade. Values you set here take priority over the group default.'
    : 'These values apply to every company unless a company tailors or skips a benefit.');

  return <section className="space-y-3" aria-labelledby="benefits-matrix-heading">
    {embedded
      ? <h2 id="benefits-matrix-heading" className="sr-only">{t('Benefits by grade')}</h2>
      : <div>
        <h1 id="benefits-matrix-heading" className="text-h2 font-semibold text-fg">{t('Benefits by grade')}</h1>
        <p data-purpose className="mt-1 max-w-[70ch] text-body text-fg-muted">{t('Set what each grade receives, per company. Changes start on the date you choose when you publish.')}</p>
      </div>}

    {matrix && grades.length > 0 && <GuideStrip progress={progress} changes={changed.length} canManage={canManage} status={status}
      pendingNames={components.filter(isPendingFinance).map(componentName)}
      nextLabel={progress.nextKey ? nextLabel(progress.nextKey) : ''}
      onNext={() => progress.nextKey && openCell(progress.nextKey)} />}

    {(matrix || loading) && <>
      <div data-testid="matrix-toolbar" className="flex flex-wrap items-center gap-x-2.5 gap-y-2">
        <label className="inline-flex items-center gap-2 whitespace-nowrap text-body text-fg max-sm:w-full">{t('Applies to')}
          <select className="select w-40 max-sm:min-w-0 max-sm:flex-1" value={companyId} onChange={e => setCompanyId(e.target.value)} aria-label={t('Applies to')}>
            {isGroupScope && <option value="">{t('All companies (group default)')}</option>}
            {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <InfoTip label={t('Applies to')} text={scopeNote} />
        {matrix && needs.size > 0 && <button type="button" aria-pressed={filtering} onClick={() => setOnlyGaps(on => !on)}
          className={`inline-flex h-7 items-center gap-1.5 rounded-full border px-3 text-body-sm font-semibold transition-colors duration-fast ${filtering
            ? 'border-primary bg-primary text-on-primary' : 'border-warning bg-warning-soft text-warning-fg hover:border-line-strong'}`}>
          {filtering ? <Check className="h-3.5 w-3.5" aria-hidden /> : <TriangleAlert className="h-3.5 w-3.5" aria-hidden />}
          {fillTemplate(t('Needs a value ({count})'), { count: needs.size })}
        </button>}
        {!matrix ? null : canManage
          ? <span className="text-meta text-fg-muted">{t('Select a value to edit it.')}</span>
          : <span className="inline-flex items-center gap-1 text-meta font-semibold text-fg-muted">{t('View only')}<InfoTip label={t('View only')} text={t('You can see these values. HR administrators change them.')} /></span>}
        <div className="ms-auto flex flex-wrap items-center gap-2">
          {gridShown && view.scrolls && view.total > 1 && <div className="flex items-center gap-1" data-testid="grade-scroll-controls">
            <button type="button" aria-label={t('Show earlier grades')} disabled={view.atStart} onClick={() => scrollGrades(-1)}
              className="grid h-7 w-7 place-items-center rounded-lg border border-line text-fg hover:bg-surface-sunken disabled:text-fg-subtle disabled:opacity-60">
              <ChevronLeft className="h-4 w-4 rtl:rotate-180" aria-hidden /></button>
            <span data-testid="grades-status" aria-live="polite" className="min-w-[8.5rem] text-center text-meta text-fg-muted tabular-nums">
              {fillTemplate(t('Grades {from}-{to} of {total}'), { from: view.from, to: view.to, total: view.total })}</span>
            <button type="button" aria-label={t('Show later grades')} disabled={view.atEnd} onClick={() => scrollGrades(1)}
              className="grid h-7 w-7 place-items-center rounded-lg border border-line text-fg hover:bg-surface-sunken disabled:text-fg-subtle disabled:opacity-60">
              <ChevronRight className="h-4 w-4 rtl:rotate-180" aria-hidden /></button>
          </div>}
          {canAddBenefit && <button type="button" className="btn-secondary btn-sm" aria-haspopup="dialog" disabled={loading || !matrix} onClick={() => setAdding(true)}>
            <Plus className="h-4 w-4" aria-hidden />{t('Add benefit')}</button>}
          {matrix && <ActionMenu label={t('More')} items={[
            { key: 'export', label: t('Export to Excel (CSV)'), onSelect: () => downloadCsv(gridToCsv(components, grades, drafts), companyId) },
            ...(canManage ? [{ key: 'import', label: t('Import from Excel'), onSelect: () => setImporting(true) }] : []),
            ...(plansLink ? [{ key: 'plans', label: t('Open in Benefit plans'), href: '/setup?tab=benefitPlans' }] : []),
          ] satisfies ActionMenuItem[]} />}
        </div>
      </div>
    </>}

    {adding && canAddBenefit && <AddBenefitDialog onClose={() => setAdding(false)} onAdded={async (name, benefit) => { addedBenefit(name, benefit); await load(asOf); }} />}

    {added && <ResultBanner
      changed={added.pending ? t('Added. Finance must confirm the GL account and end-of-service setting before it can be used.') : fillTemplate(t('{benefit} was added.'), { benefit: added.name })}
      where={added.pending ? undefined : t('Benefits by grade, group default. Not live until you publish values.')}
      next={!added.pending && grades[0] ? { label: fillTemplate(t('Set values for {benefit}'), { benefit: added.name }), onClick: () => openCell(cellKey(grades[0].id, added.code)) } : undefined}
      onDismiss={() => setAdded(null)} />}
    {error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
    {notice && <p role="status" className="text-body text-success-fg">{notice}</p>}
    {cautions.length > 0 && <div role="status" className="rounded-xl border border-info/30 bg-info-soft p-3 text-body text-fg" data-testid="allowance-cautions">
      <p className="flex items-center gap-2 font-semibold"><Info className="h-4 w-4 shrink-0" aria-hidden="true" />{t('Worth a second look')}</p>
      <ul className="mt-1 list-disc space-y-0.5 ps-9 text-meta">{cautions.map(c => <li key={c}>{t(c)}</li>)}</ul>
    </div>}
    {staleNote && <p role="status" className="flex items-start gap-2 rounded-xl border border-warning/40 bg-warning-soft p-3 text-body text-warning-fg" data-testid="stale-runs">
      <TriangleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{staleNote}</p>}
    {asOf && <p role="status" className="rounded-lg border border-primary/30 bg-primary-soft p-2 text-body text-fg">
      {fillTemplate(t('Showing the values that apply from {date}. Until then the current values stay in force.'), { date: asOf })}
      {' '}<button type="button" className="font-semibold text-primary-fg underline" onClick={() => { setAsOf(null); void load(); }}>{t('Show current values')}</button>
    </p>}

    {loading ? <div role="status" aria-busy="true" aria-label={t('Loading benefits by grade')} className="space-y-2">
        <Skeleton className="h-12 w-full" /><Skeleton className="h-8 w-2/3" />
        {Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="h-12 w-full" />)}
      </div>
      : loadError ? <div role="alert" className="surface space-y-2 p-4 text-body">
        <p className="text-danger-fg">{t('Benefits by grade did not load: the server did not respond.')} {loadError}</p>
        <button type="button" className="btn-secondary" onClick={() => void load()}>{t('Try again')}</button></div>
      : !matrix ? null
      : grades.length === 0 ? <div className="surface"><EmptyState kind="first-use" title={t('No grades yet')}
        description={t('Grades decide what each employee receives. Add grades first, then set their benefits here.')}
        action={{ label: t('Add the first grade'), href: '/setup?tab=grades' }} /></div>
      : <>
        {scheduled.size > 0 && firstScheduled && <div role="status" className="rounded-xl border border-primary/30 bg-primary-soft p-3 text-body text-fg">
          <p className="font-semibold">{fillTemplate(t('{count} published value(s) start later, the first on {date}.'), { count: scheduled.size, date: firstScheduled })}</p>
          <p className="text-meta">{t('Until then those grades have no value for that benefit.')}{' '}
            <button type="button" className="font-semibold text-primary-fg underline" onClick={() => { setAsOf(firstScheduled); void load(firstScheduled); }}>
              {fillTemplate(t('Show the values from {date}'), { date: firstScheduled })}
            </button></p>
        </div>}
        <WaitingForFinance items={waitingForFinance(components, canFinance, makerBlocked)}
          nameOf={code => { const c = componentOf(code); return c ? componentName(c) : code; }}
          onConfirm={code => { setFinance({ code, mode: 'confirm' }); setNotice(''); setCautions([]); setStaleNote(''); setEditing(null); }} />

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

        {(() => {
          const rowProps = (c: MatrixComponent) => ({
            component: c, grades, drafts, original, gapKeys, offering: offering(c.code), companyView: !!companyId, canManage, today: matrix.today, companyId,
            canFinance, makerBlocked: makerBlocked.has(c.code), financeOpen: finance?.code === c.code ? finance.mode : null,
            onFinance: (mode: 'confirm' | 'correct') => { setFinance({ code: c.code, mode }); setNotice(''); setCautions([]); setStaleNote(''); setEditing(null); },
            canRemove: canAddBenefit, onRemoved: (name: string) => { setNotice(fillTemplate(t('{benefit} was removed.'), { benefit: name })); void load(asOf); },
            onEdit: openCell, onMarkRest: () => setDrafts(list => markRestNotOffered(list, grades, c, new Set(scheduled.keys()))), onOfferingChanged: () => void load(asOf),
            say, money, reasonText, componentName, serverError, editing, cellByKey, scheduled, isNew: newCode === c.code,
          });
          const groups = [...groupOrder, 'Loans' as const].flatMap(group => {
            const rows = group === 'Loans' ? shown.filter(c => c.isLoanFacility) : shown.filter(c => c.group === group && !c.isLoanFacility);
            return rows.length === 0 ? [] : [{ group, rows }];
          });
          const bandLabel = (group: (typeof groups)[number]['group']) => t(group === 'Loans' ? 'Loan facilities — set in Loans' : groupKeys[group]);

          if (phone) return <div data-testid="grade-cards" className="space-y-3">
            <div className="flex items-center gap-2">
              <button type="button" aria-label={t('Previous grade')} disabled={grades.findIndex(g => g.id === phoneGradeId) <= 0}
                onClick={() => setPhoneGrade(grades[Math.max(0, grades.findIndex(g => g.id === phoneGradeId) - 1)].id)}
                className="grid h-11 w-11 shrink-0 place-items-center rounded-lg border border-line text-fg disabled:opacity-50"><ChevronLeft className="h-4 w-4 rtl:rotate-180" aria-hidden /></button>
              <select className="select min-w-0 flex-1" aria-label={t('Grade')} value={phoneGradeId} onChange={e => setPhoneGrade(e.target.value)}>
                {grades.map(g => <option key={g.id} value={g.id}>{gradeName(g)} · {g.code}</option>)}
              </select>
              <button type="button" aria-label={t('Next grade')} disabled={grades.findIndex(g => g.id === phoneGradeId) >= grades.length - 1}
                onClick={() => setPhoneGrade(grades[Math.min(grades.length - 1, grades.findIndex(g => g.id === phoneGradeId) + 1)].id)}
                className="grid h-11 w-11 shrink-0 place-items-center rounded-lg border border-line text-fg disabled:opacity-50"><ChevronRight className="h-4 w-4 rtl:rotate-180" aria-hidden /></button>
            </div>
            {groups.map(({ group, rows }) => <section key={group} aria-label={bandLabel(group)} className="space-y-1.5">
              <h3 className="px-1 text-caption font-semibold uppercase tracking-wide text-fg-muted">{bandLabel(group)}</h3>
              <ul className="divide-y divide-line overflow-hidden rounded-xl border border-line bg-surface">
                {rows.map(c => <MatrixRow key={c.code} layout="card" onlyGradeId={phoneGradeId} {...rowProps(c)} />)}
              </ul>
            </section>)}
            {totals && <p data-testid="monthly-total-row" className="surface flex items-center justify-between gap-2 p-3 text-body">
              <span className="text-fg-muted">{t('Fixed monthly allowances (amounts only)')}</span>
              <bdi className="font-semibold tabular-nums">{totals.byGrade[phoneGradeId] === undefined ? '—' : money(totals.byGrade[phoneGradeId])}</bdi></p>}
          </div>;

          return <div className="relative surface overflow-hidden">
            <div ref={scroller} role="region" tabIndex={0} aria-label={t('Benefits by grade, scrolls sideways')} data-testid="grid-scroller"
              className="max-h-[calc(100dvh-8rem)] overflow-auto rounded-xl focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
              <table className="w-full text-body" style={{ minWidth: `${FIRST_COLUMN_REM + GRADE_COLUMN_REM * grades.length}rem` }}>
                <thead>
                  <tr className="border-b border-line">
                    <th scope="col" data-first className="sticky start-0 top-0 z-30 w-[10.25rem] min-w-[10.25rem] bg-surface px-2 py-2 text-start text-meta font-semibold text-fg-muted">{t('Benefit')}</th>
                    {grades.map(g => <th key={g.id} scope="col" data-grade className="sticky top-0 z-20 min-w-[6.5rem] bg-surface px-2 py-2 text-start text-meta font-semibold text-fg">
                      {gradeName(g)}<bdi className="block font-normal text-fg-muted" dir="ltr">{g.code}</bdi>
                    </th>)}
                  </tr>
                </thead>
                {groups.map(({ group, rows }) => <tbody key={group}>
                  <tr><th colSpan={grades.length + 1} scope="colgroup" className="bg-surface-sunken px-3 py-1.5 text-start text-caption font-semibold uppercase tracking-wide text-fg-muted">
                    <span className="sticky start-3">{bandLabel(group)}</span>
                  </th></tr>
                  {rows.map(c => <MatrixRow key={c.code} layout="table" {...rowProps(c)} />)}
                </tbody>)}
                {totals && <tfoot>
                  <tr data-testid="monthly-total-row" className="border-t border-line bg-surface-sunken">
                    <th scope="row" className="sticky start-0 z-10 bg-surface-sunken px-3 py-2 text-start text-meta font-semibold text-fg"
                      title={totals.percentRows > 0 ? fillTemplate(t('{count} row(s) given as a percentage of basic are not included.'), { count: totals.percentRows }) : undefined}>
                      {t('Fixed monthly allowances (amounts only)')}
                    </th>
                    {grades.map(g => <td key={g.id} className="px-2 py-2 text-body-sm font-semibold tabular-nums text-fg">
                      <bdi>{totals.byGrade[g.id] === undefined ? '—' : money(totals.byGrade[g.id])}</bdi></td>)}
                  </tr>
                </tfoot>}
              </table>
            </div>
            {view.scrolls && !view.atEnd && <div data-testid="scroll-fade-end" aria-hidden
              className="pointer-events-none absolute inset-y-0 end-0 w-10 bg-gradient-to-l from-surface to-transparent rtl:bg-gradient-to-r" />}
          </div>;
        })()}

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

        {canManage && <div className="surface flex flex-wrap items-end justify-between gap-3 p-3">
          <div className="max-w-xl space-y-1 text-meta text-fg-muted">
            <p>{fillTemplate(t('Publishing for: {scope}. Values are never edited in place: the value in force ends the day before and the new one starts.'), { scope: companyLabel })}</p>
            {changed.length > 0 && effectiveFrom && <p className="font-medium text-fg">
              {fillTemplate(t('You are changing {count} value(s) for {grades} grade(s), starting {date}.'),
                { count: changed.length, grades: new Set(changed.map(d => d.gradeId)).size, date: effectiveFrom })}
            </p>}
            {changed.length > 0 && impact && <p role="status" className="font-medium text-fg">
              {fillTemplate(t('Reaches {now} employee(s) on that date and {later} at their next contract year.'), { now: impact.affectedNow, later: impact.affectedAtRenewal })}
              {(impact.superseded ?? 0) > 0 && <span className="block font-normal">{fillTemplate(t('{count} value(s) that have not started yet will be replaced on their start date.'), { count: impact.superseded ?? 0 })}</span>}
            </p>}
            {changed.length > 0 && impactError && <p role="alert" className="text-warning-fg">{impactError}</p>}
            {problems.length > 0 && <p className="text-danger-fg">{fillTemplate(t('{count} change(s) need correcting first.'), { count: problems.length })}</p>}
          </div>
          <div className="flex flex-wrap items-end gap-2">
            <label className="text-body">{t('Effective from')}
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

/** The benefit column's width and a grade column's minimum, in rem: six grades fit beside the app menu and Setup's menu at 1366 px. */
const FIRST_COLUMN_REM = 10.25;
const GRADE_COLUMN_REM = 6.5;


const chipTone: Record<RowChip['tone'], string> = {
  warning: 'border-warning/40 bg-warning-soft text-warning-fg',
  neutral: 'border-line bg-surface-sunken text-fg-muted',
  info: 'border-primary/30 bg-primary-soft text-primary-fg',
  success: 'border-success/30 bg-success-soft text-success-fg',
};

/**
 * One benefit: its name column (name, an `i`, at most one chip, a menu of the row's actions) and a cell for each grade. As a
 * table row (`layout="table"`) it is the sticky benefit column plus every grade; as a card (`layout="card"`, a phone) it is the
 * same header above the one grade picked.
 */
function MatrixRow({ layout, onlyGradeId, isNew, component: c, grades, drafts, original, gapKeys, offering, companyView, canManage, today, companyId, canRemove, onRemoved, onEdit, onMarkRest,
  onOfferingChanged, say, money, reasonText, componentName, serverError, editing, cellByKey, scheduled, canFinance, makerBlocked, financeOpen, onFinance }: {
  layout: 'table' | 'card'; onlyGradeId?: string; isNew: boolean;
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
  const [panel, setPanel] = useState<'offering' | 'remove' | null>(null);
  const skipped = offering?.mode === 'Skipped';
  const pending = isPendingFinance(c);
  const confirmedCash = isCashAllowance(c) && !pending && !!c.cashAllowance;
  const anyUnset = !pending && grades.some(g => drafts[cellKey(g.id, c.code)]?.state === 'unset' && !scheduled.has(cellKey(g.id, c.code)));
  const name = componentName(c);
  const chip = rowChip({ pending, mode: offering?.mode, companyView: companyView && !c.isLoanFacility, isNew, isCustom: !!c.isCustom });
  const tailored = companyView && offering?.mode === 'Tailored';

  // Every action of the row is in its menu; Finance's one visible step (Review and confirm) stays on the row.
  const actions: ActionMenuItem[] = [
    ...(canManage && companyView && offering?.canBeSkipped ? [{ key: 'offering', label: t(offering.offered ? 'Skip for this company' : 'Offer again in this company'), onSelect: () => setPanel('offering') }] : []),
    ...(canManage && !c.isFloor && !c.isLoanFacility && !skipped && anyUnset ? [{ key: 'rest', label: t('Mark the rest Not offered'), onSelect: onMarkRest }] : []),
    ...(confirmedCash && canFinance ? [{ key: 'correct', label: t('Correct GL / end of service'), onSelect: () => onFinance('correct') }] : []),
    ...(c.isCustom && canRemove ? [{ key: 'remove', label: t('Remove benefit'), onSelect: () => setPanel('remove') }] : []),
  ];

  const header = <div className="min-w-0 space-y-1">
    <div className="flex items-start gap-1">
      <span className="min-w-0 flex-1 break-words text-body font-medium leading-5 text-fg">
        {name}
        {isCashAllowance(c) && <span role="img" aria-label={t('Paid in salary')} title={t('Paid in salary')} className="ms-1 inline-block align-text-bottom text-success-fg"><Banknote className="h-3.5 w-3.5" aria-hidden="true" /></span>}
        {c.isFloor && <InfoTip className="ms-0.5" label={name} text={fillTemplate(t('Required by law: {title} {why}'), { title: reasonText(c.floorReason, 'title'), why: reasonText(c.floorReason, 'why') })} />}
      </span>
      {actions.length > 0 && <ActionMenu iconOnly label={fillTemplate(t('Actions for {benefit}'), { benefit: name })} items={actions} />}
    </div>
    {chip && <span data-testid="row-chip" title={t(chip.title)} className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-caption font-semibold ${chipTone[chip.tone]}`}>
      {chip.key === 'Waiting for Finance' && <Hourglass className="h-3 w-3" aria-hidden="true" />}{t(chip.key)}</span>}
    {pending && canFinance && !makerBlocked && <button type="button" className="btn-secondary btn-sm" aria-expanded={financeOpen === 'confirm'} onClick={() => onFinance('confirm')}>
      {t('Review and confirm')}<span className="sr-only"> {name}</span></button>}
    {pending && canFinance && makerBlocked && <span className="block text-meta text-fg-muted" data-testid="maker-cannot-confirm">
      {t('You added this allowance, so someone else in Finance must confirm it.')}</span>}
    {pending && !canFinance && <span className="block text-meta text-fg-muted">{t('Finance must confirm it before it can be used.')}</span>}
    {companyView && offering?.changesOn && <span className="block text-meta text-fg-muted">{fillTemplate(t(offering.offered ? 'Not offered from {date}' : 'Offered again from {date}'), { date: offering.changesOn })}</span>}
    {c.isLoanFacility && <Link href="/loans" className="inline-flex min-h-6 items-center text-meta font-semibold text-primary-fg underline">{t('Edit in Loans → Loan policies → Limits by grade')}</Link>}
    {panel === 'offering' && offering && <OfferingPanel code={c.code} name={name} companyId={companyId} offering={offering} today={today} onChanged={() => { setPanel(null); onOfferingChanged(); }} onClose={() => setPanel(null)} />}
    {panel === 'remove' && <RemovePanel code={c.code} name={name} onRemoved={onRemoved} onClose={() => setPanel(null)} />}
  </div>;

  const cellFor = (g: MatrixGrade) => {
    const key = cellKey(g.id, c.code);
    const d = drafts[key];
    if (!d) return null;
    const dirty = !!original[key] && JSON.stringify(original[key]) !== JSON.stringify(d);
    const problem = dirty ? draftProblem(d, c) : null;
    const refused = serverError(key);
    const gap = !pending && gapKeys.has(key) && d.state === 'unset';
    const startsOn = d.state === 'unset' ? scheduled.get(key) : undefined;
    const summary = pending ? t('Waiting for Finance') : c.isLoanFacility ? loanSummary(cellByKey.get(key), money, t)
      : startsOn ? fillTemplate(t('Starts {date}'), { date: startsOn }) : say(summarise(d, money));
    const view = cellView({ state: d.state, dirty, invalid: !!problem || !!refused, startsOn, pending, fixedElsewhere: c.isLoanFacility || skipped, canEdit: canManage });
    const value = [summary, gap && !dirty ? t('Needs a value') : '', dirty ? t('Changed — not published yet') : ''].filter(Boolean).join(', ');
    const label = fillTemplate(t(view.interactive && canManage ? '{benefit} for {grade}: {value}. Edit' : '{benefit} for {grade}: {value}'), { benefit: name, grade: g.code, value });
    const problemText = problem ? (problem === FLOOR_PROBLEM ? reasonText(c.floorReason, 'title') : t(problem)) : undefined;
    return <GridCell view={view} label={label} summary={summary} selected={editing === key} edited={dirty} problem={problemText}
      override={tailored && !!cellByKey.get(key)?.isCompanyOverride && !dirty} onOpen={() => onEdit(key)} />;
  };

  if (layout === 'card') {
    const g = grades.find(x => x.id === onlyGradeId) ?? grades[0];
    // The benefit on one side and its value on the other, so a phone shows a dozen benefits, not four.
    return <li className="flex items-start gap-2 p-2.5"><div className="min-w-0 flex-1">{header}</div><div className="w-[46%] shrink-0">{g && cellFor(g)}</div></li>;
  }
  return <tr className={`border-b border-line align-top last:border-b-0`}>
    <th scope="row" className="sticky start-0 z-10 w-[10.25rem] min-w-[10.25rem] max-w-[10.25rem] bg-surface px-2 py-1 text-start font-medium text-fg">{header}</th>
    {grades.map(g => <td key={g.id} className="p-1">{cellFor(g)}</td>)}
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
 * The guided path through the grid: ① grades, ② the benefits on offer, ③ a value for every grade, ④ review and publish. Every
 * step stays a status the admin can act on, never a gate. Beside it, what is true of the grid right now (values set, waiting
 * for Finance, unpublished changes, the next start), all from data the page already holds.
 */
function GuideStrip({ progress, changes, canManage, status, pendingNames, nextLabel, onNext }: {
  progress: { total: number; set: number; nextKey: string | null }; changes: number; canManage: boolean; status: StatusItem[];
  /** Cash allowances waiting for Finance: nothing can be set for them until it confirms. */
  pendingNames: string[];
  nextLabel: string; onNext: () => void;
}) {
  const { t } = useLocale();
  const valuesDone = progress.total > 0 && progress.set === progress.total;
  const current = !valuesDone ? 3 : changes > 0 ? 4 : 0;
  const steps: { n: number; label: string; done: boolean }[] = [
    { n: 1, label: t('Grades'), done: true },
    { n: 2, label: t('Choose benefits'), done: true },
    { n: 3, label: t('Set values'), done: valuesDone },
    { n: 4, label: t('Review & publish'), done: valuesDone && changes === 0 },
  ];
  const statusText = (item: StatusItem) => {
    switch (item.kind) {
      case 'values': return item.complete && changes === 0
        ? fillTemplate(t('All {total} values are set. Nothing to publish.'), { total: item.total })
        : fillTemplate(t('{set} of {total} values set'), { set: item.set, total: item.total });
      case 'finance': return fillTemplate(t('{count} waiting for Finance'), { count: item.count });
      case 'changes': return fillTemplate(t('{count} unpublished change(s)'), { count: item.count });
      case 'next': return fillTemplate(t('Next change starts {date}'), { date: item.date });
    }
  };
  return <div className="surface space-y-2 px-3 py-1.5">
    <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
      <nav aria-label={t('Steps to set benefits by grade')}>
        <ol className="flex flex-wrap gap-x-4 gap-y-1 text-body-sm">
          {steps.map(step => <li key={step.n} aria-current={step.n === current ? 'step' : undefined}
            className={`flex items-center gap-1.5 ${step.n === current ? 'font-semibold text-primary-fg' : 'text-fg'} ${step.n === (current || 4) ? '' : 'max-sm:hidden'}`}>
            <span aria-hidden="true" className={`grid h-5 w-5 place-items-center rounded-full text-meta ${step.done ? 'bg-success-soft text-success-fg' : step.n === current ? 'bg-primary text-on-primary' : 'bg-surface-sunken text-fg-muted'}`}>
              {step.done ? <Check className="h-3 w-3" /> : step.n}
            </span>
            <span>{step.label}</span>
          </li>)}
        </ol>
      </nav>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <ul data-testid="matrix-status" aria-live="polite" className="flex flex-wrap items-center gap-x-3 gap-y-1 text-meta text-fg-muted">
          {status.map(item => <li key={item.kind} className={item.kind === 'values' ? 'font-semibold text-fg' : item.kind === 'finance' ? 'text-warning-fg' : undefined}>
            {item.kind === 'finance' && <Hourglass className="me-1 inline h-3.5 w-3.5" aria-hidden="true" />}
            {item.kind === 'values' && item.complete && changes === 0 && <Check className="me-1 inline h-3.5 w-3.5 text-success-fg" aria-hidden="true" />}
            {statusText(item)}</li>)}
        </ul>
        {canManage && progress.nextKey && <button type="button" className={`${changes === 0 ? 'btn-primary' : 'btn-secondary'} btn-sm`} title={nextLabel} onClick={onNext}>{t('Set next value')}</button>}
      </div>
    </div>
    {pendingNames.length > 0 && <p className="flex items-start gap-2 border-t border-line pt-2 text-body text-warning-fg" data-testid="guide-waiting-for-finance">
      <Hourglass className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
      {t('{count, plural, one {Nothing can be set for {benefits} until Finance confirms it.} other {Nothing can be set for {benefits} until Finance confirms them.}}',
        { count: pendingNames.length, benefits: pendingNames.join(', ') })}
    </p>}
  </div>;
}

/**
 * Adds a benefit of the customer's own to the grid, in a dialog: a name (English, Arabic or both), then whether it is paid in
 * the monthly salary. A contract benefit is only recorded on the package; a monthly cash allowance needs the GL account its
 * expense posts to and an end of service choice (a category and a note). GOSI and WPS are shown as fixed. What the server refuses
 * is said next to its field. Added by someone without the Finance permission, a cash allowance waits for Finance to confirm it.
 */
function AddBenefitDialog({ onAdded, onClose }: { onAdded: (name: string, added: MatrixComponent) => Promise<void>; onClose: () => void }) {
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


  useEffect(() => {
    // The dialog takes focus on its close button; the name is what the person came to type.
    const frame = requestAnimationFrame(() => requestAnimationFrame(() => nameRef.current?.focus()));
    return () => cancelAnimationFrame(frame);
  }, []);

  return <Modal isOpen title={t('Add a benefit')} onClose={onClose} size="md" closeDisabled={busy} closeLabel={t('Close')}
    footer={<>
      <button type="button" className="btn-secondary" disabled={busy} onClick={onClose}>{t('Cancel')}</button>
      <button type="submit" form="add-benefit-form" className="btn-primary min-w-24" disabled={busy}>{t('Add benefit')}</button>
    </>}>
    <form id="add-benefit-form" onSubmit={e => void submit(e)} noValidate className="space-y-4">
      <p data-purpose className="text-body text-fg-muted">{t('Add a benefit that every grade can receive. You set its value for each grade next.')}</p>
    <div className="grid gap-3 sm:grid-cols-2">
      <p className="text-meta text-fg-muted sm:col-span-2">{t('Enter the name in English, Arabic or both.')}</p>
      <label className="text-body">{t('Benefit name (English)')}
        <input ref={nameRef} className="input mt-1 w-full" maxLength={100} dir="ltr" value={form.nameEn} aria-invalid={!!nameError} aria-describedby={nameError ? 'add-benefit-name-error' : undefined}
          onChange={e => set({ nameEn: e.target.value })} />
      </label>
      <label className="text-body">{t('Benefit name (Arabic)')}
        <input className="input mt-1 w-full" maxLength={100} dir="rtl" value={form.nameAr} aria-invalid={!!nameError} aria-describedby={nameError ? 'add-benefit-name-error' : undefined}
          onChange={e => set({ nameAr: e.target.value })} />
      </label>
      {nameError && <p id="add-benefit-name-error" role="alert" className="text-body text-danger-fg sm:col-span-2">{nameError}</p>}
    </div>

      <fieldset className="space-y-2">
        <legend className="flex items-center gap-1 text-body font-medium text-fg">{t('Is it paid in the monthly salary?')}
          <InfoTip label={t('Is it paid in the monthly salary?')} text={t('Choose cash only if it is paid every month in the salary. Cash allowances need a GL account and an end-of-service setting, and Finance confirms them before use.')} /></legend>
        <label className="flex items-start gap-2 text-body">
          <input type="radio" name="benefit-kind" className="mt-1" checked={!cash} onChange={() => set({ kind: 'Contract' })} />
          <span>{t('No, it’s a contract benefit (recorded on the employee’s package, not paid in salary)')}</span>
        </label>
        <label className="flex items-start gap-2 text-body">
          <input type="radio" name="benefit-kind" className="mt-1" checked={cash} onChange={() => set({ kind: 'Cash' })} />
          <span>{t('Yes, a monthly cash allowance paid in the salary')}</span>
        </label>
      </fieldset>
      {cash && <div className="space-y-3 rounded-xl border border-line p-3" data-testid="cash-allowance-fields">
        <GlAccountField idPrefix="add-benefit" gl={gl} value={form.glDriverKey} onChange={key => set({ glDriverKey: key })} error={glError} selectRef={glRef} onRetry={retry}
          forbiddenKey="You don’t have access to the GL accounts. Ask someone in Finance to add this allowance, or to give you access." />

        <dl className="grid gap-2 text-sm sm:grid-cols-2">
          <div>
            <dt className="text-meta font-semibold text-fg-muted">{t('GOSI (social insurance)')} <span className="font-normal text-fg-muted">· {t('Fixed')}</span></dt>
            <dd className="text-fg">{t('No — GOSI is calculated on basic + housing only')}</dd>
          </div>
          <div>
            <dt className="text-meta font-semibold text-fg-muted">{t('WPS (salary transfer)')} <span className="font-normal text-fg-muted">· {t('Fixed')}</span></dt>
            <dd className="text-fg">{t('Yes — paid in the salary transfer')}</dd>
          </div>
        </dl>

        <EosbFields idPrefix="add-benefit" value={form} onChange={patch => set(patch)} categoryError={shown('eosbCategory')} reasonError={shown('eosbReason')}
          categoryRef={categoryRef} reasonRef={reasonRef} />

        <p className="text-meta text-fg-muted">{t('Each employee’s amount is set on their salary and approved by Finance before it’s paid.')}</p>
      </div>}
      {generalError && <p role="alert" className="text-body text-danger-fg">{generalError}</p>}
    </form>
  </Modal>;
}


/** Takes away a benefit that was added by mistake. The server refuses once any grade has a value for it. */
function RemovePanel({ code, name, onRemoved, onClose }: { code: string; name: string; onRemoved: (name: string) => void; onClose: () => void }) {
  const { t } = useLocale();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const remove = async () => {
    setBusy(true); setError('');
    try { await entitlementsApi.removeBenefit(code); onRemoved(name); }
    catch (e) { setError(loanErrorMessage(e, t('Unable to remove the benefit.'))); setBusy(false); }
  };
  return <div className="space-y-2 rounded-lg border border-line p-2 text-meta font-normal">
    <p>{fillTemplate(t('Remove {benefit}? This works only while no grade has a value for it.'), { benefit: name })}</p>
    {error && <p role="alert" className="text-danger-fg">{error}</p>}
    <div className="flex gap-2">
      <button type="button" className="btn-primary btn-sm" disabled={busy} onClick={() => void remove()}>{t('Yes, remove it')}</button>
      <button type="button" className="btn-secondary btn-sm" disabled={busy} onClick={onClose}>{t('Keep it')}</button>
    </div>
  </div>;
}

/** Choose the month a company stops (or starts again) offering a benefit. */
function OfferingPanel({ code, name, companyId, offering, today, onChanged, onClose }: {
  code: string; name: string; companyId: string; offering: MatrixOffering; today: string; onChanged: () => void; onClose: () => void;
}) {
  const { t } = useLocale();
  const months = monthStarts(today, 12);
  const [from, setFrom] = useState(months[0]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const stop = offering.offered;
  const submit = async () => {
    setBusy(true); setError('');
    try {
      await entitlementsApi.setOffering({ companyId, componentCode: code, offered: !stop, effectiveFrom: from });
      onChanged();
    } catch (e) { setError(loanErrorMessage(e, t('Unable to change whether this company offers the benefit.'))); }
    finally { setBusy(false); }
  };
  return <div className="space-y-2 rounded-lg border border-line p-2 text-meta font-normal">
    <p>{fillTemplate(t(stop ? 'Choose the month this company stops offering {benefit}.' : 'Choose the month this company offers {benefit} again.'), { benefit: name })}</p>
    <select className="select w-full" value={from} onChange={e => setFrom(e.target.value)} aria-label={t('First month')}>
      {months.map(m => <option key={m} value={m}>{m}</option>)}
    </select>
    {stop && <p className="text-fg-muted">{t('Employees already in a contract year keep the package fixed for it. New contract years leave this benefit out.')}</p>}
    {error && <p role="alert" className="text-danger-fg">{error}</p>}
    <div className="flex gap-2">
      <button type="button" className="btn-primary btn-sm" disabled={busy} onClick={() => void submit()}>{t(stop ? 'Skip from that month' : 'Offer from that month')}</button>
      <button type="button" className="btn-secondary btn-sm" disabled={busy} onClick={onClose}>{t('Cancel')}</button>
    </div>
  </div>;
}

/**
 * The value of one grade for one benefit, in a drawer on the inline-end edge: the grid stays on screen behind it, and it can
 * never open off screen below a tall grid. Edits are made in the grid's draft; nothing is sent until Publish.
 */
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
  return <Modal isOpen placement="end" size="md" title={`${componentName(c)} — ${gradeName(grade)}`} onClose={onClose} closeLabel={t('Close')}
    footer={<>
      <button type="button" className="btn-secondary" onClick={onClose}>{t('Done editing')}</button>
      {onNext && !readOnly && <button type="button" className="btn-primary" disabled={!!problem} onClick={onNext}>{t('Save & next')}</button>}
    </>}>
    <div className="space-y-3">
      <p data-purpose className="text-body text-fg-muted">{t('Choose whether this grade gets it, and how much.')} <span className="text-meta">{t(groupKeys[c.group])}</span></p>
      {c.isFloor && <div className="rounded-lg border border-line bg-slate-50 p-2 text-xs  dark:bg-white/[0.03]">
        <p className="font-semibold">{reasonText(c.floorReason, 'title')}</p>
        <p className="text-fg-muted">{reasonText(c.floorReason, 'why')}</p>
      </div>}

      <fieldset disabled={readOnly} className="space-y-3">
        <div className="flex flex-wrap gap-3 text-sm" role="radiogroup" aria-label={t('Does this grade get it?')}>
          <label className="flex items-center gap-1.5"><input type="radio" checked={offered} onChange={() => setState('offered')} />{t('Offered')}</label>
          <label className={`flex items-center gap-1.5 ${c.isFloor ? 'text-fg-muted' : ''}`}>
            <input type="radio" checked={d.state === 'notOffered'} disabled={c.isFloor} onChange={() => setState('notOffered')} />{t('Not offered')}
          </label>
          {companyView && hasCompanyValue && <label className="flex items-center gap-1.5">
            <input type="radio" checked={d.state === 'groupDefault'} onChange={() => setState('groupDefault')} />{t('Use the group default')}
          </label>}
        </div>
        {c.isFloor && <p className="text-xs text-fg-muted">{reasonText(c.floorReason, 'fix')}</p>}

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

        {offered && <details className="rounded-lg border border-line p-2 text-sm " open={d.nationalityScope !== 'Any' || d.afterProbation || !!d.minServiceMonths}>
          <summary className="cursor-pointer text-xs font-semibold text-fg-muted">{t('Conditions (optional)')}</summary>
          <p className="mt-1 text-xs text-fg-muted">{t('Only months of service, the end of probation and — with a recorded legal basis — nationality can be conditions. Age, gender, marital status and disability never can.')}</p>
          {medical && <p className="mt-1 text-xs text-fg-muted">{t('Medical cover applies from the first day to every employee and their family, so it has no conditions.')}</p>}
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

      {problem && <p role="alert" className="text-sm text-danger-fg">{problem === FLOOR_PROBLEM ? reasonText(c.floorReason, 'fix') : t(problem)}</p>}
      {serverError && <p role="alert" className="text-sm text-danger-fg">{serverError.reason ? reasonText(serverError.reason, 'title') : serverError.message}</p>}
      {!readOnly && <div className="flex flex-wrap gap-2 border-t border-line pt-3">
        <button type="button" className="btn-secondary btn-sm" onClick={onSameForAll}>{t('Same for all grades')}</button>
        <button type="button" className="btn-secondary btn-sm" onClick={onUpward}>{fillTemplate(t('From {grade} upward'), { grade: grade.code })}</button>
        <button type="button" className="btn-secondary btn-sm" onClick={onReset}>{t('Undo changes to this grade')}</button>
      </div>}
      {!readOnly && <p className="text-meta text-fg-muted">{t('Saved in this grid. Not live until you publish.')}</p>}
    </div>
  </Modal>;
}
